using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins the local copy of IMDb's crew data: what a download produces, what a failed one leaves
/// behind, and when a new one is due.
/// </summary>
public sealed class ImdbCrewDataTests : IDisposable
{
    private static readonly DateTimeOffset Published = new(2026, 11, 1, 0, 46, 33, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-crewdata-" + Guid.NewGuid().ToString("N"));

    public ImdbCrewDataTests() => Directory.CreateDirectory(_root);

    private string CopyPath => Path.Combine(_root, "imdb-composers.bin");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task ADownloadNamesEachTitlesComposers()
    {
        using var crew = Crew(new Imdb());

        Assert.True(await crew.RefreshAsync(null, CancellationToken.None));

        Assert.Equal(new[] { "Larry Groupé" }, crew.Composers("tt19035928"));
        Assert.Equal(new[] { "Dave Porter" }, crew.Composers("tt0903747"));
        Assert.Equal(new[] { "Dust Brothers", "John King", "Michael Simpson" }, crew.Composers("tt0137523"));
        Assert.Empty(crew.Composers("tt0141842"));
        Assert.Empty(crew.Composers(null));
    }

    [Fact]
    public async Task SaysWhatTheCopyHolds()
    {
        using var crew = Crew(new Imdb());
        await crew.RefreshAsync(null, CancellationToken.None);

        var status = crew.Status(new PluginConfiguration());

        Assert.True(status.Enabled);
        Assert.True(status.Available);
        Assert.Null(status.Problem);
        Assert.Equal(Published.UtcDateTime, status.PublishedUtc);
        Assert.Equal(4, status.Titles);
        Assert.Equal(1, status.Series);
        Assert.True(status.FileBytes > 0);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task AFailedDownloadKeepsThePreviousCopy()
    {
        var imdb = new Imdb();
        using var crew = Crew(imdb);
        await crew.RefreshAsync(null, CancellationToken.None);

        imdb.Broken = ImdbCrewDataset.NamesFile;
        var replaced = await crew.RefreshAsync(null, CancellationToken.None);

        Assert.False(replaced);
        Assert.Equal(new[] { "Larry Groupé" }, crew.Composers("tt19035928"));
        var status = crew.Status(new PluginConfiguration());
        Assert.True(status.Available);
        Assert.Contains("503", status.LastError, StringComparison.Ordinal);
        Assert.False(File.Exists(CopyPath + ".new"));
    }

    [Fact]
    public async Task ADownloadThatNamesTooFewTitlesIsNotTrusted()
    {
        using var crew = Crew(new Imdb(), minimumTitles: 100);

        Assert.False(await crew.RefreshAsync(null, CancellationToken.None));

        Assert.False(crew.Status(new PluginConfiguration()).Available);
        Assert.Contains("only 4 titles", crew.Status(new PluginConfiguration()).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACopyIsReadAgainAfterARestart()
    {
        using (var first = Crew(new Imdb()))
        {
            await first.RefreshAsync(null, CancellationToken.None);
        }

        using var second = Crew(new Imdb { Broken = ImdbCrewDataset.PrincipalsFile });

        Assert.Equal(new[] { "Larry Groupé" }, second.Composers("tt19035928"));
    }

    [Fact]
    public void WithNoCopyThereIsNothingToSayAndADownloadIsDue()
    {
        using var crew = Crew(new Imdb());

        Assert.Empty(crew.Composers("tt19035928"));
        Assert.True(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow, out var reason));
        Assert.Equal("there is no copy yet", reason);
        Assert.Equal("it has not been downloaded", crew.Status(new PluginConfiguration()).Problem);
    }

    [Fact]
    public async Task AFreshCopyIsNotDownloadedAgain()
    {
        using var crew = Crew(new Imdb());
        await crew.RefreshAsync(null, CancellationToken.None);

        Assert.False(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow.AddDays(29), out var reason));
        Assert.Contains("a new one is due in", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMonthOldCopyIsReplaced()
    {
        using var crew = Crew(new Imdb());
        await crew.RefreshAsync(null, CancellationToken.None);

        Assert.True(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow.AddDays(31), out var reason));
        Assert.Contains("days old", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATitleNewerThanTheCopyBringsTheNextDownloadForwardToAWeek()
    {
        using var crew = Crew(new Imdb());
        await crew.RefreshAsync(null, CancellationToken.None);
        var configuration = new PluginConfiguration();

        Assert.False(crew.IsDue(configuration, DateTime.UtcNow.AddDays(8), out _));

        // Numbered above anything in the copy, so added to IMDb after it was made.
        Assert.Empty(crew.Composers("tt40000001"));

        Assert.False(crew.IsDue(configuration, DateTime.UtcNow.AddDays(3), out _));
        Assert.True(crew.IsDue(configuration, DateTime.UtcNow.AddDays(8), out var reason));
        Assert.Contains("added to IMDb after the copy was made", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnOlderTitleWithNoComposerIsNotMistakenForANewOne()
    {
        using var crew = Crew(new Imdb());
        await crew.RefreshAsync(null, CancellationToken.None);

        Assert.Empty(crew.Composers("tt0141842"));

        Assert.False(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow.AddDays(8), out _));
    }

    [Fact]
    public void NothingIsDueWhenItIsSwitchedOff()
    {
        using var crew = Crew(new Imdb());

        Assert.False(crew.IsDue(new PluginConfiguration { UseImdbCrewData = false }, DateTime.UtcNow, out var reason));
        Assert.Contains("switched off", reason, StringComparison.Ordinal);
        Assert.False(crew.IsDue(new PluginConfiguration { ResearchComposers = false }, DateTime.UtcNow, out _));
    }

    [Fact]
    public async Task ReportsProgressAcrossTheThreeFiles()
    {
        using var crew = Crew(new Imdb());
        var seen = new List<double>();

        await crew.RefreshAsync(new Recorder(seen), CancellationToken.None);

        Assert.Equal(100, seen[^1]);
        Assert.All(seen, value => Assert.InRange(value, 0, 100));
    }

    [Fact]
    public async Task ACancelledDownloadKeepsThePreviousCopy()
    {
        var imdb = new Imdb();
        using var crew = Crew(imdb);
        await crew.RefreshAsync(null, CancellationToken.None);

        using var cancel = new CancellationTokenSource();
        imdb.OnRequest = file =>
        {
            if (file == ImdbCrewDataset.EpisodesFile)
            {
                cancel.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crew.RefreshAsync(null, cancel.Token));

        Assert.Equal(new[] { "Larry Groupé" }, crew.Composers("tt19035928"));
    }

    [Fact]
    public async Task AFailedDownloadIsNotRetriedOnEveryRestartOfTheDay()
    {
        // A download that fails after the 800 MB principals file must not be repeated by every
        // restart and every trigger that day.
        using var crew = Crew(new Imdb { Broken = ImdbCrewDataset.NamesFile });
        await crew.RefreshAsync(null, CancellationToken.None);

        Assert.False(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow.AddHours(2), out var reason));
        Assert.Contains("the last download failed", reason, StringComparison.Ordinal);
        Assert.True(crew.IsDue(new PluginConfiguration(), DateTime.UtcNow.AddHours(21), out _));
    }

    [Fact]
    public async Task ADownloadCancelledMidStreamIsACancellationNotAFailure()
    {
        // Cancelling closes the connection, so the read under way fails with whatever closing it
        // throws. That is still a cancellation, and must not be reported as IMDb failing.
        var imdb = new Imdb();
        using var crew = Crew(imdb);
        using var cancel = new CancellationTokenSource();
        imdb.Halfway = cancel.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => crew.RefreshAsync(null, cancel.Token));

        Assert.Null(crew.Status(new PluginConfiguration()).LastError);
        Assert.False(File.Exists(CopyPath + ".new"));
    }

    private ImdbCrewData Crew(Imdb imdb, int minimumTitles = 0) =>
        new(new Factory(imdb), new NullThemeForgeLogger<ImdbCrewData>(), CopyPath) { MinimumTitles = minimumTitles };

    private static byte[] Gzip(string text)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            gzip.Write(bytes, 0, bytes.Length);
        }

        return buffer.ToArray();
    }

    /// <summary>Serves IMDb's three files, or fails one of them on request.</summary>
    private sealed class Imdb : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _files = new()
        {
            [ImdbCrewDataset.PrincipalsFile] = Gzip(ImdbCrewDatasetTests.Principals),
            [ImdbCrewDataset.EpisodesFile] = Gzip(ImdbCrewDatasetTests.Episodes),
            [ImdbCrewDataset.NamesFile] = Gzip(ImdbCrewDatasetTests.Names),
        };

        public string? Broken { get; set; }

        public Action<string>? OnRequest { get; set; }

        /// <summary>Gets or sets what happens halfway through sending title.principals.</summary>
        public Action? Halfway { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var file = request.RequestUri!.Segments[^1];
            Assert.StartsWith(ImdbCrewDataset.BaseUrl, request.RequestUri.ToString(), StringComparison.Ordinal);
            OnRequest?.Invoke(file);
            cancellationToken.ThrowIfCancellationRequested();

            if (file == Broken || !_files.TryGetValue(file, out var bytes))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { ReasonPhrase = "Service Unavailable" });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = Halfway is not null && file == ImdbCrewDataset.PrincipalsFile
                    ? new StreamContent(new InterruptedStream(bytes, Halfway))
                    : new ByteArrayContent(bytes),
            };
            response.Content.Headers.LastModified = Published;
            response.Content.Headers.ContentLength = bytes.Length;
            return Task.FromResult(response);
        }
    }

    /// <summary>Sends half its bytes, then runs an action and behaves as a closed connection.</summary>
    private sealed class InterruptedStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly Action _halfway;
        private readonly int _half;
        private int _position;
        private bool _closed;

        public InterruptedStream(byte[] bytes, Action halfway)
        {
            _bytes = bytes;
            _halfway = halfway;
            _half = bytes.Length / 2;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_closed)
            {
                throw new ObjectDisposedException("the connection");
            }

            if (_position >= _half)
            {
                _closed = true;
                _halfway();
                throw new ObjectDisposedException("the connection");
            }

            var take = Math.Min(count, _half - _position);
            Array.Copy(_bytes, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(Read(buffer, offset, count));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Factory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public Factory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class Recorder : IProgress<double>
    {
        private readonly List<double> _seen;

        public Recorder(List<double> seen) => _seen = seen;

        public void Report(double value)
        {
            lock (_seen)
            {
                _seen.Add(value);
            }
        }
    }
}
