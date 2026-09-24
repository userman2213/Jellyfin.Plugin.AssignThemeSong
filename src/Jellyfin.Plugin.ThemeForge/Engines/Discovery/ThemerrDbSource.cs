using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// Looks a film, show or film collection up in ThemerrDB.
/// </summary>
/// <remarks>
/// <para>
/// ThemerrDB is a community-curated index mapping a work's TMDB id to the video someone chose as
/// its theme. It covers roughly 4400 films, 1300 shows and 140 film collections. It supplies a
/// link rather than audio, so the download is unchanged — but the link was picked by a person for
/// that exact work, which is a far better starting point than the best guess a search can offer,
/// and it is asked before any searching happens.
/// </para>
/// <para>
/// Which works are in it is mirrored locally by <see cref="IThemerrDbCatalogue"/> and refreshed
/// daily, so a title that is not in the database costs no request at all. Without that mirror
/// every item in the library would cost one request to discover a miss. When no mirror exists yet
/// the source still works, by asking about each item directly — that is what it did before, and it
/// is what keeps a fresh install useful before the first sync runs.
/// </para>
/// <para>
/// A film with no theme of its own inherits its collection's, so Alien³ gets the Alien Collection
/// theme. The membership comes from the collection records read during the sync, so the fallback
/// costs nothing at run time.
/// </para>
/// </remarks>
public sealed class ThemerrDbSource : IThemeProvenanceSource
{
    private readonly IThemerrDbCatalogue _catalogue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<ThemerrDbSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemerrDbSource"/> class.</summary>
    /// <param name="catalogue">The local copy of which works have a theme.</param>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ThemerrDbSource(
        IThemerrDbCatalogue catalogue,
        IHttpClientFactory httpClientFactory,
        IThemeForgeLogger<ThemerrDbSource> logger)
    {
        _catalogue = catalogue;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ThemerrDB";

    /// <inheritdoc />
    /// <remarks>
    /// First. It covers films, shows and collections, and every entry in it was chosen by a
    /// person for that exact work.
    /// </remarks>
    public int Order => 0;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration) =>
        configuration is not null && configuration.UseThemerrDb;

    /// <inheritdoc />
    public async Task<Candidate?> FindAsync(
        MediaIdentity identity,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        var snapshot = await _catalogue.GetAsync(cancellationToken).ConfigureAwait(false);

        return identity.IsSeries
            ? await FindShowAsync(identity, snapshot, cancellationToken).ConfigureAwait(false)
            : await FindMovieAsync(identity, snapshot, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds the show in the catalogue whose title is unmistakably this one.
    /// </summary>
    /// <remarks>
    /// ThemerrDB keys shows on a TMDB id and nothing else, but a Jellyfin series may carry only a
    /// TheTVDB id depending on which metadata providers are enabled. Matching on title is the only
    /// way those series ever benefit — and against a curated list of about 1300 names it is a far
    /// safer thing to do than against YouTube. It is still held to a strict standard: each title
    /// has to name the other with nothing left over, in both directions, and the match has to be
    /// the only one. That is what separates "Girls" from "Golden Girls", which name each other in
    /// one direction only.
    /// </remarks>
    /// <param name="title">The series title as Jellyfin has it.</param>
    /// <param name="shows">The catalogue's shows.</param>
    /// <returns>The single unmistakable match, or <see langword="null"/>.</returns>
    public static CatalogueTitle? MatchShowByTitle(string? title, IReadOnlyList<CatalogueTitle> shows)
    {
        if (string.IsNullOrWhiteSpace(title) || shows is null)
        {
            return null;
        }

        CatalogueTitle? found = null;

        foreach (var show in shows)
        {
            if (!NamesTheSameWork(title, show.Title))
            {
                continue;
            }

            if (found is not null)
            {
                // Two entries answer to this name, so the title alone cannot say which.
                return null;
            }

            found = show;
        }

        return found;
    }

    private static bool NamesTheSameWork(string first, string second)
    {
        var forward = TitleAnchor.Match(first, second);
        if (forward.Rejected || forward.Residual < 1)
        {
            return false;
        }

        var backward = TitleAnchor.Match(second, first);
        return !backward.Rejected && backward.Residual >= 1;
    }

    private async Task<Candidate?> FindMovieAsync(
        MediaIdentity identity,
        ThemerrDbSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        // Without a mirror there is nothing to check against, so the record is asked for directly.
        // That is one request per film, which is what the mirror exists to avoid.
        if (!snapshot.IsUsable)
        {
            return string.IsNullOrWhiteSpace(identity.TmdbId)
                ? null
                : await FetchAsync(
                    identity,
                    string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.MoviesByTmdb, identity.TmdbId),
                    $"TMDB id {identity.TmdbId}",
                    cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasMovie(identity.TmdbId))
        {
            return await FetchAsync(
                identity,
                string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.MoviesByTmdb, identity.TmdbId),
                $"TMDB id {identity.TmdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasMovieByImdb(identity.ImdbId))
        {
            return await FetchAsync(
                identity,
                string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.MoviesByImdb, identity.ImdbId),
                $"IMDb id {identity.ImdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.CollectionContaining(identity.TmdbId) is { } collection)
        {
            // No request: the collection's theme came with the membership during the sync.
            _logger.LogInformation(
                "ThemeForge: ThemerrDB has no theme for \"{Item}\" itself, but it is part of {Collection}, which has one.",
                identity.Label,
                collection.Title);

            return Describe(
                identity,
                collection.ThemeUrl,
                $"{identity.Title} ({collection.Title}, ThemerrDB)",
                $"Chosen for the collection \"{collection.Title}\", which this film belongs to.");
        }

        return null;
    }

    private async Task<Candidate?> FindShowAsync(
        MediaIdentity identity,
        ThemerrDbSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (!snapshot.IsUsable)
        {
            return string.IsNullOrWhiteSpace(identity.TmdbId)
                ? null
                : await FetchAsync(
                    identity,
                    string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.ShowsByTmdb, identity.TmdbId),
                    $"TMDB id {identity.TmdbId}",
                    cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasShow(identity.TmdbId))
        {
            return await FetchAsync(
                identity,
                string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.ShowsByTmdb, identity.TmdbId),
                $"TMDB id {identity.TmdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        if (MatchShowByTitle(identity.Title, snapshot.TvShows) is { } matched)
        {
            _logger.LogInformation(
                "ThemeForge: \"{Item}\" has no TMDB id, but ThemerrDB lists exactly one show by that name.",
                identity.Label);

            return await FetchAsync(
                identity,
                string.Format(CultureInfo.InvariantCulture, ThemerrDbCatalogue.ShowsByTmdb, matched.Id),
                $"the single ThemerrDB entry named \"{matched.Title}\"",
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<Candidate?> FetchAsync(
        MediaIdentity identity,
        string url,
        string keyedOn,
        CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // A miss is served as an HTML 404 page, so the status code is the only reliable signal.
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var themeUrl = ThemerrDbCatalogue.ReadThemeUrl(body);

            if (themeUrl is null)
            {
                return null;
            }

            _logger.LogInformation(
                "ThemeForge: ThemerrDB has a theme for \"{Item}\" ({KeyedOn}).",
                identity.Label,
                keyedOn);

            return Describe(identity, themeUrl, $"{identity.Title} (ThemerrDB)", $"Chosen for {keyedOn}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "ThemeForge: could not read {Url}.", url);
            return null;
        }
    }

    private Candidate Describe(MediaIdentity identity, string themeUrl, string title, string description) => new()
    {
        Id = Orchestration.ThemeOrchestrator.ExtractVideoId(themeUrl),
        Url = themeUrl,
        Title = title,
        Channel = Name,
        Description = description,
        FoundBy = new SearchQuery($"themerrdb:{identity.StableKey}", 0, "catalogue"),
        IsHydrated = true,
        Provenance = Name,
    };
}
