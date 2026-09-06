using System;
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
