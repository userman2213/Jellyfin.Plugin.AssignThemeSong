using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
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

    /// <summary>A catalogue that serves whatever the test put in it, and never syncs.</summary>
    private sealed class StubCatalogue : IThemerrDbCatalogue
    {
        private readonly ThemerrDbSnapshot _snapshot;

        public StubCatalogue(ThemerrDbSnapshot? snapshot = null) => _snapshot = snapshot ?? new ThemerrDbSnapshot();

        public Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

        public Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(_snapshot);
    }

    private const string ThemeJson = """{"youtube_theme_url": "https://www.youtube.com/watch?v=abcdefghijk"}""";

    private static MediaIdentity Series(string? tmdbId, string title = "Firefly", string? tvdbId = "78874") => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        Kind = Jellyfin.Data.Enums.BaseItemKind.Series,
        TmdbId = tmdbId,
        TvdbId = tvdbId,
    };

    private static MediaIdentity Movie(string? tmdbId, string? imdbId = null, string title = "Blade Runner") => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        Kind = Jellyfin.Data.Enums.BaseItemKind.Movie,
        TmdbId = tmdbId,
        ImdbId = imdbId,
    };

    private static PlexTvThemeSource Plex(HttpMessageHandler handler) =>
        new(new Factory(handler), NullThemeForgeLogger<PlexTvThemeSource>.Instance);

    private static ThemerrDbSource Themerr(HttpMessageHandler handler, ThemerrDbSnapshot? snapshot = null) =>
        new(new StubCatalogue(snapshot), new Factory(handler), NullThemeForgeLogger<ThemerrDbSource>.Instance);

    private static PluginConfiguration Enabled()
    {
        var configuration = TestData.Config();
        configuration.UsePlexThemeArchive = true;
        configuration.UseThemerrDb = true;
        return configuration;
    }

    private static ThemerrDbSnapshot Snapshot(
        IEnumerable<string>? movies = null,
        IEnumerable<string>? movieImdbIds = null,
        IEnumerable<CatalogueTitle>? shows = null,
        IEnumerable<CatalogueCollection>? collections = null) => new()
        {
            UpdatedUtc = DateTime.UtcNow,
            MovieTmdbIds = (movies ?? new[] { "999999" }).ToList(),
            MovieImdbIds = (movieImdbIds ?? Enumerable.Empty<string>()).ToList(),
            TvShows = (shows ?? Enumerable.Empty<CatalogueTitle>()).ToList(),
            Collections = (collections ?? Enumerable.Empty<CatalogueCollection>()).ToList(),
        };

    // ---- Gating -------------------------------------------------------------------

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
    public void ThemerrDbIsAskedBeforePlex()
    {
        // It covers films, shows and collections; Plex is the fallback for the series it lacks.
        // Stated by the sources rather than left to the order they happen to be registered in.
        Assert.True(Themerr(new Forbidden()).Order < Plex(new Forbidden()).Order);
    }

    // ---- Films --------------------------------------------------------------------

    [Fact]
    public async Task AFilmNotInTheCatalogueCostsNoRequest()
    {
        // The whole point of mirroring the index: a library is mostly titles the database has
        // never heard of, and asking about each one is hundreds of wasted requests per scan.
        var found = await Themerr(new Forbidden(), Snapshot(movies: new[] { "111" }))
            .FindAsync(Movie("78"), Enabled(), CancellationToken.None);

        Assert.Null(found);
    }

    [Fact]
    public async Task AFilmInTheCatalogueIsFetchedByItsTmdbId()
    {
        var handler = new Canned(HttpStatusCode.OK, ThemeJson);
        var found = await Themerr(handler, Snapshot(movies: new[] { "78" }))
            .FindAsync(Movie("78"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", found!.Url);
        Assert.Equal("abcdefghijk", found.Id);
        Assert.False(found.IsDirectAudio, "a link still goes through the ordinary download");
        Assert.Equal("ThemerrDB", found.Provenance);
        Assert.Contains("themoviedb/78.json", Assert.Single(handler.Requested).Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilmWithNoTmdbIdFallsBackToItsImdbId()
    {
        var handler = new Canned(HttpStatusCode.OK, ThemeJson);
        var found = await Themerr(handler, Snapshot(movies: new[] { "111" }, movieImdbIds: new[] { "tt0083658" }))
            .FindAsync(Movie(null, "tt0083658"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Contains("imdb/tt0083658.json", Assert.Single(handler.Requested).Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilmWithNoThemeInheritsItsCollections()
    {
        // The membership and the collection's theme both came out of the daily sync, so this
        // answers without a request at all.
        var collection = new CatalogueCollection(
            "8091",
            "Alien Collection",
            "https://www.youtube.com/watch?v=abcdefghijk",
            new[] { "348", "679", "8077" });

        var found = await Themerr(new Forbidden(), Snapshot(movies: new[] { "111" }, collections: new[] { collection }))
            .FindAsync(Movie("679", title: "Aliens"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", found!.Url);
        Assert.Contains("Alien Collection", found.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFilmsOwnThemeWinsOverItsCollections()
    {
        var collection = new CatalogueCollection(
            "8091",
            "Alien Collection",
            "https://www.youtube.com/watch?v=collection1",
            new[] { "348" });

        var handler = new Canned(HttpStatusCode.OK, ThemeJson);
        var found = await Themerr(handler, Snapshot(movies: new[] { "348" }, collections: new[] { collection }))
            .FindAsync(Movie("348", title: "Alien"), Enabled(), CancellationToken.None);

        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", found!.Url);
    }

    // ---- Shows --------------------------------------------------------------------

    [Fact]
    public async Task AShowInTheCatalogueIsFetchedByItsTmdbId()
    {
        var handler = new Canned(HttpStatusCode.OK, ThemeJson);
        var found = await Themerr(handler, Snapshot(shows: new[] { new CatalogueTitle("1396", "Breaking Bad") }))
            .FindAsync(Series("1396", "Breaking Bad"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Contains("tv_shows/themoviedb/1396.json", Assert.Single(handler.Requested).Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShowWithNoTmdbIdIsMatchedByItsNameWhenExactlyOneAnswers()
    {
        // ThemerrDB keys shows on TMDB and nothing else, but a Jellyfin series may carry only a
        // TheTVDB id. Matching against 1300 curated names is a different proposition from
        // matching against YouTube.
        var handler = new Canned(HttpStatusCode.OK, ThemeJson);
        var shows = new[] { new CatalogueTitle("1396", "Breaking Bad"), new CatalogueTitle("60059", "Better Call Saul") };

        var found = await Themerr(handler, Snapshot(shows: shows))
            .FindAsync(Series(null, "Breaking Bad"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Contains("tv_shows/themoviedb/1396.json", Assert.Single(handler.Requested).Url.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AShowWithNoTmdbIdAndNoNameMatchIsLeftAlone()
    {
        var shows = new[] { new CatalogueTitle("1396", "Breaking Bad") };

        Assert.Null(await Themerr(new Forbidden(), Snapshot(shows: shows))
            .FindAsync(Series(null, "Firefly"), Enabled(), CancellationToken.None));
    }

    [Theory]
    [InlineData("Girls", "The Golden Girls")]
    [InlineData("Star Trek", "Star Trek: Deep Space Nine")]
    [InlineData("Alien", "Aliens")]
    [InlineData("The Office", "The Office UK")]
    public void ASimilarNameIsNotTheSameWork(string wanted, string catalogued) =>
        Assert.Null(ThemerrDbSource.MatchShowByTitle(wanted, new[] { new CatalogueTitle("1", catalogued) }));

    [Theory]
    [InlineData("Breaking Bad", "Breaking Bad")]
    [InlineData("The Office", "The Office")]
    [InlineData("Marvel's Agents of S.H.I.E.L.D.", "Marvel's Agents of S.H.I.E.L.D.")]
    public void TheSameNameIs(string wanted, string catalogued) =>
        Assert.NotNull(ThemerrDbSource.MatchShowByTitle(wanted, new[] { new CatalogueTitle("1", catalogued) }));

    [Fact]
    public void ANameTwoEntriesAnswerToIsNotUsed()
    {
        // Two shows of the same name exist. The title cannot say which, so it says nothing.
        var shows = new[] { new CatalogueTitle("1", "Doctor Who"), new CatalogueTitle("2", "Doctor Who") };
        Assert.Null(ThemerrDbSource.MatchShowByTitle("Doctor Who", shows));
    }

    // ---- Without a mirror ----------------------------------------------------------

    [Fact]
    public async Task WithNoMirrorTheRecordIsAskedForDirectly()
    {
        // A fresh install, or one where the sync is switched off, still works.
        var handler = new Canned(HttpStatusCode.NotFound);
        Assert.Null(await Themerr(handler).FindAsync(Movie("78"), Enabled(), CancellationToken.None));
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task WithNoMirrorAndNoIdNothingIsAsked() =>
        Assert.Null(await Themerr(new Forbidden()).FindAsync(Movie(null), Enabled(), CancellationToken.None));

    // ---- Plex ----------------------------------------------------------------------

    [Fact]
    public async Task NothingIsRequestedForASeriesWithNoTvdbId() =>
        Assert.Null(await Plex(new Forbidden()).FindAsync(Series("1396", tvdbId: null), Enabled(), CancellationToken.None));

    [Fact]
    public async Task PlexOnlyAnswersForSeries() =>
        Assert.Null(await Plex(new Forbidden()).FindAsync(Movie("78"), Enabled(), CancellationToken.None));

    [Fact]
    public async Task AMissingSeriesProducesNothing()
    {
        var handler = new Canned(HttpStatusCode.NotFound);
        Assert.Null(await Plex(handler).FindAsync(Series("1396", tvdbId: "999999"), Enabled(), CancellationToken.None));
        Assert.Single(handler.Requested);
    }

    [Fact]
    public async Task APresentSeriesProducesAnAudioCandidateKeyedOnItsId()
    {
        var handler = new Canned(HttpStatusCode.OK);
        var found = await Plex(handler).FindAsync(Series("1396", tvdbId: "78874"), Enabled(), CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal("https://tvthemes.plexapp.com/78874.mp3", found!.Url);
        Assert.True(found.IsDirectAudio, "the archive serves audio, so yt-dlp is not involved");
        Assert.Equal("Plex television theme archive", found.Provenance);

        // A HEAD, because most series have no theme and pulling the audio to find that out would
        // download a few megabytes for every miss.
        Assert.Equal(HttpMethod.Head, Assert.Single(handler.Requested).Method);
    }

    [Fact]
    public async Task ACatalogueThatIsUnreachableIsNotAnError()
    {
        // A catalogue being down must not fail the item: the search ladder still runs.
        var handler = new Canned(HttpStatusCode.ServiceUnavailable);
        Assert.Null(await Plex(handler).FindAsync(Series("1396", tvdbId: "78874"), Enabled(), CancellationToken.None));
        Assert.Null(await Themerr(handler, Snapshot(movies: new[] { "78" })).FindAsync(Movie("78"), Enabled(), CancellationToken.None));
    }

    // ---- Link validation -------------------------------------------------------------

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://youtu.be/abcdefghijk")]
    [InlineData("https://music.youtube.com/watch?v=abcdefghijk")]
    public void AYoutubeLinkIsAccepted(string url) =>
        Assert.Equal(url, ThemerrDbCatalogue.AcceptableThemeUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("http://www.youtube.com/watch?v=abcdefghijk")]
    [InlineData("https://example.invalid/theme.mp3")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://wyoutube.com/watch?v=abcdefghijk")]
    [InlineData("https://youtube.com.evil.example/watch?v=abcdefghijk")]
    public void AnythingElseIsRefused(string? url)
    {
        // The field is community-supplied. Handing whatever it contains to a downloader would let
        // an arbitrary entry in someone else's database decide what this server fetches.
        Assert.Null(ThemerrDbCatalogue.AcceptableThemeUrl(url));
    }
}
