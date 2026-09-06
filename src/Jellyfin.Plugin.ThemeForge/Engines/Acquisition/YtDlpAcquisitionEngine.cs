using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
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

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly ILoudnessNormalizer _normalizer;
    private readonly IAudioProbe _probe;
    private readonly IThemeForgeLogger<YtDlpAcquisitionEngine> _logger;

    /// <summary>Initializes a new instance of the <see cref="YtDlpAcquisitionEngine"/> class.</summary>
    /// <param name="toolProvisioner">Supplies yt-dlp and ffmpeg.</param>
    /// <param name="processRunner">Runs yt-dlp.</param>
    /// <param name="normalizer">Encodes the finished theme.</param>
    /// <param name="probe">Verifies the finished theme.</param>
    /// <param name="logger">Logger.</param>
    public YtDlpAcquisitionEngine(
        IToolProvisioner toolProvisioner,
        IProcessRunner processRunner,
        ILoudnessNormalizer normalizer,
        IAudioProbe probe,
        IThemeForgeLogger<YtDlpAcquisitionEngine> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _normalizer = normalizer;
        _probe = probe;
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
            var downloaded = await DownloadAsync(candidate, workingDirectory, cancellationToken).ConfigureAwait(false);

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
