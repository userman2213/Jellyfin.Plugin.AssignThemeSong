using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Checks the catalogues still answer the way the code assumes.
/// </summary>
/// <remarks>
/// These are the only assumptions in the plugin that live on somebody else's server: a URL shape
/// and a JSON field name, both of which can change without warning and neither of which any
/// amount of unit testing would catch. Off by default because a test suite that needs the
/// internet is a test suite that fails for the wrong reasons.
/// </remarks>
public class CatalogueLiveTests
{
    /// <summary>Firefly. Chosen because the archive has it and it is unlikely to be removed.</summary>
    private const string KnownTvdbId = "78874";

    /// <summary>Blade Runner.</summary>
    private const string KnownTmdbId = "78";

    [NetworkFact]
    public async Task ThePlexArchiveStillServesAudioForAKnownSeries()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://tvthemes.plexapp.com/{KnownTvdbId}.mp3");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode, $"expected a theme for TVDB {KnownTvdbId}, got {(int)response.StatusCode}");
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [NetworkFact]
    public async Task ThePlexArchiveStillAnswersNotFoundForANonexistentSeries()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://tvthemes.plexapp.com/99999999.mp3");
        using var response = await client.SendAsync(request, CancellationToken.None);

        // A miss has to be distinguishable from a hit, or every series would appear to have a theme.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [NetworkFact]
    public async Task ThemerrDbStillPublishesAPagedIndexForAllThreeCategories()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        foreach (var type in new[] { "movies", "tv_shows", "movie_collections" })
        {
            var pages = Engines.Catalogue.ThemerrDbCatalogue.ParsePageCount(
                await FromThemerrDbAsync(client, $"{type}/pages.json"));

            Assert.True(pages > 0, $"{type} reported {pages} pages");

            var entries = Engines.Catalogue.ThemerrDbCatalogue.ParsePage(
                await FromThemerrDbAsync(client, $"{type}/all_page_1.json"));

            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Id)));
        }
    }

    [NetworkFact]
    public async Task AThemerrDbCollectionStillListsItsMembersAndItsTheme()
    {
        // The film fallback depends on both, and both live on somebody else's server.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await FromThemerrDbAsync(client, "movie_collections/themoviedb/8091.json");

        var (url, members) = Engines.Catalogue.ThemerrDbCatalogue.ParseCollection(body);

        Assert.NotNull(url);
        Assert.NotEmpty(members);
    }

    [NetworkFact]
    public async Task ThemerrDbStillHasAShowRecordWhereTheCodeLooksForOne()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await FromThemerrDbAsync(client, Engines.Catalogue.ThemerrDbCatalogue.ShowByTmdbPath("1396"));

        Assert.NotNull(Engines.Catalogue.ThemerrDbCatalogue.ReadThemeUrl(body));
    }

    [NetworkFact]
    public async Task TheWholeSyncRunsAgainstTheRealService()
    {
        // The only test that exercises the sync end to end: several hundred paged requests, then
        // every collection record, then the lookups built from them. Slow, and worth it — this is
        // the code that decides whether a scan costs one request per title or none.
        var catalogue = new Engines.Catalogue.ThemerrDbCatalogue(
            new SingleClientFactory(),
            NullThemeForgeLogger<Engines.Catalogue.ThemerrDbCatalogue>.Instance);

        var snapshot = await catalogue.SyncAsync(null, CancellationToken.None);

        Assert.True(snapshot.IsUsable, "the sync produced nothing");
        Assert.True(snapshot.MovieTmdbIds.Count > 3000, $"films: {snapshot.MovieTmdbIds.Count}");
        Assert.True(snapshot.TvShows.Count > 1000, $"shows: {snapshot.TvShows.Count}");
        Assert.True(snapshot.Collections.Count > 50, $"collections: {snapshot.Collections.Count}");

        // Known entries, so a sync that returned the right shape of nothing still fails.
        Assert.True(snapshot.HasMovie("78"), "Blade Runner should be listed");
        Assert.True(snapshot.HasShow("1396"), "Breaking Bad should be listed");
        Assert.NotNull(snapshot.CollectionContaining("348"));

        Assert.All(snapshot.Collections, collection =>
        {
            Assert.NotEmpty(collection.MemberIds);
            Assert.StartsWith("https://", collection.ThemeUrl, StringComparison.Ordinal);
        });
    }

    /// <summary>Hands the catalogue a plain client, since there is no Jellyfin host here.</summary>
    /// <summary>
    /// Reads a file from wherever ThemerrDB publishes it now -- its website, else GitHub's copy --
    /// as the plugin does. The format is what is checked, not which address is up.
    /// </summary>
    private static async Task<string> FromThemerrDbAsync(HttpClient client, string path)
    {
        foreach (var address in Engines.Catalogue.ThemerrDbCatalogue.Addresses)
        {
            using var response = await client.GetAsync($"{address}/{path}", CancellationToken.None);
            if (response.IsSuccessStatusCode)
            {
                return await response.Content.ReadAsStringAsync(CancellationToken.None);
            }
        }

        throw new HttpRequestException($"ThemerrDB has no {path} at any of its addresses");
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [NetworkFact]
    public async Task AgainstTheRealThemerrDbBlackSummerGetsNoOtherShowsTheme()
    {
        // Black Summer (TMDB 88052) is not in ThemerrDB; Black Summoner (157842), an anime, is.
        // Until 3.1.1 the first was given the second's opening by a name close to its own.
        var catalogue = new Engines.Catalogue.ThemerrDbCatalogue(
            new SingleClientFactory(),
            NullThemeForgeLogger<Engines.Catalogue.ThemerrDbCatalogue>.Instance);
        var snapshot = await catalogue.SyncAsync(null, CancellationToken.None);
        var source = new Engines.Discovery.ThemerrDbSource(
            catalogue,
            new SingleClientFactory(),
            new AlwaysPlays(),
            NullThemeForgeLogger<Engines.Discovery.ThemerrDbSource>.Instance);
        var configuration = TestData.Config();
        configuration.UseThemerrDb = true;

        Assert.Null(Engines.Discovery.ThemerrDbSource.MatchShowByTitle("Black Summer", snapshot.TvShows));
        Assert.Null(await source.FindAsync(Show("Black Summer", "88052", 2019), configuration, CancellationToken.None));
        Assert.Null(await source.FindAsync(Show("Black Summer", null, 2019), configuration, CancellationToken.None));

        var summoner = await source.FindAsync(Show("Black Summoner", "157842", 2022), configuration, CancellationToken.None);
        Assert.Contains("UQgv8MBxCuU", summoner!.Url, StringComparison.Ordinal);

        var breakingBad = await source.FindAsync(Show("Breaking Bad", "1396", 2008), configuration, CancellationToken.None);
        Assert.Equal("its own TMDB id 1396", breakingBad!.MatchedOn);
    }

    private static Engines.Identity.MediaIdentity Show(string title, string? tmdbId, int year) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = Engines.Identity.TitleNormalizer.Normalize(title),
        Year = year,
        Kind = Jellyfin.Data.Enums.BaseItemKind.Series,
        TmdbId = tmdbId,
    };

    private sealed class AlwaysPlays : Engines.Discovery.ILinkChecker
    {
        public Task<Engines.Discovery.LinkState> CheckAsync(string url, CancellationToken cancellationToken) =>
            Task.FromResult(Engines.Discovery.LinkState.Alive);
    }

    [NetworkFact]
    public async Task ThemerrDbStillUsesTheFieldNameTheCodeReads()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await FromThemerrDbAsync(client, Engines.Catalogue.ThemerrDbCatalogue.MovieByTmdbPath(KnownTmdbId));

        var url = Engines.Catalogue.ThemerrDbCatalogue.ReadThemeUrl(json);

        Assert.NotNull(url);
        Assert.StartsWith("https://", url, StringComparison.Ordinal);
    }
}

/// <summary>
/// Checks that the two composer databases still answer the way the parsers assume.
/// </summary>
/// <remarks>
/// <para>
/// These pin the things no amount of unit testing can: a URL shape, a property name and a
/// User-Agent policy, all three of which live on somebody else's server and can change without
/// warning. The fixtures the parsers are tested against are answers these very requests gave, so
/// when one of these fails the fixture is stale and the parser is about to be wrong.
/// </para>
/// <para>
/// Off by default. A test suite that needs the internet is a test suite that fails for the wrong
/// reasons, and both services rate limit.
/// </para>
/// </remarks>
public class CreditsLiveTests
{
    /// <summary>Alien. Wikidata has its composer, its soundtrack release and both of its ids.</summary>
    private const string Alien = "tt0078748";

    private const string Battlestar = "tt0407362";

    /// <summary>Alien's soundtrack release group, which Wikidata supplies alongside the composer.</summary>
    private const string AlienSoundtrack = "e15b15c1-97e6-3f22-a21f-ec7c2498f604";

    private static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", Engines.Credits.PoliteRequest.UserAgent);
        return client;
    }

    [NetworkFact]
    public async Task WikidataStillAnswersAboutABatchByImdbAndTmdbId()
    {
        var query = Engines.Credits.WikidataCreditsSource.BuildQuery(new[]
        {
            new Engines.Credits.CreditsRequest("imdb:" + Alien, new[] { "imdb:" + Alien }, Alien, "348", false, "Alien"),
            new Engines.Credits.CreditsRequest("tmdbtv:1972", new[] { "tmdbtv:1972" }, null, "1972", true, "Battlestar Galactica"),
        });

        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, Engines.Credits.WikidataCreditsSource.Endpoint)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query!) }),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");

        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode, $"Wikidata answered {(int)response.StatusCode}");

        var found = Engines.Credits.WikidataCreditsSource.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        // Both branches of the query, and both ways a work can be keyed.
        Assert.Contains("Jerry Goldsmith", found["imdb:" + Alien].Composers, StringComparer.Ordinal);
        Assert.Contains("Bear McCreary", found["tmdbtv:1972"].Composers, StringComparer.Ordinal);
    }

    [NetworkFact]
    public async Task WikidataStillNamesAThemeAndTheArticleAndLeavesSeasonsOut()
    {
        // Friends has its theme on Wikidata. The X-Files shares its IMDb id with its tenth season,
        // whose article must not be the one returned.
        var query = Engines.Credits.WikidataCreditsSource.BuildQuery(new[]
        {
            new Engines.Credits.CreditsRequest("imdb:tt0108778", new[] { "imdb:tt0108778" }, "tt0108778", null, true, "Friends"),
            new Engines.Credits.CreditsRequest("imdb:tt0106179", new[] { "imdb:tt0106179" }, "tt0106179", null, true, "The X-Files"),
        });

        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, Engines.Credits.WikidataCreditsSource.Endpoint)
        {
            Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query!) }),
        };
        request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");

        using var response = await client.SendAsync(request, CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode, $"Wikidata answered {(int)response.StatusCode}");

        var found = Engines.Credits.WikidataCreditsSource.Parse(await response.Content.ReadAsStringAsync(CancellationToken.None));

        Assert.Equal("I'll Be There for You", found["imdb:tt0108778"].Theme?.Title);
        Assert.Equal("The X-Files", found["imdb:tt0106179"].WikipediaTitle);
    }

    [NetworkFact]
    public async Task WikipediaStillRecordsTheSopranosThemeInItsInfobox()
    {
        // The request the source makes, and the parser it feeds. If the infobox field is renamed or
        // the response changes shape, this is where it shows. Wikipedia rate limits, so a 429 here
        // says more about the network than about the code; the source waits one out and stops.
        using var client = Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        var url = Engines.Credits.WikipediaThemeSource.Endpoint
            + "?action=query&prop=revisions&rvprop=content&rvslots=main&format=json&formatversion=2&redirects=1&maxlag=5&titles="
            + Uri.EscapeDataString("The Sopranos");

        using var response = await client.GetAsync(url, CancellationToken.None);
        Assert.True(response.IsSuccessStatusCode, $"Wikipedia answered {(int)response.StatusCode}");

        var articles = Engines.Credits.WikipediaThemeSource.ParseArticles(
            await response.Content.ReadAsStringAsync(CancellationToken.None),
            new[] { "The Sopranos" });

        Assert.Equal(
            new Engines.Credits.ThemeSong("Woke Up This Morning", "Alabama 3"),
            Engines.Credits.WikipediaInfobox.Theme(articles["The Sopranos"]));
    }

    /// <summary>Fetches one document, waiting out a "too fast" exactly as the source does.</summary>
    private static async Task<string> Fetch(HttpClient client, string url)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await client.GetAsync(url, CancellationToken.None);
            if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt));
                continue;
            }

            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(CancellationToken.None);
        }
    }
}
