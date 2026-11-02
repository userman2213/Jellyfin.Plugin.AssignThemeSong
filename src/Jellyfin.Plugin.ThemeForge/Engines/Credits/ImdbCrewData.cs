using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>What the local copy of IMDb's crew data holds, for the settings page and Diagnostics.</summary>
/// <param name="Enabled">Whether it is switched on.</param>
/// <param name="Available">Whether there is a copy to look things up in.</param>
/// <param name="Problem">Why there is no usable copy, when there is not.</param>
/// <param name="PublishedUtc">When IMDb published the files the copy was built from.</param>
/// <param name="DownloadedUtc">When the copy was built.</param>
/// <param name="Titles">How many titles it names a composer for.</param>
/// <param name="Series">How many of those are series, from their episodes.</param>
/// <param name="FileBytes">The size of the copy on disk.</param>
/// <param name="Refreshing">Whether a download is under way.</param>
/// <param name="LastError">Why the last download failed, when it did.</param>
public sealed record ImdbCrewStatus(
    bool Enabled,
    bool Available,
    string? Problem,
    DateTime? PublishedUtc,
    DateTime? DownloadedUtc,
    int Titles,
    int Series,
    long FileBytes,
    bool Refreshing,
    string? LastError);

/// <summary>IMDb's own crew data: who IMDb credits as the composer of each title.</summary>
public interface IImdbCrewData
{
    /// <summary>Finds the composers IMDb credits on a title.</summary>
    /// <remarks>
    /// Synchronous, because identity is resolved synchronously, and cheap: a few small reads from
    /// a file on disk, remembered afterwards. Whether the data is switched on is for the caller.
    /// </remarks>
    /// <param name="imdbId">The title's IMDb id.</param>
    /// <returns>The composers, or nothing when none are known or there is no copy.</returns>
    IReadOnlyList<string> Composers(string? imdbId);

    /// <summary>Describes the local copy.</summary>
    /// <param name="configuration">The settings.</param>
    /// <returns>Its state.</returns>
    ImdbCrewStatus Status(PluginConfiguration configuration);

    /// <summary>Reports whether a new copy should be downloaded, and why or why not.</summary>
    /// <param name="configuration">The settings.</param>
    /// <param name="nowUtc">The time now.</param>
    /// <param name="reason">Why, either way.</param>
    /// <returns><see langword="true"/> when one should.</returns>
    bool IsDue(PluginConfiguration configuration, DateTime nowUtc, out string reason);

    /// <summary>Downloads IMDb's files and replaces the local copy.</summary>
    /// <param name="progress">Progress, 0 to 100, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when the copy was replaced; the old one is kept otherwise.</returns>
    Task<bool> RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps a local copy of who IMDb credits as the composer of each title, built from IMDb's
/// published dataset files.
/// </summary>
/// <remarks>
/// <para>
/// This is what names the composer of a film whose IMDb soundtrack listing only lists songs. The
/// 2026 war film Lucky Strike is one: its listing names a western song that shares its title, and
/// IMDb's crew credits name Larry Groupé as its composer. Looked up by the title's IMDb id, so
/// nothing is matched by name.
/// </para>
/// <para>
/// About 1.2 GB is downloaded and read as it arrives; nothing but the composer credits is kept,
/// in a file of about 20 MB read from disk a few bytes at a time. A new copy is downloaded once a
/// month, or after a week when a title in the library is newer than the copy -- IMDb numbers its
/// titles in the order they are added, so a title numbered higher than any in the copy was added
/// after it. A download that fails or is cancelled leaves the previous copy in place.
/// </para>
/// </remarks>
public sealed class ImdbCrewData : IImdbCrewData, IDisposable
{
    /// <summary>How long a copy is used before a new one is downloaded.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>How old a copy must be before a newer title in the library is reason to replace it.</summary>
    public static readonly TimeSpan NewerTitleWait = TimeSpan.FromDays(7);

    /// <summary>Each file's share of the progress, by size.</summary>
    private static readonly (string File, double Share)[] Files =
    {
        (ImdbCrewDataset.PrincipalsFile, 0.68),
        (ImdbCrewDataset.EpisodesFile, 0.05),
        (ImdbCrewDataset.NamesFile, 0.27),
    };

    /// <summary>How long a download may go without receiving anything before it is given up.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(2);

    /// <summary>How long after a failed download the next is tried, so a restart is not a 1.2 GB retry.</summary>
    private static readonly TimeSpan AfterFailure = TimeSpan.FromHours(20);

    /// <summary>Lookups remembered at most; far more than any library holds.</summary>
    private const int MaxRemembered = 100_000;

    /// <summary>About how many composer credits IMDb's files hold: 3.25 million on 1 November 2026.</summary>
    private const int ExpectedCredits = 3_600_000;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<ImdbCrewData> _logger;
    private readonly string? _path;
    private readonly ReaderWriterLockSlim _lock = new();
    private readonly SemaphoreSlim _refreshing = new(1, 1);
    private readonly ConcurrentDictionary<int, IReadOnlyList<string>> _remembered = new();

    private ImdbCrewFile? _file;
    private string? _problem;
    private volatile bool _opened;
    private volatile bool _busy;
    private long _newerTitleSeenTicks;
    private long _failedTicks;
    private string? _lastError;

    /// <summary>Initializes a new instance of the <see cref="ImdbCrewData"/> class.</summary>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ImdbCrewData(IHttpClientFactory httpClientFactory, IThemeForgeLogger<ImdbCrewData> logger)
        : this(httpClientFactory, logger, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ImdbCrewData"/> class, keeping its copy somewhere specific.</summary>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="path">Where the copy is kept, or null for beside the index.</param>
    internal ImdbCrewData(IHttpClientFactory httpClientFactory, IThemeForgeLogger<ImdbCrewData> logger, string? path)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _path = path;
    }

    /// <summary>Gets where the copy is kept by default, beside the index so it survives upgrades.</summary>
    public static string DefaultPath =>
        Path.Combine(Plugin.Instance?.DataPath ?? Path.GetTempPath(), "imdb-composers.bin");

    /// <summary>Gets or sets the fewest titles a download must yield to be trusted.</summary>
    /// <remarks>
    /// The real files yield nearly a million. A few thousand would mean IMDb served something other
    /// than its files, and replacing a good copy with that would lose every composer at once.
    /// </remarks>
    internal int MinimumTitles { get; set; } = 100_000;

    private string FilePath => _path ?? DefaultPath;

    /// <inheritdoc />
    public IReadOnlyList<string> Composers(string? imdbId)
    {
        var title = ImdbCrewDataset.ParseTitleId(imdbId);
        if (title < 0)
        {
            return Array.Empty<string>();
        }

        if (_remembered.TryGetValue(title, out var known))
        {
            return known;
        }

        EnsureOpened();

        _lock.EnterReadLock();
        try
        {
            var file = _file;
            if (file is null)
            {
                return Array.Empty<string>();
            }

            IReadOnlyList<string> composers;
            try
            {
                composers = file.Composers(title);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "ThemeForge: could not read IMDb's crew data for {Title}.", imdbId);
                return Array.Empty<string>();
            }

            if (composers.Count == 0 && title > file.NewestTitle)
            {
                // Added to IMDb after the copy was made, so the copy cannot know.
                Interlocked.CompareExchange(ref _newerTitleSeenTicks, DateTime.UtcNow.Ticks, 0);
            }

            if (_remembered.Count < MaxRemembered)
            {
                _remembered[title] = composers;
            }

            return composers;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public ImdbCrewStatus Status(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        EnsureOpened();

        _lock.EnterReadLock();
        try
        {
            var file = _file;
            return new ImdbCrewStatus(
                configuration.ResearchComposers && configuration.UseImdbCrewData,
                file is not null,
                file is null ? _problem : null,
                file?.PublishedUtc,
                file?.BuiltUtc,
                file?.TitleCount ?? 0,
                file?.SeriesCount ?? 0,
                file?.Length ?? 0,
                _busy,
                _lastError);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    public bool IsDue(PluginConfiguration configuration, DateTime nowUtc, out string reason)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.ResearchComposers || !configuration.UseImdbCrewData)
        {
            reason = "using IMDb's crew data is switched off";
            return false;
        }

        var failed = Interlocked.Read(ref _failedTicks);
        if (failed != 0 && nowUtc - new DateTime(failed, DateTimeKind.Utc) < AfterFailure)
        {
            reason = string.Format(
                CultureInfo.InvariantCulture,
                "the last download failed at {0:u} ({1}), and the next is tried {2} hours after that",
                new DateTime(failed, DateTimeKind.Utc),
                _lastError,
                (int)AfterFailure.TotalHours);
            return false;
        }

        var status = Status(configuration);
        if (!status.Available)
        {
            reason = "there is no copy yet";
            return true;
        }

        var age = nowUtc - status.DownloadedUtc!.Value;
        if (age >= Lifetime)
        {
            reason = string.Format(CultureInfo.InvariantCulture, "the copy is {0} days old", (int)age.TotalDays);
            return true;
        }

        if (Interlocked.Read(ref _newerTitleSeenTicks) != 0 && age >= NewerTitleWait)
        {
            reason = string.Format(
                CultureInfo.InvariantCulture,
                "the library has a title added to IMDb after the copy was made, and the copy is {0} days old",
                (int)age.TotalDays);
            return true;
        }

        reason = string.Format(
            CultureInfo.InvariantCulture,
            "the copy is {0} days old, and a new one is due in {1} days",
            (int)age.TotalDays,
            Math.Max(1, (int)Math.Ceiling((Lifetime - age).TotalDays)));
        return false;
    }

    /// <inheritdoc />
    public async Task<bool> RefreshAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (!await _refreshing.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogInformation("ThemeForge: IMDb's crew data is already being downloaded.");
            return false;
        }

        _busy = true;
        var temporary = FilePath + ".new";
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            // A download of this size takes as long as the connection makes it take; a stalled
            // one is caught by the watchdog in Download instead.
            client.Timeout = Timeout.InfiniteTimeSpan;

            _logger.LogInformation("ThemeForge: downloading IMDb's crew data, about 1.2 GB; only the composer credits are kept.");

            var done = 0.0;
            List<CrewCredit> credits;
            int newest;
            DateTime published;
            using (var download = await Download.StartAsync(client, Files[0].File, Part(progress, done, Files[0].Share), cancellationToken).ConfigureAwait(false))
            {
                published = download.LastModifiedUtc ?? DateTime.UtcNow;
                (credits, newest) = await Task.Run(
                    () => download.Read(content =>
                    {
                        var read = ImdbCrewDataset.ReadComposerCredits(content, out var highest, cancellationToken, ExpectedCredits);
                        return (read, highest);
                    }),
                    cancellationToken).ConfigureAwait(false);
            }

            done += Files[0].Share;
            _logger.LogInformation(
                "ThemeForge: IMDb's crew data credits {Count:N0} composers; working out which are on episodes.",
                credits.Count);

            List<(int Episode, int Series)> episodeSeries;
            using (var download = await Download.StartAsync(client, Files[1].File, Part(progress, done, Files[1].Share), cancellationToken).ConfigureAwait(false))
            {
                episodeSeries = await Task.Run(
                    () => download.Read(content => ImdbCrewDataset.ReadSeriesOfCreditedEpisodes(content, credits, cancellationToken)),
                    cancellationToken).ConfigureAwait(false);
            }

            done += Files[1].Share;
            var composed = ImdbCrewDataset.Compose(credits, episodeSeries);
            credits = null!;
            episodeSeries = null!;

            // Several hundred megabytes of lists are garbage from here on. Collected now, once a
            // month, rather than left for the server to carry until it next gets round to it.
            GC.Collect();

            var wanted = composed.People.Distinct().ToArray();
            Array.Sort(wanted);

            Dictionary<int, string> names;
            using (var download = await Download.StartAsync(client, Files[2].File, Part(progress, done, Files[2].Share), cancellationToken).ConfigureAwait(false))
            {
                names = await Task.Run(
                    () => download.Read(content => ImdbCrewDataset.ReadNames(content, wanted, cancellationToken)),
                    cancellationToken).ConfigureAwait(false);
            }

            var table = ImdbCrewDataset.Assemble(composed, names, newest, published);
            if (table.Titles.Length < MinimumTitles)
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "IMDb's files named composers for only {0} titles, far fewer than they should; the copy was not replaced",
                    table.Titles.Length));
            }

            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            {
                ImdbCrewDataset.Write(table, output, DateTime.UtcNow);
                output.Flush(flushToDisk: true);
            }

            Replace(temporary);

            Interlocked.Exchange(ref _newerTitleSeenTicks, 0);
            Interlocked.Exchange(ref _failedTicks, 0);
            _lastError = null;
            progress?.Report(100);

            _logger.LogInformation(
                "ThemeForge: IMDb's crew data, published {Published:u}, names the composers of {Titles:N0} titles, {Series:N0} of them series credited through their episodes ({Size:N1} MB).",
                published,
                table.Titles.Length,
                table.SeriesCount,
                new FileInfo(FilePath).Length / 1048576.0);
            return true;
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            // Cancelling closes the connection, so a read under way fails with whatever closing it
            // throws; it is still a cancellation, not a failure.
            _logger.LogInformation("ThemeForge: the download of IMDb's crew data was cancelled; the previous copy is kept.");
            throw ex as OperationCanceledException ?? new OperationCanceledException("The download of IMDb's crew data was cancelled.", ex, cancellationToken);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Interlocked.Exchange(ref _failedTicks, DateTime.UtcNow.Ticks);
            _logger.LogWarning(ex, "ThemeForge: could not download IMDb's crew data; the previous copy is kept.");
            return false;
        }
        finally
        {
            _busy = false;
            TryDelete(temporary);
            _refreshing.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _lock.EnterWriteLock();
        try
        {
            _file?.Dispose();
            _file = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }

        _lock.Dispose();
        _refreshing.Dispose();
    }

    private static IProgress<double>? Part(IProgress<double>? progress, double before, double share) =>
        progress is null ? null : new Progress<double>(within => progress.Report(100 * (before + (share * within / 100))));

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Overwritten by the next download.
        }
    }

    private void EnsureOpened()
    {
        if (_opened)
        {
            return;
        }

        _lock.EnterWriteLock();
        try
        {
            if (_opened)
            {
                return;
            }

            try
            {
                _file = ImdbCrewFile.Open(FilePath, out _problem);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _file = null;
                _problem = ex.Message;
                _logger.LogWarning(ex, "ThemeForge: could not open IMDb's crew data.");
            }

            _opened = true;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Puts a new copy in place of the old, which stays if the new one cannot be opened.</summary>
    private void Replace(string temporary)
    {
        _lock.EnterWriteLock();
        try
        {
            // Closed first: Windows will not replace a file that is open.
            _file?.Dispose();
            _file = null;

            try
            {
                File.Move(temporary, FilePath, overwrite: true);
            }
            finally
            {
                _file = ImdbCrewFile.Open(FilePath, out _problem);
                _opened = true;
                _remembered.Clear();
            }

            if (_file is null)
            {
                throw new InvalidDataException("The new copy of IMDb's crew data could not be opened: " + _problem + ".");
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>One file being downloaded and decompressed as it arrives.</summary>
    private sealed class Download : IDisposable
    {
        private readonly HttpResponseMessage _response;
        private readonly CountingStream _counted;
        private readonly Timer _watchdog;
        private readonly CancellationTokenRegistration _cancellation;

        private Download(HttpResponseMessage response, Stream network, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            _response = response;
            _counted = new CountingStream(network, response.Content.Headers.ContentLength, progress);
            Content = new GZipStream(_counted, CompressionMode.Decompress);
            LastModifiedUtc = response.Content.Headers.LastModified?.UtcDateTime;

            // A read from a connection that has gone quiet would wait for ever; closing the
            // response is what makes it give up.
            _watchdog = new Timer(_ => GiveUpIfStalled(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
            _cancellation = cancellationToken.Register(() => _response.Dispose());
        }

        public Stream Content { get; }

        public DateTime? LastModifiedUtc { get; }

        /// <summary>Gets a value indicating whether the connection was closed for going quiet.</summary>
        public bool Stalled { get; private set; }

        /// <summary>Reads the content, saying so plainly when it failed because the connection went quiet.</summary>
        public T Read<T>(Func<Stream, T> read)
        {
            try
            {
                return read(Content);
            }
            catch (Exception ex) when (Stalled)
            {
                throw new IOException(
                    string.Format(CultureInfo.InvariantCulture, "IMDb stopped sending data for {0} minutes", (int)Stall.TotalMinutes),
                    ex);
            }
        }

        public static async Task<Download> StartAsync(
            HttpClient client,
            string file,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ImdbCrewDataset.BaseUrl + file);
            PoliteRequest.Identify(request);

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(string.Format(
                        CultureInfo.InvariantCulture,
                        "IMDb answered {0} {1} for {2}",
                        (int)response.StatusCode,
                        response.ReasonPhrase,
                        file));
                }

                var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return new Download(response, network, progress, cancellationToken);
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            _cancellation.Dispose();
            _watchdog.Dispose();
            Content.Dispose();
            _response.Dispose();
        }

        private void GiveUpIfStalled()
        {
            if (DateTime.UtcNow - _counted.LastReadUtc > Stall)
            {
                Stalled = true;
                _response.Dispose();
            }
        }
    }

    /// <summary>Counts what has arrived, to report progress and notice a stalled connection.</summary>
    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;
        private readonly long? _length;
        private readonly IProgress<double>? _progress;
        private long _read;
        private long _reported;

        public CountingStream(Stream inner, long? length, IProgress<double>? progress)
        {
            _inner = inner;
            _length = length;
            _progress = progress;
            LastReadUtc = DateTime.UtcNow;
        }

        public DateTime LastReadUtc { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Counted(_inner.Read(buffer, offset, count));

        public override int Read(Span<byte> buffer) => Counted(_inner.Read(buffer));

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private int Counted(int read)
        {
            _read += read;
            LastReadUtc = DateTime.UtcNow;

            if (_progress is not null && _length is > 0 && _read - _reported >= 4 << 20)
            {
                _reported = _read;
                _progress.Report(Math.Min(100, 100.0 * _read / _length.Value));
            }

            return read;
        }
    }
}
