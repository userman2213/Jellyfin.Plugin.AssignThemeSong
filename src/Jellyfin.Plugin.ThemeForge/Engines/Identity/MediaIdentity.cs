using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>
/// Everything the rest of the pipeline needs to know about a library item, resolved
/// once up front so that no downstream engine has to touch a <c>BaseItem</c>.
/// </summary>
public sealed class MediaIdentity
{
    /// <summary>Gets the Jellyfin item id.</summary>
    public required Guid ItemId { get; init; }

    /// <summary>Gets the item's display title as Jellyfin knows it.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the title reduced to a comparable form. See <see cref="TitleNormalizer"/>.</summary>
    public required string NormalizedTitle { get; init; }

    /// <summary>Gets the original-language title, when it differs from <see cref="Title"/>.</summary>
    public string? OriginalTitle { get; init; }

    /// <summary>Gets additional titles worth searching under, already normalized and de-duplicated.</summary>
    public IReadOnlyList<string> AlternateTitles { get; init; } = Array.Empty<string>();

    /// <summary>Gets the production year, when known.</summary>
    public int? Year { get; init; }

    /// <summary>
    /// Gets the composers Jellyfin has on record for the item. Empty when it has none.
    /// </summary>
    /// <remarks>
    /// The one name that resolves a soundtrack upload titled by track rather than by show, and a
    /// far more specific thing to search for than the title alone.
    /// </remarks>
    public IReadOnlyList<string> Composers { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets everybody else credited on the item's music -- lyricist, conductor, arranger.
    /// </summary>
    /// <remarks>
    /// Scored, never searched for. An upload naming the conductor of this score is talking about
    /// this work's music and deserves a nod; a query built from a lyricist's name would return
    /// the songs they wrote for everybody else.
    /// </remarks>
    public IReadOnlyList<string> MusicCredits { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets what the item's theme is called, and who performs it, when that is known.
    /// </summary>
    /// <remarks>
    /// The most specific search there is for a show whose theme is a song: "The Sopranos Woke Up
    /// This Morning Alabama 3" finds what "The Sopranos theme song" only sometimes does. It is also
    /// what lets an upload titled by song and artist alone -- which never names the show -- be
    /// recognised as this show's theme rather than rejected for not naming it.
    /// </remarks>
    public Credits.ThemeSong? Theme { get; init; }

    /// <summary>Gets the kind of item — only <see cref="BaseItemKind.Movie"/> and <see cref="BaseItemKind.Series"/> are processed.</summary>
    public required BaseItemKind Kind { get; init; }

    /// <summary>Gets a value indicating whether this is a series rather than a film. Series themes are shorter and scored differently.</summary>
    public bool IsSeries => Kind == BaseItemKind.Series;

    /// <summary>Gets the TheTVDB id, when known.</summary>
    public string? TvdbId { get; init; }

    /// <summary>Gets the TMDB id, when known.</summary>
    public string? TmdbId { get; init; }

    /// <summary>Gets the IMDb id, when known.</summary>
    public string? ImdbId { get; init; }

    /// <summary>
    /// Gets a stable key that survives a library rebuild. Jellyfin item ids are regenerated
    /// when a library is removed and re-added, so the index needs an identity anchored to
    /// the metadata providers instead.
    /// </summary>
    public string StableKey =>
        !string.IsNullOrEmpty(TvdbId) ? $"tvdb:{TvdbId}"
        : !string.IsNullOrEmpty(TmdbId) ? $"tmdb:{TmdbId}"
        : !string.IsNullOrEmpty(ImdbId) ? $"imdb:{ImdbId}"
        : $"title:{NormalizedTitle}:{Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}";

    /// <summary>Gets a short human-readable label used in logs and the review queue.</summary>
    public string Label => Year is null ? Title : $"{Title} ({Year})";
}
