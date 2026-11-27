using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins that all of ThemerrDB is kept locally, links included, and that a run asks it nothing.
/// </summary>
/// <remarks>
/// Until 3.1.2 only the ids were kept, and each listed title's record was fetched during the run.
/// A request that failed read as "not in ThemerrDB": The Fast and the Furious: Tokyo Drift (TMDB
/// 9615), which ThemerrDB has, was left without a theme.
/// </remarks>
public sealed class ThemerrDbArchiveTests
{
    private const string TokyoDrift = "https://www.youtube.com/watch?v=pS5d77DQHOI";

    private readonly ITestOutputHelper _output;

    public ThemerrDbArchiveTests(ITestOutputHelper output) => _output = output;

    private static readonly (string Path, string Json)[] Files =
    {
        ("pax_global_header", "ignored"),
        ("ThemerrDB-gh-pages/index.html", "<html></html>"),
        ("ThemerrDB-gh-pages/movies/pages.json", """{"count": 2, "pages": 1}"""),
        ("ThemerrDB-gh-pages/movies/themoviedb/9615.json", """{"id": 9615, "imdb_id": "tt0463985", "title": "The Fast and the Furious: Tokyo Drift", "youtube_theme_url": "https://www.youtube.com/watch?v=pS5d77DQHOI"}"""),
        ("ThemerrDB-gh-pages/movies/imdb/tt0463985.json", """{"id": 9615, "youtube_theme_url": "https://www.youtube.com/watch?v=pS5d77DQHOI"}"""),
        ("ThemerrDB-gh-pages/movies/themoviedb/348.json", "{ this record is broken"),
        ("ThemerrDB-gh-pages/movies/themoviedb/999.json", """{"id": 999, "title": "No Link"}"""),
        ("ThemerrDB-gh-pages/tv_shows/themoviedb/157842.json", """{"id": 157842, "name": "Black Summoner", "first_air_date": "2022-07-09", "youtube_theme_url": "https://www.youtube.com/watch?v=UQgv8MBxCuU"}"""),
        ("ThemerrDB-gh-pages/movie_collections/themoviedb/8091.json", """{"id": 8091, "name": "Alien Collection", "youtube_theme_url": "https://www.youtube.com/watch?v=collection1", "parts": [{"id": 348}, {"id": 679}]}"""),
        ("ThemerrDB-gh-pages/games/igdb/1.json", """{"id": 1, "youtube_theme_url": "https://www.youtube.com/watch?v=agame000001"}"""),
    };

    [Fact]
    public async Task EveryRecordsLinkIsReadOutOfTheArchive()
    {
        var snapshot = await ThemerrDbCatalogue.ParseArchiveAsync(Archive(), CancellationToken.None);

        Assert.Equal((TokyoDrift, false), snapshot.MovieTheme("9615", null));
        Assert.Equal((TokyoDrift, true), snapshot.MovieTheme(null, "tt0463985"));
        Assert.Equal("https://www.youtube.com/watch?v=UQgv8MBxCuU", snapshot.ShowTheme("157842"));
        Assert.Equal(2022, snapshot.ShowYears["157842"]);
        Assert.Equal("Black Summoner", Assert.Single(snapshot.TvShows).Title);
        Assert.Equal(new[] { "348", "679" }, Assert.Single(snapshot.Collections).MemberIds);
        Assert.Equal(new[] { "9615" }, snapshot.MovieThemes.Keys);
        Assert.True(snapshot.HasLinks);
    }

    [Fact]
    public async Task TheLinksSurviveBeingSavedAndReadBack()
    {
        var snapshot = await ThemerrDbCatalogue.ParseArchiveAsync(Archive(), CancellationToken.None);

        var read = System.Text.Json.JsonSerializer.Deserialize<ThemerrDbSnapshot>(System.Text.Json.JsonSerializer.Serialize(snapshot))!;

        Assert.Equal((TokyoDrift, false), read.MovieTheme("9615", null));
        Assert.True(read.HasLinks);
        Assert.True(read.HasMovie("9615"));
    }

    [Fact]
    public async Task ARunAsksThemerrDbNothing()
    {
        var snapshot = await ThemerrDbCatalogue.ParseArchiveAsync(Archive(), CancellationToken.None);
        var source = Source(snapshot);

        var film = await source.FindAsync(Film("The Fast and the Furious: Tokyo Drift", "9615", "tt0463985"), Enabled(), CancellationToken.None);
        var byImdb = await source.FindAsync(Film("Tokyo Drift", null, "tt0463985"), Enabled(), CancellationToken.None);
        var collection = await source.FindAsync(Film("Alien 3", "679", null), Enabled(), CancellationToken.None);
        var unknown = await source.FindAsync(Film("Lucky Strike", "1", "tt19035928"), Enabled(), CancellationToken.None);

        Assert.Equal(TokyoDrift, film!.Url);
        Assert.Equal("its own TMDB id 9615", film.MatchedOn);
        Assert.Equal("its own IMDb id tt0463985", byImdb!.MatchedOn);
        Assert.Contains("collection1", collection!.Url, StringComparison.Ordinal);
        Assert.Null(unknown);
    }

    [Fact]
    public async Task ShowsAreFoundLocallyByIdAndNeverByACloseName()
    {
        var snapshot = await ThemerrDbCatalogue.ParseArchiveAsync(Archive(), CancellationToken.None);
        var source = Source(snapshot);

        Assert.NotNull(await source.FindAsync(Show("Black Summoner", "157842", 2022), Enabled(), CancellationToken.None));
        Assert.NotNull(await source.FindAsync(Show("Black Summoner", null, 2022), Enabled(), CancellationToken.None));
        Assert.Null(await source.FindAsync(Show("Black Summoner", null, 2010), Enabled(), CancellationToken.None));
        Assert.Null(await source.FindAsync(Show("Black Summer", "88052", 2019), Enabled(), CancellationToken.None));
        Assert.Null(await source.FindAsync(Show("Black Summer", null, 2019), Enabled(), CancellationToken.None));
    }

    [Fact]
    public async Task TheScheduledTaskDownloadsTheArchiveInOneRequest()
    {
        var handler = new Serves(url => url == ThemerrDbCatalogue.ArchiveUrl ? Archive(films: 1200).ToArray() : null);
        var catalogue = new ThemerrDbCatalogue(new Factory(handler), NullThemeForgeLogger<ThemerrDbCatalogue>.Instance);

        var snapshot = await catalogue.SyncAsync(null, CancellationToken.None);

        Assert.Equal(new[] { ThemerrDbCatalogue.ArchiveUrl }, handler.Requested);
        Assert.True(snapshot.MovieThemes.Count >= 1200);
        Assert.Equal(TokyoDrift, snapshot.MovieTheme("9615", null)!.Value.Url);
    }

    [Fact]
    public async Task WithoutTheArchiveTheLinksAreFetchedDuringTheTask()
    {
        var mirror = ThemerrDbCatalogue.MirrorUrl + "/";
        var files = new Dictionary<string, string>
        {
            ["movies/pages.json"] = """{"count": 1, "pages": 1}""",
            ["movies/all_page_1.json"] = """[{"id": 9615, "imdb_id": "tt0463985", "title": "Tokyo Drift"}]""",
            ["movies/themoviedb/9615.json"] = Files[3].Json,
            ["tv_shows/pages.json"] = """{"count": 1, "pages": 1}""",
            ["tv_shows/all_page_1.json"] = """[{"id": 157842, "title": "Black Summoner"}]""",
            ["tv_shows/themoviedb/157842.json"] = Files[7].Json,
            ["movie_collections/pages.json"] = """{"count": 0, "pages": 1}""",
            ["movie_collections/all_page_1.json"] = "[]",
        };
        var handler = new Serves(url => url.StartsWith(mirror, StringComparison.Ordinal) && files.TryGetValue(url[mirror.Length..], out var body)
            ? Encoding.UTF8.GetBytes(body)
            : null);
        var catalogue = new ThemerrDbCatalogue(new Factory(handler), NullThemeForgeLogger<ThemerrDbCatalogue>.Instance);

        var snapshot = await catalogue.SyncAsync(null, CancellationToken.None);

        Assert.Equal(TokyoDrift, snapshot.MovieTheme("9615", null)!.Value.Url);
        Assert.Equal(TokyoDrift, snapshot.MovieTheme(null, "tt0463985")!.Value.Url);
        Assert.NotNull(snapshot.ShowTheme("157842"));
        Assert.Equal(2022, snapshot.ShowYears["157842"]);
    }

    /// <summary>Runs against the real archive when THEMEFORGE_THEMERR_ARCHIVE names a copy of it.</summary>
    [Fact]
    public async Task TheRealArchiveYieldsTheWholeDatabase()
    {
        var path = Environment.GetEnvironmentVariable("THEMEFORGE_THEMERR_ARCHIVE");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return;
        }

        await using var file = File.OpenRead(path);
        var snapshot = await ThemerrDbCatalogue.ParseArchiveAsync(file, CancellationToken.None);
        _output.WriteLine("REAL films {0}, by imdb {1}, shows {2}, collections {3}; Tokyo Drift {4}; F&F 2001 {5}",
            snapshot.MovieThemes.Count, snapshot.MovieThemesByImdb.Count, snapshot.ShowThemes.Count, snapshot.Collections.Count,
            snapshot.MovieTheme("9615", null)?.Url, snapshot.MovieTheme("9799", null)?.Url);

        Assert.True(snapshot.MovieThemes.Count > 4000);
        Assert.True(snapshot.ShowThemes.Count > 1500);
        Assert.True(snapshot.Collections.Count > 100);
        Assert.Equal(TokyoDrift, snapshot.MovieTheme("9615", null)!.Value.Url);
        Assert.NotNull(snapshot.MovieTheme("9799", null));
        Assert.Equal("https://www.youtube.com/watch?v=UQgv8MBxCuU", snapshot.ShowTheme("157842"));
        Assert.Null(snapshot.ShowTheme("88052"));
    }

    private static MemoryStream Archive(int films = 0)
    {
        var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: true))
        {
            var all = Files.Concat(Enumerable.Range(0, films).Select(n => (
                $"ThemerrDB-gh-pages/movies/themoviedb/{100000 + n}.json",
                $$"""{"id": {{100000 + n}}, "title": "Film {{n}}", "youtube_theme_url": "https://www.youtube.com/watch?v=filmfilm{{n % 1000:000}}"}""")));

            foreach (var (path, json) in all)
            {
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, path) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(json)) });
            }
        }

        buffer.Position = 0;
        return buffer;
    }

    private static ThemerrDbSource Source(ThemerrDbSnapshot snapshot) => new(
        new Fixed(snapshot),
        new Factory(new Serves(_ => throw new InvalidOperationException("a run must ask ThemerrDB nothing"))),
        new Alive(),
        NullThemeForgeLogger<ThemerrDbSource>.Instance);

    private static PluginConfiguration Enabled()
    {
        var configuration = TestData.Config();
        configuration.UseThemerrDb = true;
        return configuration;
    }

    private static MediaIdentity Film(string title, string? tmdb, string? imdb) => new()
    {
        ItemId = Guid.NewGuid(), Title = title, NormalizedTitle = TitleNormalizer.Normalize(title),
        Kind = Jellyfin.Data.Enums.BaseItemKind.Movie, TmdbId = tmdb, ImdbId = imdb,
    };

    private static MediaIdentity Show(string title, string? tmdb, int year) => new()
    {
        ItemId = Guid.NewGuid(), Title = title, NormalizedTitle = TitleNormalizer.Normalize(title),
        Kind = Jellyfin.Data.Enums.BaseItemKind.Series, TmdbId = tmdb, Year = year,
    };

    private sealed class Fixed : IThemerrDbCatalogue
    {
        private readonly ThemerrDbSnapshot _snapshot;

        public Fixed(ThemerrDbSnapshot snapshot) => _snapshot = snapshot;

        public Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

        public Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken) => GetAsync(cancellationToken);
    }

    private sealed class Alive : ILinkChecker
    {
        public Task<LinkState> CheckAsync(string url, CancellationToken cancellationToken) => Task.FromResult(LinkState.Alive);
    }

    private sealed class Serves : HttpMessageHandler
    {
        private readonly Func<string, byte[]?> _answer;

        public Serves(Func<string, byte[]?> answer) => _answer = answer;

        public List<string> Requested { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requested.Add(url);
            var body = _answer(url);
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>404</html>") }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
