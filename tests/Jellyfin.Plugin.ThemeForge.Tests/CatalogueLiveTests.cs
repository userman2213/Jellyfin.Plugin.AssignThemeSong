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
                await client.GetStringAsync($"https://app.lizardbyte.dev/ThemerrDB/{type}/pages.json", CancellationToken.None));

            Assert.True(pages > 0, $"{type} reported {pages} pages");

            var entries = Engines.Catalogue.ThemerrDbCatalogue.ParsePage(
                await client.GetStringAsync($"https://app.lizardbyte.dev/ThemerrDB/{type}/all_page_1.json", CancellationToken.None));

            Assert.NotEmpty(entries);
            Assert.All(entries, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Id)));
        }
    }

    [NetworkFact]
    public async Task AThemerrDbCollectionStillListsItsMembersAndItsTheme()
    {
        // The film fallback depends on both, and both live on somebody else's server.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await client.GetStringAsync(
            "https://app.lizardbyte.dev/ThemerrDB/movie_collections/themoviedb/8091.json",
            CancellationToken.None);

        var (url, members) = Engines.Catalogue.ThemerrDbCatalogue.ParseCollection(body);

        Assert.NotNull(url);
        Assert.NotEmpty(members);
    }

    [NetworkFact]
    public async Task ThemerrDbStillHasAShowRecordWhereTheCodeLooksForOne()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var body = await client.GetStringAsync(
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                Engines.Catalogue.ThemerrDbCatalogue.ShowsByTmdb,
                "1396"),
            CancellationToken.None);

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
    private sealed class SingleClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    [NetworkFact]
    public async Task ThemerrDbStillUsesTheFieldNameTheCodeReads()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await client.GetStringAsync(
            $"https://app.lizardbyte.dev/ThemerrDB/movies/themoviedb/{KnownTmdbId}.json",
            CancellationToken.None);

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

    /// <summary>Battlestar Galactica (2004), whose IMDb page MusicBrainz links to four releases.</summary>
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
    public async Task MusicBrainzStillAnswersInBothOfTheShapesTheParserReads()
    {
        // Both lookups in one test, a second apart. MusicBrainz allows one request per second and
        // answers 503 to a second one inside it -- which is exactly what happened when these were
        // two tests, and is worth knowing about rather than working around.
        using var client = Client();
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json");

        // The relationship lookup, which finds a release group from the film's IMDb page and
        // wraps it in a relation. If that key is ever respelled, or the artist credit moves,
        // this is where it shows.
        var byImdb = Engines.Credits.MusicBrainzCreditsSource.Parse(
            await Fetch(client, string.Format(CultureInfo.InvariantCulture, Engines.Credits.MusicBrainzCreditsSource.ByImdbUrl, Battlestar)));

        Assert.Contains("Bear McCreary", byImdb.Composers, StringComparer.Ordinal);
        Assert.NotNull(byImdb.ReleaseGroupId);

        await Task.Delay(TimeSpan.FromSeconds(1));

        // The short path, taken when Wikidata already supplied the id. It returns the release
        // group unwrapped rather than inside a relation, which the parser has to handle too.
        var byGroup = Engines.Credits.MusicBrainzCreditsSource.Parse(
            await Fetch(client, string.Format(CultureInfo.InvariantCulture, Engines.Credits.MusicBrainzCreditsSource.ByReleaseGroup, AlienSoundtrack)));

        Assert.Contains("Jerry Goldsmith", byGroup.Composers, StringComparer.Ordinal);
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
