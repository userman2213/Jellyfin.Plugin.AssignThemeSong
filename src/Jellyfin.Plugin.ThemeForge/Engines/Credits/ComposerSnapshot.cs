using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>The theme itself: what it is called, and who performs it.</summary>
/// <param name="Title">The song or piece, as credited.</param>
/// <param name="Performer">Who performs it, when exactly one performer is named; otherwise null.</param>
public sealed record ThemeSong(string Title, string? Performer)
{
    /// <summary>Gets the words to search for: the song, and its performer when known.</summary>
    public string SearchText => string.IsNullOrWhiteSpace(Performer) ? Title : Title + " " + Performer;

    /// <inheritdoc />
    public override string ToString() =>
        string.IsNullOrWhiteSpace(Performer) ? $"“{Title}”" : $"“{Title}” by {Performer}";
}

/// <summary>What was found out about a work's music: who wrote it, and what its theme is called.</summary>
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

    /// <summary>Gets the theme song, when one is known.</summary>
    public ThemeSong? Theme { get; init; }

    /// <summary>Gets who wrote the theme, which is often not who scored the work.</summary>
    /// <remarks>Dexter's theme is Rolfe Kent's; its score is Daniel Licht's.</remarks>
    public IReadOnlyList<string> ThemeComposers { get; init; } = Array.Empty<string>();

    /// <summary>Gets the English Wikipedia article about the work, when there is one.</summary>
    /// <remarks>A lead rather than an answer: it is how the infobox is found.</remarks>
    public string? WikipediaTitle { get; init; }

    /// <summary>Gets a value indicating whether this names somebody who wrote the music.</summary>
    public bool Any => Composers.Count > 0 || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>Gets a value indicating whether anything is known about the theme.</summary>
    public bool KnowsTheme => Theme is not null || ThemeComposers.Count > 0;

    /// <summary>Gets every name this record offers for who wrote the music, composers first.</summary>
    public IReadOnlyList<string> Names =>
        Composers.Count > 0
            ? Composers
            : string.IsNullOrWhiteSpace(Artist) ? Array.Empty<string>() : new[] { Artist };
}

/// <summary>One work's credits, as they were found and when.</summary>
/// <remarks>
/// Two questions are answered here, and each has its own date, because they are answered by
/// different sources and go stale separately. The composer fields keep the names they had in 2.5,
/// so a cache written by 2.5 still loads: its composers stand, and its theme question, which has no
/// date yet, is asked once.
/// </remarks>
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

    /// <summary>Gets or sets which source named the composer, or <c>nobody</c>.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets when the composer question was last settled; the default means never.</summary>
    public DateTime LookedUpUtc { get; set; }

    /// <summary>Gets or sets the theme song's title.</summary>
    public string? ThemeTitle { get; set; }

    /// <summary>Gets or sets who performs the theme song.</summary>
    public string? ThemePerformer { get; set; }

    /// <summary>Gets or sets who wrote the theme.</summary>
    public List<string> ThemeComposers { get; set; } = new();

    /// <summary>Gets or sets which source named the theme, or <c>nobody</c>.</summary>
    public string? ThemeSource { get; set; }

    /// <summary>Gets or sets when the theme question was last settled; null means never.</summary>
    public DateTime? ThemeLookedUpUtc { get; set; }

    /// <summary>Gets or sets the English Wikipedia article about the work.</summary>
    public string? WikipediaTitle { get; set; }

    /// <summary>Gets a value indicating whether somebody is named as having written the music.</summary>
    [JsonIgnore]
    public bool Found => Composers.Count > 0 || !string.IsNullOrWhiteSpace(Artist);

    /// <summary>Gets a value indicating whether anything is known about the theme.</summary>
    [JsonIgnore]
    public bool ThemeFound => !string.IsNullOrWhiteSpace(ThemeTitle) || ThemeComposers.Count > 0;

    /// <summary>Reads this record back as credits.</summary>
    /// <returns>The credits.</returns>
    public ResearchedCredits AsCredits() => new(Composers, Artist, ReleaseGroupId)
    {
        Theme = string.IsNullOrWhiteSpace(ThemeTitle) ? null : new ThemeSong(ThemeTitle, ThemePerformer),
        ThemeComposers = ThemeComposers,
        WikipediaTitle = WikipediaTitle,
    };

    /// <summary>Copies this record, so a copy can be changed while the original is being read.</summary>
    /// <returns>The copy.</returns>
    public ComposerCredits Clone()
    {
        var copy = (ComposerCredits)MemberwiseClone();
        copy.Composers = Composers.ToList();
        copy.ThemeComposers = ThemeComposers.ToList();
        return copy;
    }
}

/// <summary>
/// What research has established about a work's music, kept locally.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin records a composer for very few items, and without one the ladder's composer rung is
/// dropped, the composer bonus never pays, and an ordinary title such as <c>Lost</c> has nothing
/// to corroborate it. The answer is public and stable — a film's composer does not change — so it
/// is worth looking up once and keeping. The same goes for what the theme is called.
/// </para>
/// <para>
/// Misses are kept as carefully as hits. A quarter of any library has no composer recorded
/// anywhere, and without remembering that, those items would be asked about again on every scan,
/// which is most of the traffic this cache exists to avoid. A question that could not be asked —
/// the service was down, or refused — is not a miss, and is not recorded as one.
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

    /// <summary>Gets how many records name at least one person who wrote the music.</summary>
    [JsonIgnore]
    public int Known => Entries.Count(entry => entry.Found);

    /// <summary>Gets how many records know something about the theme.</summary>
    [JsonIgnore]
    public int ThemesKnown => Entries.Count(entry => entry.ThemeFound);

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

    /// <summary>Reports whether either question about a work should be asked again.</summary>
    /// <param name="keys">The provider keys to try.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><see langword="true"/> when something is unknown or has aged out.</returns>
    public bool NeedsLookUp(IEnumerable<string> keys, DateTime nowUtc)
    {
        var list = keys as IReadOnlyCollection<string> ?? keys.ToList();
        return NeedsComposers(list, nowUtc) || NeedsTheme(list, nowUtc);
    }

    /// <summary>Reports whether who wrote a work's music should be asked again.</summary>
    /// <param name="keys">The provider keys to try.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><see langword="true"/> when it was never settled, or the answer has aged out.</returns>
    public bool NeedsComposers(IEnumerable<string> keys, DateTime nowUtc)
    {
        var found = Find(keys);
        if (found is null || found.LookedUpUtc == default)
        {
            return true;
        }

        return nowUtc - found.LookedUpUtc > (found.Found ? HitLifetime : MissLifetime);
    }

    /// <summary>Reports whether what a work's theme is called should be asked again.</summary>
    /// <param name="keys">The provider keys to try.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns><see langword="true"/> when it was never settled, or the answer has aged out.</returns>
    public bool NeedsTheme(IEnumerable<string> keys, DateTime nowUtc)
    {
        var found = Find(keys);
        if (found?.ThemeLookedUpUtc is not { } asked)
        {
            return true;
        }

        return nowUtc - asked > (found.ThemeFound ? HitLifetime : MissLifetime);
    }

    /// <summary>Records who wrote a work's music, leaving what is known about its theme alone.</summary>
    /// <param name="keys">Every id this work is known by.</param>
    /// <param name="credits">What was found, which may be nothing.</param>
    /// <param name="source">Which source answered, or <c>nobody</c>.</param>
    /// <param name="nowUtc">The current time.</param>
    public void RecordComposers(IReadOnlyList<string> keys, ResearchedCredits credits, string source, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(credits);

        foreach (var entry in EntriesFor(keys))
        {
            entry.Composers = credits.Composers.ToList();
            entry.Artist = credits.Artist;
            entry.Source = source;
            entry.LookedUpUtc = nowUtc;
            Leads(entry, credits);
        }
    }

    /// <summary>Records what a work's theme is called, leaving who wrote its music alone.</summary>
    /// <param name="keys">Every id this work is known by.</param>
    /// <param name="credits">What was found, which may be nothing.</param>
    /// <param name="source">Which source answered, or <c>nobody</c>.</param>
    /// <param name="nowUtc">The current time.</param>
    public void RecordTheme(IReadOnlyList<string> keys, ResearchedCredits credits, string source, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(credits);

        foreach (var entry in EntriesFor(keys))
        {
            entry.ThemeTitle = credits.Theme?.Title;
            entry.ThemePerformer = credits.Theme?.Performer;
            entry.ThemeComposers = credits.ThemeComposers.ToList();
            entry.ThemeSource = source;
            entry.ThemeLookedUpUtc = nowUtc;
            Leads(entry, credits);
        }
    }

    /// <summary>Copies the whole cache, so a sync can change the copy while searches read the original.</summary>
    /// <returns>The copy.</returns>
    public ComposerSnapshot Clone() => new()
    {
        UpdatedUtc = UpdatedUtc,
        Entries = Entries.Select(entry => entry.Clone()).ToList(),
    };

    private static void Leads(ComposerCredits entry, ResearchedCredits credits)
    {
        entry.ReleaseGroupId = credits.ReleaseGroupId ?? entry.ReleaseGroupId;
        entry.WikipediaTitle = credits.WikipediaTitle ?? entry.WikipediaTitle;
    }

    /// <summary>Finds or makes the record for each id, one per id.</summary>
    private List<ComposerCredits> EntriesFor(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var entries = new List<ComposerCredits>();
        foreach (var key in keys.Where(key => !string.IsNullOrEmpty(key)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var entry = Entries.Find(existing => string.Equals(existing.Key, key, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                entry = new ComposerCredits { Key = key };
                Entries.Add(entry);
            }

            entries.Add(entry);
        }

        _byKey = null;
        return entries;
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
