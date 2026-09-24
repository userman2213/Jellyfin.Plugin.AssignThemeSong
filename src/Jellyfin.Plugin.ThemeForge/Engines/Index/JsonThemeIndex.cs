using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Index;

/// <summary>
/// The index, held in memory and persisted as a single JSON document.
/// </summary>
/// <remarks>
/// <para>
/// A JSON document rather than a database: the whole index is read on startup and consulted
/// constantly during a run, so it wants to be in memory anyway, and at the size of even a very
/// large library it is a few megabytes. Adding a database engine would mean shipping an
/// assembly that can conflict with the host's own, which is a real risk in a Jellyfin plugin
/// and buys nothing at this scale.
/// </para>
/// <para>
/// Saves are written to a temporary file and moved into place, so an interrupted write leaves
/// the previous index intact rather than a half-written one.
/// </para>
/// </remarks>
public sealed class JsonThemeIndex : IThemeIndex, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ConcurrentDictionary<Guid, ThemeIndexEntry> _entries = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly IThemeForgeLogger<JsonThemeIndex> _logger;
    private readonly string? _overridePath;

    private int _dirty;

    /// <summary>Initializes a new instance of the <see cref="JsonThemeIndex"/> class.</summary>
    /// <param name="logger">Logger.</param>
    /// <param name="indexPath">
    /// Where to store the index. Left null in normal use so the location follows the plugin's
    /// data directory; supplied by tests so they do not share one file.
    /// </param>
    public JsonThemeIndex(IThemeForgeLogger<JsonThemeIndex> logger, string? indexPath = null)
    {
        _logger = logger;
        _overridePath = indexPath;
    }

    /// <summary>Gets the path the index is stored at.</summary>
    private string IndexPath =>
        _overridePath
        ?? Plugin.Instance?.IndexPath
        ?? Path.Combine(Path.GetTempPath(), "themeforge-index.json");

    /// <inheritdoc />
    public ThemeIndexEntry? Get(Guid itemId) => _entries.TryGetValue(itemId, out var entry) ? entry : null;

    /// <inheritdoc />
    public ThemeIndexEntry? Resolve(Guid itemId, string stableKey)
    {
        if (_entries.TryGetValue(itemId, out var entry))
        {
            return entry;
        }

        if (string.IsNullOrEmpty(stableKey))
        {
            return null;
        }

        // Item ids are regenerated when a library is removed and re-added. Re-keying the match
        // under the new id keeps the history — including a human's decisions — attached.
        var matched = _entries.Values.FirstOrDefault(e => string.Equals(e.StableKey, stableKey, StringComparison.Ordinal));
        if (matched is null)
        {
            return null;
        }

        _entries.TryRemove(matched.ItemId, out _);
        matched.ItemId = itemId;
        _entries[itemId] = matched;
        MarkDirty();

        _logger.LogDebug("ThemeForge: re-keyed the index entry for \"{Label}\" onto its new item id.", matched.Label);
        return matched;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ThemeIndexEntry> All() => _entries.Values.ToList();

    /// <inheritdoc />
    public IReadOnlyList<ThemeIndexEntry> ReviewQueue() =>
        _entries.Values
            .Where(entry => entry.State == ThemeItemState.PendingReview)
            .OrderByDescending(entry => entry.Score ?? 0)
            .ToList();

    /// <inheritdoc />
    public void Put(ThemeIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        entry.UpdatedUtc = DateTime.UtcNow;
        _entries[entry.ItemId] = entry;
        MarkDirty();
    }

    /// <inheritdoc />
    public bool Remove(Guid itemId)
    {
        var removed = _entries.TryRemove(itemId, out _);
        if (removed)
        {
            MarkDirty();
        }

        return removed;
    }

    /// <inheritdoc />
    public string? FindAssignmentOwner(string videoId, Guid excludingItem)
    {
        if (string.IsNullOrEmpty(videoId))
        {
            return null;
        }

        return _entries.Values
            .FirstOrDefault(entry =>
                entry.ItemId != excludingItem
                && string.Equals(entry.ChosenId, videoId, StringComparison.Ordinal)
                && entry.State is ThemeItemState.AutoAssigned or ThemeItemState.Approved or ThemeItemState.ManualOverride)
            ?.Label;
    }

    /// <inheritdoc />
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var path = IndexPath;
        if (!File.Exists(path))
        {
            _logger.LogInformation("ThemeForge: no index found at {Path}; starting fresh.", path);
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer
                .DeserializeAsync<List<ThemeIndexEntry>>(stream, SerializerOptions, cancellationToken)
                .ConfigureAwait(false);

            _entries.Clear();
            foreach (var entry in entries ?? new List<ThemeIndexEntry>())
            {
                _entries[entry.ItemId] = entry;
            }

            var repaired = RepairScannerLatches();
            var dryRunArtefacts = ClearDryRunArtefacts();
            _logger.LogInformation("ThemeForge: loaded {Count} index entries.", _entries.Count);

            if (repaired > 0)
            {
                _logger.LogInformation(
                    "ThemeForge: released {Count} items that an earlier version had marked as manually overridden merely because a theme file was present. They will be reconsidered against the current library rules.",
                    repaired);
                MarkDirty();
            }

            if (dryRunArtefacts > 0)
            {
                _logger.LogInformation(
                    "ThemeForge: cleared {Count} review-queue entries that a dry run had left behind. A dry run records nothing now.",
                    dryRunArtefacts);
                MarkDirty();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // A corrupt index must not stop the plugin loading. It is a cache of decisions, and
            // the worst case of losing it is that the next run re-examines the library.
            _logger.LogError(ex, "ThemeForge: the index at {Path} could not be read and will be rebuilt.", path);
            TryPreserveCorruptIndex(path);
            _entries.Clear();
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _dirty, 0) == 0)
        {
            return;
        }

        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var path = IndexPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporaryPath = path + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer
                    .SerializeAsync(stream, _entries.Values.OrderBy(e => e.Label, StringComparer.Ordinal).ToList(), SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
            _logger.LogDebug("ThemeForge: saved {Count} index entries.", _entries.Count);
        }
        catch (Exception ex)
        {
            // Put the flag back so the next flush retries rather than silently dropping changes.
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogError(ex, "ThemeForge: could not save the index.");
        }
        finally
        {
            _saveGate.Release();
        }
    }

    /// <summary>
    /// Flushes any pending changes before releasing the file lock.
    /// </summary>
    /// <remarks>
    /// Without this, an orderly host shutdown outside a run silently discarded every decision
    /// made since the last flush — an approval or a lock set from the settings page moments
    /// before a restart would simply be gone.
    /// </remarks>
    public void Dispose()
    {
        try
        {
            FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ThemeForge: could not save the index while shutting down.");
        }

        _saveGate.Dispose();
    }

    private void MarkDirty() => Interlocked.Exchange(ref _dirty, 1);

    /// <summary>
    /// Undoes the state an earlier version wrote when it merely observed an existing theme file.
    /// </summary>
    /// <remarks>
    /// That version set <see cref="ThemeItemState.ManualOverride"/> on any item that already had a
    /// theme, which is checked before the library's overwrite policy and so made the policy
    /// permanently unreachable for those items. A genuine manual assignment always records a
    /// <see cref="ThemeIndexEntry.ThemePath"/>; the latch never did, which is what tells them
    /// apart. Everything else about the entry is preserved.
    /// </remarks>
    /// <returns>How many entries were released.</returns>
    private int RepairScannerLatches()
    {
        var repaired = 0;

        foreach (var entry in _entries.Values)
        {
            if (entry.State != ThemeItemState.ManualOverride || entry.ThemePath is not null)
            {
                continue;
            }

            entry.State = ThemeItemState.Unprocessed;
            entry.LastSkipReason = null;
            entry.LastSkipUtc = null;
            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// Removes review-queue entries that an earlier version's dry run left behind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// That version returned early from the assignment step when the run was a dry run, and did so
    /// by marking the item as awaiting review — but the winning candidate and its score had
    /// already been written. The queue therefore filled with items the run would have assigned
    /// outright, showing the full score they earned, sorted above every genuine suggestion, and
    /// they survived dry run being switched off.
    /// </para>
    /// <para>
    /// They are identifiable exactly. A genuine review entry scores <i>below</i> the auto-assign
    /// threshold, because that is the only way the decision policy produces one. An entry queued
    /// because its audio was ambiguous has been through acquisition, so its attempt count is at
    /// least one. An entry that is awaiting review, has never been attempted, and scores at or
    /// above the threshold can only have come from that early return.
    /// </para>
    /// </remarks>
    /// <returns>How many entries were cleared.</returns>
    private int ClearDryRunArtefacts()
    {
        var configuration = Plugin.Config;
        var autoAssign = Math.Max(configuration.AutoAssignThreshold, configuration.ReviewThreshold);
        var cleared = 0;

        foreach (var entry in _entries.Values)
        {
            if (entry.State != ThemeItemState.PendingReview
                || entry.Attempts > 0
                || entry.Score is not { } score
                || score < autoAssign)
            {
                continue;
            }

            entry.State = ThemeItemState.Unprocessed;
            entry.LastSkipReason = null;
            entry.LastSkipUtc = null;
            cleared++;
        }

        return cleared;
    }

    /// <summary>Keeps a corrupt index aside so it can be inspected rather than silently lost.</summary>
    private void TryPreserveCorruptIndex(string path)
    {
        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not set the corrupt index aside.");
        }
    }
}
