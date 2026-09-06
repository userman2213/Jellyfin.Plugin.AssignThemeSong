using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the catalogues that answer by the item's own database id.
/// </summary>
/// <remarks>
/// The value of these sources is that a hit is certain, so what matters most is that they never
/// answer when they should not: a catalogue that returned something for the wrong id, or was
/// consulted when the user had switched it off, would bypass every check the scoring pipeline
/// applies and write the result straight into the library.
/// </remarks>
public class ProvenanceSourceTests
{
    /// <summary>A handler that fails the test if it is ever asked for anything.</summary>
    private sealed class Forbidden : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException($"no request should have been made, but one went to {request.RequestUri}");
    }

    private sealed class Canned : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public Canned(HttpStatusCode status, string body = "")
        {
            _status = status;
            _body = body;
        }

        public List<(HttpMethod Method, Uri Url)> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add((request.Method, request.RequestUri!));
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private static MediaIdentity Series(string? tvdbId) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = "Firefly",
        NormalizedTitle = "firefly",
        Kind = Jellyfin.Data.Enums.BaseItemKind.Series,
        TvdbId = tvdbId,
    };

    private static MediaIdentity Movie(string? tmdbId) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = "Blade Runner",
        NormalizedTitle = "blade runner",
        Kind = Jellyfin.Data.Enums.BaseItemKind.Movie,
        TmdbId = tmdbId,
    };

    private static PlexTvThemeSource Plex(HttpMessageHandler handler) =>
        new(new Factory(handler), NullThemeForgeLogger<PlexTvThemeSource>.Instance);

    private static ThemerrDbSource Themerr(HttpMessageHandler handler) =>
        new(new Factory(handler), NullThemeForgeLogger<ThemerrDbSource>.Instance);

    private static PluginConfiguration Enabled()
    {
        var configuration = TestData.Config();
        configuration.UsePlexThemeArchive = true;
        configuration.UseThemerrDb = true;
        return configuration;
    }

    [Fact]
    public void ThePlexArchiveIsOffUntilItIsTurnedOn()
    {
        // Its files are hosted for Plex's own clients. Defaulting it on would make that choice
        // for every user who installs the plugin without reading anything.
        Assert.False(TestData.Config().UsePlexThemeArchive);
        Assert.False(Plex(new Forbidden()).IsEnabled(TestData.Config()));
        Assert.True(Plex(new Forbidden()).IsEnabled(Enabled()));
    }

    [Fact]
    public void ThemerrDbIsOnByDefault() =>
        Assert.True(TestData.Config().UseThemerrDb);

    [Fact]
    public async Task NothingIsRequestedForAnItemWithNoIdToLookUp()
    {
        // The handler throws if touched, so this asserts the absence of a request rather than
        // merely the absence of a result.
        Assert.Null(await Plex(new Forbidden()).FindAsync(Series(null), Enabled(), CancellationToken.None));
        Assert.Null(await Themerr(new Forbidden()).FindAsync(Movie(null), Enabled(), CancellationToken.None));
    }

    [Fact]
    public async Task EachCatalogueOnlyAnswersForTheKindItCovers()
    {
        // ThemerrDB's television set was a strict subset of Plex's, so films go to one and series
        // to the other rather than both being asked everything.
        Assert.Null(await Plex(new Forbidden()).FindAsync(Movie("78874"), Enabled(), CancellationToken.None));
        Assert.Null(await Themerr(new Forbidden()).FindAsync(Series("78874"), Enabled(), CancellationToken.None));
    }

    [Fact]
    public async Task AMissingSeriesProducesNothing()
    {
        var handler = new Canned(HttpStatusCode.NotFound);
        Assert.Null(await Plex(handler).FindAsync(Series("999999"), Enabled(), CancellationToken.None));
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task APresentSeriesProducesAnAudioCandidateKeyedOnItsId()
    {
        var handler = new Canned(HttpStatusCode.OK);
        var found = await Plex(handler).FindAsync(Series("78874"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("https://tvthemes.plexapp.com/78874.mp3", found!.Url);
        Assert.True(found.IsDirectAudio, "the archive serves audio, so yt-dlp is not involved");
        Assert.Equal("Plex television theme archive", found.Provenance);

        // A HEAD, because most series have no theme and pulling the audio to find that out would
        // download a few megabytes for every miss.
        Assert.Equal(HttpMethod.Head, Assert.Single(handler.Requested).Method);
    }

    [Fact]
    public async Task APresentFilmProducesTheLinkTheCatalogueChose()
    {
        var handler = new Canned(
            HttpStatusCode.OK,
            """{"youtube_theme_url": "https://www.youtube.com/watch?v=abcdefghijk"}""");

        var found = await Themerr(handler).FindAsync(Movie("78"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", found!.Url);
        Assert.Equal("abcdefghijk", found.Id);
        Assert.False(found.IsDirectAudio, "a link still goes through the ordinary download");
        Assert.Equal("ThemerrDB", found.Provenance);
    }

    [Fact]
    public async Task ACatalogueThatIsUnreachableIsNotAnError()
    {
        // A catalogue being down must not fail the item: the search ladder still runs.
        var handler = new Canned(HttpStatusCode.ServiceUnavailable);
        Assert.Null(await Plex(handler).FindAsync(Series("78874"), Enabled(), CancellationToken.None));
        Assert.Null(await Themerr(handler).FindAsync(Movie("78"), Enabled(), CancellationToken.None));
    }

    [Theory]
    [InlineData("""{"youtube_theme_url": "https://www.youtube.com/watch?v=abcdefghijk"}""", "https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("""{"youtube_theme_url": "https://youtu.be/abcdefghijk"}""", "https://youtu.be/abcdefghijk")]
    [InlineData("""{"youtube_theme_url": "https://music.youtube.com/watch?v=abcdefghijk"}""", "https://music.youtube.com/watch?v=abcdefghijk")]
    public void AYoutubeLinkIsAccepted(string json, string expected) =>
        Assert.Equal(expected, ThemerrDbSource.ReadYoutubeUrl(json));

    [Theory]
    [InlineData("""{}""")]
    [InlineData("""{"youtube_theme_url": ""}""")]
    [InlineData("""{"youtube_theme_url": null}""")]
    [InlineData("""{"youtube_theme_url": "not a url"}""")]
    [InlineData("""{"youtube_theme_url": "http://www.youtube.com/watch?v=abcdefghijk"}""")]
    [InlineData("""{"youtube_theme_url": "https://example.invalid/theme.mp3"}""")]
    [InlineData("""{"youtube_theme_url": "file:///etc/passwd"}""")]
    [InlineData("""{"youtube_theme_url": "https://wyoutube.com/watch?v=abcdefghijk"}""")]
    [InlineData("""{"youtube_theme_url": "https://youtube.com.evil.example/watch?v=abcdefghijk"}""")]
    public void AnythingElseIsRefused(string json)
    {
        // The field is community-supplied. Handing whatever it contains to a downloader would let
        // an arbitrary entry in someone else's database decide what this server fetches.
        Assert.Null(ThemerrDbSource.ReadYoutubeUrl(json));
    }
}
