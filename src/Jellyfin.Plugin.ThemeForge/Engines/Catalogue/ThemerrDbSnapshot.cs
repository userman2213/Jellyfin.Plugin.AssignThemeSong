using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Catalogue;

/// <summary>One title in the catalogue, as the index lists it.</summary>
/// <param name="Id">The TMDB id.</param>
/// <param name="Title">The title, used only when an item has no id to look up by.</param>
public sealed record CatalogueTitle(string Id, string Title);

/// <summary>
/// One film collection, with the films it contains and the theme chosen for it.
/// </summary>
/// <param name="Id">The collection's TMDB id.</param>
/// <param name="Title">The collection's name.</param>
/// <param name="ThemeUrl">The theme chosen for the collection as a whole.</param>
/// <param name="MemberIds">The TMDB ids of the films in it.</param>
public sealed record CatalogueCollection(
    string Id,
    string Title,
    string ThemeUrl,
    IReadOnlyList<string> MemberIds);

/// <summary>
/// The part of ThemerrDB worth keeping locally: which works have a theme at all.
/// </summary>
/// <remarks>
/// <para>
/// The database is published once a day and is small — around 4400 films, 1300 shows and 140
/// collections — while a library scan asks about every item it holds. Without this, a run over
/// 772 titles spends 770-odd requests discovering that most of them are not in the database.
/// With it, a miss costs nothing and only a hit makes a request.
/// </para>
/// <para>
/// Theme URLs are not stored for films and shows: the record has to be fetched to place a theme
/// anyway, and holding thousands of URLs that go stale between syncs buys nothing. Collections are
/// the exception — their record is fetched during the sync to learn which films belong to them,
/// so keeping the URL that came with it makes the fallback below free.
/// </para>
/// </remarks>
public sealed class ThemerrDbSnapshot
{
    /// <summary>Gets or sets when the catalogue was last read.</summary>
    public DateTime UpdatedUtc { get; set; }

    /// <summary>Gets or sets the TMDB ids of films that have a theme.</summary>
    public List<string> MovieTmdbIds { get; set; } = new();

    /// <summary>Gets or sets the IMDb ids of films that have a theme, for items with no TMDB id.</summary>
    public List<string> MovieImdbIds { get; set; } = new();

    /// <summary>Gets or sets the shows that have a theme.</summary>
    public List<CatalogueTitle> TvShows { get; set; } = new();

    /// <summary>Gets or sets the film collections that have a theme, with their members.</summary>
    public List<CatalogueCollection> Collections { get; set; } = new();

    /// <summary>Gets a value indicating whether this snapshot holds anything worth consulting.</summary>
    [JsonIgnore]
    public bool IsUsable => MovieTmdbIds.Count > 0 || TvShows.Count > 0;

    /// <summary>Gets how old the snapshot is.</summary>
    [JsonIgnore]
    public TimeSpan Age => DateTime.UtcNow - UpdatedUtc;

    /// <summary>Reports whether a film with this TMDB id has a theme.</summary>
    /// <param name="tmdbId">The film's TMDB id.</param>
    /// <returns><see langword="true"/> when the catalogue lists it.</returns>
    public bool HasMovie(string? tmdbId) => Contains(ref _movieTmdb, MovieTmdbIds, tmdbId);

    /// <summary>Reports whether a film with this IMDb id has a theme.</summary>
    /// <param name="imdbId">The film's IMDb id.</param>
    /// <returns><see langword="true"/> when the catalogue lists it.</returns>
    public bool HasMovieByImdb(string? imdbId) => Contains(ref _movieImdb, MovieImdbIds, imdbId);

    /// <summary>Reports whether a show with this TMDB id has a theme.</summary>
    /// <param name="tmdbId">The show's TMDB id.</param>
    /// <returns><see langword="true"/> when the catalogue lists it.</returns>
    public bool HasShow(string? tmdbId)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return false;
        }

        _shows ??= new HashSet<string>(TvShows.ConvertAll(show => show.Id), StringComparer.OrdinalIgnoreCase);
        return _shows.Contains(tmdbId);
    }

    /// <summary>Finds the collection a film belongs to, when the catalogue has a theme for it.</summary>
    /// <param name="tmdbId">The film's TMDB id.</param>
    /// <returns>The collection, or <see langword="null"/>.</returns>
    public CatalogueCollection? CollectionContaining(string? tmdbId)
    {
        if (string.IsNullOrWhiteSpace(tmdbId))
        {
            return null;
        }

        _memberships ??= BuildMemberships();
        return _memberships.GetValueOrDefault(tmdbId);
    }

    private Dictionary<string, CatalogueCollection> BuildMemberships()
    {
        var map = new Dictionary<string, CatalogueCollection>(StringComparer.OrdinalIgnoreCase);

        foreach (var collection in Collections)
        {
            foreach (var member in collection.MemberIds)
            {
                // A film in two collections keeps the first, which is stable because the sync
                // reads collections in the order the catalogue lists them.
                map.TryAdd(member, collection);
            }
        }

        return map;
    }

    private static bool Contains(ref HashSet<string>? cache, List<string> values, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        cache ??= new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        return cache.Contains(candidate);
    }

    // Lookup sets, built on first use. The lists are what is stored; these are how they are read.
    private HashSet<string>? _movieTmdb;
    private HashSet<string>? _movieImdb;
    private HashSet<string>? _shows;
    private Dictionary<string, CatalogueCollection>? _memberships;
}
