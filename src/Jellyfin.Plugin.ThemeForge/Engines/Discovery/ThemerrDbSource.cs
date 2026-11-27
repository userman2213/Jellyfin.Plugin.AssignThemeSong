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
    /// <summary>How far apart a show's first year and the item's may be and still be the same show.</summary>
    private const int YearTolerance = 1;

    private readonly IThemerrDbCatalogue _catalogue;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILinkChecker _linkChecker;
    private readonly IThemeForgeLogger<ThemerrDbSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemerrDbSource"/> class.</summary>
    /// <param name="catalogue">The local copy of which works have a theme.</param>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="linkChecker">Finds out whether a link still plays before it is handed on.</param>
    /// <param name="logger">Logger.</param>
    public ThemerrDbSource(
        IThemerrDbCatalogue catalogue,
        IHttpClientFactory httpClientFactory,
        ILinkChecker linkChecker,
        IThemeForgeLogger<ThemerrDbSource> logger)
    {
        _catalogue = catalogue;
        _httpClientFactory = httpClientFactory;
        _linkChecker = linkChecker;
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
    /// Finds the one show in the catalogue with exactly this name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// ThemerrDB keys shows on a TMDB id and nothing else, but a Jellyfin series may carry only a
    /// TheTVDB id depending on which metadata providers are enabled. Matching on the name is the
    /// only way those series ever benefit. It is used only for them: a show with its own TMDB id
    /// that ThemerrDB does not list has no theme there, and a name can then only find another show.
    /// </para>
    /// <para>
    /// The name has to be the same once case, accents, punctuation and a leading article are set
    /// aside, and only one entry may have it. It used to be close enough rather than the same, and
    /// "Black Summer" was close enough to "Black Summoner": the Netflix zombie series was given the
    /// opening of a Japanese anime.
    /// </para>
    /// </remarks>
    /// <param name="title">The series title as Jellyfin has it.</param>
    /// <param name="shows">The catalogue's shows.</param>
    /// <returns>The single show with exactly this name, or <see langword="null"/>.</returns>
    public static CatalogueTitle? MatchShowByTitle(string? title, IReadOnlyList<CatalogueTitle> shows)
    {
        if (string.IsNullOrWhiteSpace(title) || shows is null)
        {
            return null;
        }

        var wanted = TitleNormalizer.Normalize(title);
        if (wanted.Length == 0)
        {
            return null;
        }

        CatalogueTitle? found = null;

        foreach (var show in shows)
        {
            if (!string.Equals(TitleNormalizer.Normalize(show.Title), wanted, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                // Two entries answer to this name, so the name alone cannot say which.
                return null;
            }

            found = show;
        }

        return found;
    }

    /// <summary>
    /// Reports whether a show's record could be the item, by the year it first aired.
    /// </summary>
    /// <param name="record">The show's record.</param>
    /// <param name="year">The item's year, when known.</param>
    /// <returns><see langword="false"/> only when both years are known and more than one apart.</returns>
    internal static bool AiredInTheSameYear(string record, int? year)
    {
        if (year is null)
        {
            return true;
        }

        using var document = JsonDocument.Parse(record);
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("first_air_date", out var aired)
            || aired.ValueKind != JsonValueKind.String
            || aired.GetString() is not { Length: >= 4 } date
            || !int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var first))
        {
            return true;
        }

        return Math.Abs(first - year.Value) <= YearTolerance;
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
                    snapshot,
                    ThemerrDbCatalogue.MovieByTmdbPath(identity.TmdbId!),
                    $"its own TMDB id {identity.TmdbId}",
                    cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasMovie(identity.TmdbId))
        {
            return await FetchAsync(
                identity,
                snapshot,
                ThemerrDbCatalogue.MovieByTmdbPath(identity.TmdbId!),
                $"its own TMDB id {identity.TmdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasMovieByImdb(identity.ImdbId))
        {
            return await FetchAsync(
                identity,
                snapshot,
                ThemerrDbCatalogue.MovieByImdbPath(identity.ImdbId!),
                $"its own IMDb id {identity.ImdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.CollectionContaining(identity.TmdbId) is { } collection)
        {
            // No request: the collection's theme came with the membership during the sync.
            _logger.LogInformation(
                "ThemeForge: ThemerrDB has no theme for \"{Item}\" itself, but it is part of {Collection}, which has one.",
                identity.Label,
                collection.Title);

            return await PlayableOrNullAsync(
                identity,
                collection.ThemeUrl,
                $"{identity.Title} ({collection.Title}, ThemerrDB)",
                $"the collection \"{collection.Title}\" its TMDB id {identity.TmdbId} belongs to",
                cancellationToken).ConfigureAwait(false);
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
                    snapshot,
                    ThemerrDbCatalogue.ShowByTmdbPath(identity.TmdbId!),
                    $"its own TMDB id {identity.TmdbId}",
                    cancellationToken).ConfigureAwait(false);
        }

        if (snapshot.HasShow(identity.TmdbId))
        {
            return await FetchAsync(
                identity,
                snapshot,
                ThemerrDbCatalogue.ShowByTmdbPath(identity.TmdbId!),
                $"its own TMDB id {identity.TmdbId}",
                cancellationToken).ConfigureAwait(false);
        }

        // By name only for a show Jellyfin has no TMDB id for. A show with its own id that is not
        // listed has no theme in ThemerrDB, and a name could only find a different show.
        if (string.IsNullOrWhiteSpace(identity.TmdbId)
            && MatchShowByTitle(identity.Title, snapshot.TvShows) is { } matched)
        {
            _logger.LogInformation(
                "ThemeForge: \"{Item}\" has no TMDB id, and ThemerrDB lists exactly one show by exactly that name.",
                identity.Label);

            return await FetchAsync(
                identity,
                snapshot,
                ThemerrDbCatalogue.ShowByTmdbPath(matched.Id),
                $"its exact name, as the single ThemerrDB entry \"{matched.Title}\" (Jellyfin has no TMDB id for it)",
                cancellationToken,
                checkYear: true).ConfigureAwait(false);
        }

        return null;
    }

    private async Task<Candidate?> FetchAsync(
        MediaIdentity identity,
        ThemerrDbSnapshot snapshot,
        string path,
        string keyedOn,
        CancellationToken cancellationToken,
        bool checkYear = false)
    {
        try
        {
            var body = await ReadRecordAsync(snapshot, path, cancellationToken).ConfigureAwait(false);
            if (body is null)
            {
                return null;
            }

            var themeUrl = ThemerrDbCatalogue.ReadThemeUrl(body);

            if (themeUrl is null)
            {
                return null;
            }

            if (checkYear && !AiredInTheSameYear(body, identity.Year))
            {
                _logger.LogInformation(
                    "ThemeForge: ThemerrDB's show of the same name as \"{Item}\" first aired in another year, so it is a different show.",
                    identity.Label);
                return null;
            }

            _logger.LogInformation(
                "ThemeForge: ThemerrDB has a theme for \"{Item}\", by {KeyedOn}.",
                identity.Label,
                keyedOn);

            return await PlayableOrNullAsync(
                identity,
                themeUrl,
                $"{identity.Title} (ThemerrDB)",
                keyedOn,
                cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ThemeForge: ThemerrDB's record {Path} could not be read.", path);
            return null;
        }
    }

    /// <summary>
    /// Reads a record, from where the catalogue was last read first and then from the other address.
    /// </summary>
    /// <remarks>
    /// A miss is served as an HTML 404 page, so the status code is the only signal -- and it is the
    /// same signal a website that is down gives. Asking the other address before believing it keeps
    /// a down website from reading as "ThemerrDB has nothing for this", which since 10 November 2026
    /// it did for every title.
    /// </remarks>
    private async Task<string?> ReadRecordAsync(ThemerrDbSnapshot snapshot, string path, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        var addresses = string.IsNullOrEmpty(snapshot.Source)
            ? ThemerrDbCatalogue.Addresses
            : ThemerrDbCatalogue.Addresses.OrderBy(address => string.Equals(address, snapshot.Source, StringComparison.Ordinal) ? 0 : 1).ToList();

        foreach (var address in addresses)
        {
            var url = $"{address}/{path}";
            try
            {
                using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "ThemeForge: could not read {Url}.", url);
            }
        }

        return null;
    }

    /// <summary>
    /// Hands on a ThemerrDB link only if it still plays.
    /// </summary>
    /// <remarks>
    /// A dead link answered here as "no theme" lets the orchestrator do what it does for any other
    /// miss: ask the next catalogue, then search. Handed on instead, it failed the download, the
    /// item was marked failed, and because the catalogue is consulted before the search and is never
    /// behind a backoff, the next run met the same dead link -- so the item never got a theme.
    /// </remarks>
    private async Task<Candidate?> PlayableOrNullAsync(
        MediaIdentity identity,
        string themeUrl,
        string title,
        string matchedOn,
        CancellationToken cancellationToken)
    {
        var state = await _linkChecker.CheckAsync(themeUrl, cancellationToken).ConfigureAwait(false);

        if (state == LinkState.Dead)
        {
            _logger.LogInformation(
                "ThemeForge: ThemerrDB's theme for \"{Item}\" is no longer on YouTube ({Url}); searching instead.",
                identity.Label,
                themeUrl);
            return null;
        }

        return Describe(identity, themeUrl, title, matchedOn);
    }

    private Candidate Describe(MediaIdentity identity, string themeUrl, string title, string matchedOn) => new()
    {
        Id = Orchestration.ThemeOrchestrator.ExtractVideoId(themeUrl),
        Url = themeUrl,
        Title = title,
        Channel = Name,
        Description = $"Chosen for {matchedOn}.",
        FoundBy = new SearchQuery($"themerrdb:{identity.StableKey}", 0, "catalogue"),
        IsHydrated = true,
        Provenance = Name,
        MatchedOn = matchedOn,
    };
}
