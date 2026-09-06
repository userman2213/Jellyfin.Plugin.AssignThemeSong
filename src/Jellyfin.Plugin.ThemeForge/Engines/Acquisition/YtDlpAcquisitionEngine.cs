using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>Downloads a candidate and turns it into a finished theme file.</summary>
public interface IAcquisitionEngine
{
    /// <summary>
    /// Downloads, normalises and verifies a candidate.
    /// </summary>
    /// <param name="candidate">The candidate to fetch.</param>
    /// <param name="configuration">Settings governing the encode.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The finished file, staged and verified but not yet in the library.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the download or encode fails, or the result does not verify.</exception>
    Task<AcquiredAudio> AcquireAsync(Candidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches a candidate's audio with yt-dlp and hands it to the normaliser.
/// </summary>
/// <remarks>
/// Work happens in a per-attempt staging directory and the result is verified before anyone
/// else is told about it, so a partial download or a failed encode can never reach the library.
/// The download deliberately asks for the best available audio rather than letting yt-dlp
/// produce an MP3: that would mean encoding to MP3 twice, once by yt-dlp and again during
/// normalisation, and throwing away quality for no reason.
/// </remarks>
public sealed class YtDlpAcquisitionEngine : IAcquisitionEngine
{
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    /// <summary>A theme is a couple of megabytes; anything far larger is not one.</summary>
    private const long MaxDirectDownloadBytes = 64L * 1024 * 1024;

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly ILoudnessNormalizer _normalizer;
    private readonly IAudioProbe _probe;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<YtDlpAcquisitionEngine> _logger;

    /// <summary>Initializes a new instance of the <see cref="YtDlpAcquisitionEngine"/> class.</summary>
    /// <param name="toolProvisioner">Supplies yt-dlp and ffmpeg.</param>
    /// <param name="processRunner">Runs yt-dlp.</param>
    /// <param name="normalizer">Encodes the finished theme.</param>
    /// <param name="probe">Verifies the finished theme.</param>
    /// <param name="httpClientFactory">Fetches candidates that are already audio files.</param>
    /// <param name="logger">Logger.</param>
    public YtDlpAcquisitionEngine(
        IToolProvisioner toolProvisioner,
        IProcessRunner processRunner,
        ILoudnessNormalizer normalizer,
        IAudioProbe probe,
        IHttpClientFactory httpClientFactory,
        IThemeForgeLogger<YtDlpAcquisitionEngine> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _normalizer = normalizer;
        _probe = probe;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AcquiredAudio> AcquireAsync(
        Candidate candidate,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(configuration);

        var stagingRoot = Plugin.Instance?.StagingPath ?? Path.Combine(Path.GetTempPath(), "themeforge-staging");
        var workingDirectory = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var downloaded = candidate.IsDirectAudio
                ? await FetchAsync(candidate, workingDirectory, cancellationToken).ConfigureAwait(false)
                : await DownloadAsync(candidate, workingDirectory, cancellationToken).ConfigureAwait(false);

            var sourceProbe = await _probe.ProbeAsync(downloaded, cancellationToken).ConfigureAwait(false);
            if (!sourceProbe.IsValid)
            {
                throw new InvalidOperationException($"the downloaded audio is unusable: {sourceProbe.Problem}");
            }

            var finalPath = Path.Combine(workingDirectory, "theme.mp3");
            var measurement = await _normalizer.EncodeAsync(
                downloaded,
                finalPath,
                sourceProbe.DurationSeconds,
                configuration,
                new ThemeTag(candidate.Title, candidate.Url),
                cancellationToken).ConfigureAwait(false);

            // The gate: nothing leaves this method unless ffprobe can read what was produced.
            var finalProbe = await _probe.ProbeAsync(finalPath, cancellationToken).ConfigureAwait(false);
            if (!finalProbe.IsValid)
            {
                throw new InvalidOperationException($"the encoded theme did not verify: {finalProbe.Problem}");
            }

            if (finalProbe.HasVideoStream)
            {
                throw new InvalidOperationException("the encoded theme unexpectedly contains a video stream");
            }

            var info = new FileInfo(finalPath);
            _logger.LogInformation(
                "ThemeForge: acquired \"{Title}\" — {Duration:0}s, {Size:N0} bytes{Loudness}.",
                candidate.Title,
                finalProbe.DurationSeconds,
                info.Length,
                measurement is null ? string.Empty : string.Create(CultureInfo.InvariantCulture, $", input {measurement.IntegratedLufs:0.#} LUFS normalised to {configuration.TargetLoudnessLufs:0.#}"));

            return new AcquiredAudio
            {
                StagingPath = finalPath,
                DurationSeconds = finalProbe.DurationSeconds,
                MeasuredLoudnessLufs = measurement?.IntegratedLufs,
                Sha256 = await FileHash.ComputeSha256Async(finalPath, cancellationToken).ConfigureAwait(false),
                SizeBytes = info.Length,
                SourceUrl = candidate.Url,
            };
        }
        catch
        {
            TryCleanUp(workingDirectory);
            throw;
        }
    }

    /// <summary>Runs yt-dlp and returns the file it produced.</summary>
    private async Task<string> DownloadAsync(Candidate candidate, string workingDirectory, CancellationToken cancellationToken)
    {
        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);

        var arguments = new List<string>
        {
            "--no-playlist",
            "--no-progress",
            "--no-color",
            "--no-warnings",
            "--socket-timeout", "30",
            "--retries", "5",

            // Audio only, best quality available, left in its delivered format so that
            // normalisation is the only lossy step.
            "-f", "bestaudio/best",
            "--ffmpeg-location", tools.Ffmpeg,
            "-o", Path.Combine(workingDirectory, "source.%(ext)s"),
            candidate.Url,
        };

        var result = await _processRunner
            .RunAsync(tools.YtDlp, arguments, DownloadTimeout, cancellationToken)
            .ConfigureAwait(false);

        // yt-dlp chooses the extension from the delivered format, so the produced file is found
        // by looking rather than by assuming.
        var produced = Directory
            .EnumerateFiles(workingDirectory, "source.*")
            .Where(path => !path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => new FileInfo(path).Length)
            .FirstOrDefault();

        if (produced is null)
        {
            throw new InvalidOperationException($"yt-dlp downloaded nothing: {result.ErrorSummary}");
        }

        return produced;
    }


    /// <summary>
    /// Fetches a candidate that is already an audio file.
    /// </summary>
    /// <remarks>
    /// A catalogue that serves the audio itself has no page to extract from, so running yt-dlp
    /// over it would only add a process launch and a dependency to a plain HTTP GET. The file
    /// still goes through the same probe, normalisation and verification as everything else.
    /// </remarks>
    private async Task<string> FetchAsync(Candidate candidate, string workingDirectory, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(candidate.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"\"{candidate.Url}\" is not an https address");
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var response = await client
            .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"the source answered {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        if (response.Content.Headers.ContentLength > MaxDirectDownloadBytes)
        {
            throw new InvalidOperationException(
                $"the source offered {response.Content.Headers.ContentLength:N0} bytes, which is far too large for a theme");
        }

        var path = Path.Combine(workingDirectory, "source.mp3");

        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var file = File.Create(path))
        {
            await stream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }

        var length = new FileInfo(path).Length;
        if (length == 0)
        {
            throw new InvalidOperationException("the source returned an empty file");
        }

        if (length > MaxDirectDownloadBytes)
        {
            // Checked again after the fact: a chunked response has no length to check up front.
            throw new InvalidOperationException($"the source returned {length:N0} bytes, which is far too large for a theme");
        }

        _logger.LogDebug("ThemeForge: fetched {Bytes:N0} bytes directly from {Url}.", length, candidate.Url);
        return path;
    }

    private void TryCleanUp(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not clean up the staging directory {Directory}.", directory);
        }
    }
}
