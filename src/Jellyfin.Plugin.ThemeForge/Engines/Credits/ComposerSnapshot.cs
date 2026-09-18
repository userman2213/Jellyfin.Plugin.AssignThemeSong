using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>What was found out about who wrote a work's music.</summary>
/// <param name="Composers">The composers, most prominent first. Empty when none were found.</param>
/// <param name="Artist">The artist credited on the soundtrack release, when that is all there is.</param>
/// <param name="ReleaseGroupId">The MusicBrainz release group, when known, so a later look-up can skip a step.</param>
public sealed record ResearchedCredits(
    IReadOnlyList<string> Composers,
    string? Artist,
    string? ReleaseGroupId)
{
    /// <summary>Nothing was found.</summary>
    public static readonly ResearchedCredits None = new(Array.Empty<string>(), null, null);

    /// <summary>Gets a value indicating whether this says anything useful.</summary>
    public bool Any => Composers.Count > 0 || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>Gets every name this record offers, composers first.</summary>
    public IReadOnlyList<string> Names =>
        Composers.Count > 0
            ? Composers
            : string.IsNullOrWhiteSpace(Artist) ? Array.Empty<string>() : new[] { Artist };
}

/// <summary>One work's credits, as they were found and when.</summary>
public sealed class ComposerCredits
{
    /// <summary>Gets or sets the provider id this record answers for, such as <c>imdb:tt0407362</c>.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Gets or sets the composers.</summary>
    public List<string> Composers { get; set; } = new();

    /// <summary>Gets or sets the artist credited on the soundtrack, when no composer was named.</summary>
    public string? Artist { get; set; }

    /// <summary>Gets or sets the MusicBrainz release group, when one was found.</summary>
    public string? ReleaseGroupId { get; set; }

    /// <summary>Gets or sets which source answered, or where the question was last put.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets when the question was last asked.</summary>
    public DateTime LookedUpUtc { get; set; }

    /// <summary>Gets a value indicating whether anything was found.</summary>
    [JsonIgnore]
    public bool Found => Composers.Count > 0 || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>Reads this record back as credits.</summary>
    /// <returns>The credits.</returns>
    public ResearchedCredits AsCredits() => new(Composers, Artist, ReleaseGroupId);
}

/// <summary>
/// What research has established about who wrote the music, kept locally.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin records a composer for very few items, and without one the ladder's composer rung is
/// dropped, the composer bonus never pays, and an ordinary title such as <c>Lost</c> has nothing
/// to corroborate it. The answer is public and stable — a film's composer does not change — so it
/// is worth looking up once and keeping.
/// </para>
/// <para>
/// Misses are kept as carefully as hits. A quarter of any library has no composer recorded
/// anywhere, and without remembering that, those items would be asked about again on every scan,
/// which is most of the traffic this cache exists to avoid.
/// </para>
/// </remarks>
public sealed class ComposerSnapshot
{
    /// <summary>How long an answer is trusted before it is asked again.</summary>
    /// <remarks>A composer is a historical fact; only the record of it changes.</remarks>
    public static readonly TimeSpan HitLifetime = TimeSpan.FromDays(60);

    /// <summary>How long a "nobody knows" is trusted, which is shorter because the databases grow.</summary>
    public static readonly TimeSpan MissLifetime = TimeSpan.FromDays(14);

    private Dictionary<string, ComposerCredits>? _byKey;

    /// <summary>Gets or sets when the cache was last written.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets every record, one per provider id.</summary>
    /// <remarks>
    /// A work is stored once per id it is known by, so an item that later gains a TMDB id still
    /// finds its answer. The duplication is a few hundred bytes against a saved request.
    /// </remarks>
    public List<ComposerCredits> Entries { get; set; } = new();

    /// <summary>Gets a value indicating whether this cache holds anything worth consulting.</summary>
    [JsonIgnore]
    public bool IsUsable => Entries.Count > 0;

    /// <summary>Gets how old the cache is.</summary>
    [JsonIgnore]
    public TimeSpan Age => DateTime.UtcNow - UpdatedUtc;

    /// <summary>Gets how many records name at least one person.</summary>
    [JsonIgnore]
    public int Known => Entries.Count(entry => entry.Found);

    /// <summary>Finds what is known about a work, by any id it is known by.</summary>
    /// <param name="keys">The provider keys to try, best first.</param>
    /// <returns>The record, or null when the work has never been asked about.</returns>
    public ComposerCredits? Find(IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        _byKey ??= Entries
            .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (!string.IsNullOrEmpty(key) && _byKey.TryGetValue(key, out var found))
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Reports whether a work should be looked up again.</summary>
    /// <param name="keys">The provider keys to try.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><see langword="true"/> when nothing is known, or what is known has aged out.</returns>
    public bool NeedsLookUp(IEnumerable<string> keys, DateTime nowUtc)
    {
        var found = Find(keys);
        if (found is null)
        {
            return true;
        }

        return nowUtc - found.LookedUpUtc > (found.Found ? HitLifetime : MissLifetime);
    }

    /// <summary>Records what a source answered, replacing anything held for the same keys.</summary>
    /// <param name="keys">Every id this work is known by.</param>
    /// <param name="credits">What was found, which may be nothing.</param>
    /// <param name="source">Which source answered, or which was asked last.</param>
    /// <param name="nowUtc">The current time.</param>
    public void Record(IReadOnlyList<string> keys, ResearchedCredits credits, string source, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(credits);

        foreach (var key in keys.Where(key => !string.IsNullOrEmpty(key)))
        {
            Entries.RemoveAll(entry => string.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase));
            Entries.Add(new ComposerCredits
            {
                Key = key,
                Composers = credits.Composers.ToList(),
                Artist = credits.Artist,
                ReleaseGroupId = credits.ReleaseGroupId,
                Source = source,
                LookedUpUtc = nowUtc,
            });
        }

        _byKey = null;
    }
}

/// <summary>Turns an item's provider ids into the keys the cache is stored under.</summary>
public static class CreditsKeys
{
    /// <summary>Builds the keys for one work, most reliable first.</summary>
    /// <param name="imdbId">The IMDb id, when known.</param>
    /// <param name="tmdbId">The TMDB id, when known.</param>
    /// <param name="tvdbId">The TheTVDB id, when known.</param>
    /// <param name="isSeries">Whether the work is a series, since TMDB numbers films and shows separately.</param>
    /// <returns>The keys, which may be empty for an item with no ids at all.</returns>
    public static IReadOnlyList<string> For(string? imdbId, string? tmdbId, string? tvdbId, bool isSeries)
    {
        var keys = new List<string>(3);

        // IMDb first: it is the id both of the sources are keyed on, and the only one that is
        // unambiguous across the film and television halves of every database.
        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            keys.Add("imdb:" + imdbId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tmdbId))
        {
            keys.Add((isSeries ? "tmdbtv:" : "tmdb:") + tmdbId.Trim());
        }

        if (!string.IsNullOrWhiteSpace(tvdbId))
        {
            keys.Add("tvdb:" + tvdbId.Trim());
        }

        return keys;
    }
}
