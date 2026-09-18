using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>The local record of who wrote what.</summary>
public interface IComposerCatalogue
{
    /// <summary>Gets the cache in memory, loading it from disk on first use.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache, which may hold nothing if nothing has been looked up yet.</returns>
    Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the works that are not already answered for, and saves what comes back.
    /// </summary>
    /// <param name="works">Everything worth knowing about.</param>
    /// <param name="progress">Progress reporter, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache as it now stands.</returns>
    Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what is already known about one work, without asking anybody.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. Identity is resolved synchronously all through the pipeline, and
    /// this has to be answerable from there; the cache is loaded once and read from memory, which
    /// is the same bargain the ThemerrDB mirror makes.
    /// </remarks>
    /// <param name="keys">The provider keys for the work.</param>
    /// <returns>What is known, or nothing.</returns>
    ResearchedCredits Known(IReadOnlyList<string> keys);
}

/// <summary>
/// Keeps what has been found out about who wrote each work's music.
/// </summary>
/// <remarks>
/// Built on the same bargain as the ThemerrDB mirror: ask once, keep the answer beside the index
/// so it survives upgrades, read it from memory thereafter, and treat a failed refresh as a reason
/// to keep yesterday's copy rather than to lose it.
/// </remarks>
public sealed class ComposerCatalogue : IComposerCatalogue
{
    private readonly IReadOnlyList<ICreditsSource> _sources;
    private readonly IThemeForgeLogger<ComposerCatalogue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the first read from disk only, which is why it is not the sync gate.</summary>
    /// <remarks>
    /// <see cref="Known"/> is called from identity resolution, on whatever thread is serving a
    /// page. Were it to wait on <see cref="_gate"/> it would block behind a MusicBrainz pass that
    /// can run for minutes. Loading a file is short and has its own lock.
    /// </remarks>
    private readonly object _load = new();

    private volatile ComposerSnapshot? _snapshot;

    /// <summary>Initializes a new instance of the <see cref="ComposerCatalogue"/> class.</summary>
    /// <param name="sources">The databases to ask, in whatever order they declare.</param>
    /// <param name="logger">Logger.</param>
    public ComposerCatalogue(IEnumerable<ICreditsSource> sources, IThemeForgeLogger<ComposerCatalogue> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // Ordered here, once, rather than depending on the order services were registered in.
        _sources = sources.OrderBy(source => source.Order).ToList();
        _logger = logger;
    }

    /// <summary>Gets where the cache is kept, beside the index so it survives plugin upgrades.</summary>
    public static string SnapshotPath =>
        Path.Combine(Plugin.Instance?.DataPath ?? Path.GetTempPath(), "composers.json");

    /// <inheritdoc />
    public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Loaded());
    }

    /// <inheritdoc />
    public ResearchedCredits Known(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return ResearchedCredits.None;
        }

        var found = Loaded().Find(keys);
        return found?.AsCredits() ?? ResearchedCredits.None;
    }

    /// <summary>Returns the cache, reading it from disk the first time anybody asks.</summary>
    private ComposerSnapshot Loaded()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_load)
        {
            return _snapshot ??= Read() ?? new ComposerSnapshot();
        }
    }

    /// <inheritdoc />
    public async Task<ComposerSnapshot> SyncAsync(
        IReadOnlyList<CreditsRequest> works,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(works);

        var configuration = Plugin.Config;
        var snapshot = await GetAsync(cancellationToken).ConfigureAwait(false);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var outstanding = works.Where(work => snapshot.NeedsLookUp(work.Keys, now)).ToList();

            if (outstanding.Count == 0)
            {
                _logger.LogInformation("ThemeForge: every title's music credits are already known; nothing to look up.");
                progress?.Report(100);
                return snapshot;
            }

            _logger.LogInformation(
                "ThemeForge: looking up the music credits for {Count} of {Total} titles.",
                outstanding.Count,
                works.Count);

            var enabled = _sources.Where(source => source.IsEnabled(configuration)).ToList();
            var share = enabled.Count == 0 ? 0 : 100.0 / enabled.Count;
            var answered = 0;

            for (var index = 0; index < enabled.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = enabled[index];
                if (outstanding.Count == 0)
                {
                    break;
                }

                var start = share * index;
                var step = new Progress<double>(within => progress?.Report(start + (within * share / 100.0)));

                IReadOnlyDictionary<string, ResearchedCredits> found;
                try
                {
                    found = await source.LookUpAsync(outstanding, configuration, step, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One database being unreachable is not the whole job's failure.
                    _logger.LogWarning(ex, "ThemeForge: {Source} could not be asked about music credits.", source.Name);
                    continue;
                }

                // A record naming nobody is not an answer. Wikidata often knows a work's
                // soundtrack release without knowing who wrote it, and that release group is
                // precisely what lets MusicBrainz answer in one request instead of two -- so it
                // is carried forward rather than filed as "looked up, nothing there".
                var named = outstanding.Where(work => found.TryGetValue(work.Key, out var credits) && credits.Any).ToList();
                foreach (var work in named)
                {
                    snapshot.Record(work.Keys, found[work.Key], source.Name, now);
                    answered++;
                }

                _logger.LogInformation(
                    "ThemeForge: {Source} named the music for {Found} of {Asked} titles.",
                    source.Name,
                    named.Count,
                    outstanding.Count);

                outstanding = outstanding
                    .Where(work => !found.TryGetValue(work.Key, out var credits) || !credits.Any)
                    .Select(work => found.TryGetValue(work.Key, out var lead) && lead.ReleaseGroupId is { } group
                        ? work with { ReleaseGroupId = group }
                        : work)
                    .ToList();
            }

            // Everything still unanswered is recorded as such, so it is not asked about again
            // tomorrow. This is most of the traffic the cache exists to avoid.
            foreach (var work in outstanding)
            {
                snapshot.Record(work.Keys, ResearchedCredits.None, "nobody", now);
            }

            snapshot.UpdatedUtc = now;
            Write(snapshot);
            _snapshot = snapshot;

            _logger.LogInformation(
                "ThemeForge: found the music credits for {Answered} titles; {Unknown} are not recorded anywhere and will not be asked about again for a fortnight.",
                answered,
                outstanding.Count);

            progress?.Report(100);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ComposerSnapshot? Read()
    {
        try
        {
            var path = SnapshotPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<ComposerSnapshot>(File.ReadAllText(path));
            if (snapshot is not null)
            {
                _logger.LogInformation(
                    "ThemeForge: the music credits cache names somebody for {Known} of {Total} titles, read {Age:0} hours ago.",
                    snapshot.Known,
                    snapshot.Entries.Count,
                    snapshot.Age.TotalHours);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "ThemeForge: the music credits cache could not be read and will be rebuilt.");
            return null;
        }
    }

    private void Write(ComposerSnapshot snapshot)
    {
        try
        {
            var path = SnapshotPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory regardless, so the current session still benefits.
            _logger.LogWarning(ex, "ThemeForge: could not save the music credits cache.");
        }
    }
}
