using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>The loudness ffmpeg measured in a source file, in EBU R128 terms.</summary>
/// <param name="IntegratedLufs">Integrated loudness.</param>
/// <param name="TruePeakDb">True peak.</param>
/// <param name="LoudnessRange">Loudness range.</param>
/// <param name="ThresholdLufs">Gating threshold.</param>
/// <param name="TargetOffset">Offset ffmpeg suggests for the second pass.</param>
public sealed record LoudnessMeasurement(
    double IntegratedLufs,
    double TruePeakDb,
    double LoudnessRange,
    double ThresholdLufs,
    double TargetOffset);

/// <summary>Encodes a source file into the finished theme.</summary>
public interface ILoudnessNormalizer
{
    /// <summary>
    /// Encodes a downloaded file into a normalised, faded, trimmed MP3.
    /// </summary>
    /// <param name="sourcePath">The downloaded audio.</param>
    /// <param name="targetPath">Where to write the finished MP3.</param>
    /// <param name="sourceDuration">The source duration, used to place the fade-out.</param>
    /// <param name="configuration">Settings supplying the loudness target, fades and bitrate.</param>
    /// <param name="tag">Metadata written into the file, for traceability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The measured input loudness, or null when normalisation was skipped or unavailable.</returns>
    Task<LoudnessMeasurement?> EncodeAsync(
        string sourcePath,
        string targetPath,
        double sourceDuration,
        PluginConfiguration configuration,
        ThemeTag tag,
        CancellationToken cancellationToken);
}

/// <summary>Metadata written into the finished theme so its origin stays recoverable.</summary>
/// <param name="Title">Track title.</param>
/// <param name="SourceUrl">Where the audio came from.</param>
public sealed record ThemeTag(string Title, string SourceUrl);

/// <summary>
/// Produces the finished theme file: loudness-normalised, faded, optionally trimmed, and
/// encoded to a strictly-specified MP3.
/// </summary>
/// <remarks>
/// <para>
/// This is where ThemeForge solves the volume problem, and it solves it in the file rather than
/// at playback. Jellyfin plays themes through the same player as everything else and sets no
/// volume of its own, so a theme is as loud as whoever mastered it. A plugin cannot fix that
/// from the client either: the web player reloads its saved global volume whenever it creates a
/// media element, so anything written there is overwritten moments later, and writing it at all
/// leaks into the user's volume for normal playback. Normalising every theme to the same
/// integrated loudness before it is ever written removes the problem instead of fighting it,
/// and works identically on every client — including the ones no script can reach.
/// </para>
/// <para>
/// Two passes are used because single-pass loudnorm is a dynamic compressor that reacts as it
/// goes; measuring first and then applying a fixed correction preserves the dynamics of the
/// recording, which matters for orchestral main titles.
/// </para>
/// </remarks>
public sealed class LoudnessNormalizer : ILoudnessNormalizer
{
    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<LoudnessNormalizer> _logger;

    /// <summary>Initializes a new instance of the <see cref="LoudnessNormalizer"/> class.</summary>
    /// <param name="toolProvisioner">Supplies the ffmpeg path.</param>
    /// <param name="processRunner">Runs ffmpeg.</param>
    /// <param name="logger">Logger.</param>
    public LoudnessNormalizer(IToolProvisioner toolProvisioner, IProcessRunner processRunner, ILogger<LoudnessNormalizer> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LoudnessMeasurement?> EncodeAsync(
        string sourcePath,
        string targetPath,
        double sourceDuration,
        PluginConfiguration configuration,
        ThemeTag tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tag);

        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);

        LoudnessMeasurement? measurement = null;
        if (configuration.EnableLoudnessNormalization)
        {
            measurement = await MeasureAsync(tools.Ffmpeg, sourcePath, configuration, cancellationToken).ConfigureAwait(false);
            if (measurement is null)
            {
                _logger.LogWarning(
                    "ThemeForge: could not measure the loudness of {Path}; encoding without normalisation, so this theme may not match the others in volume.",
                    sourcePath);
            }
        }

        var outputDuration = ResolveOutputDuration(sourceDuration, configuration.MaxThemeSeconds);
        var filters = BuildFilterChain(measurement, configuration, outputDuration);

        var arguments = new List<string> { "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-i", sourcePath };

        if (configuration.MaxThemeSeconds > 0 && sourceDuration > configuration.MaxThemeSeconds)
        {
            arguments.AddRange(new[] { "-t", configuration.MaxThemeSeconds.ToString(CultureInfo.InvariantCulture) });
        }

        if (filters.Count > 0)
        {
            arguments.AddRange(new[] { "-af", string.Join(',', filters) });
        }

        arguments.AddRange(new[]
        {
            // Take exactly one audio stream and nothing else, so embedded cover art cannot end
            // up as a video stream that some clients then refuse to play.
            "-map", "0:a:0",
            "-vn",
            "-map_metadata", "-1",

            // Pin every property of the output. Letting ffmpeg infer these from whatever
            // container YouTube served is how plugin-written MP3s end up without a proper
            // frame header and fail to play on stricter clients.
            "-c:a", "libmp3lame",
            "-b:a", configuration.AudioBitrate.ToString(CultureInfo.InvariantCulture) + "k",
            "-ar", "44100",
            "-ac", "2",
            "-write_xing", "1",
            "-id3v2_version", "3",
            "-metadata", "title=" + tag.Title,
            "-metadata", "comment=Downloaded by ThemeForge from " + tag.SourceUrl,
            targetPath,
        });

        var result = await _processRunner
            .RunAsync(tools.Ffmpeg, arguments, EncodeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException($"ffmpeg could not encode the theme: {result.ErrorSummary}");
        }

        return measurement;
    }

    /// <summary>Runs the analysis pass, which reports the source's loudness without writing audio.</summary>
    private async Task<LoudnessMeasurement?> MeasureAsync(
        string ffmpeg,
        string sourcePath,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(
            ffmpeg,
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "info",
                "-i", sourcePath,
                "-af", BuildLoudnormFilter(configuration, measured: null) + ":print_format=json",
                "-f", "null",
                "-",
            },
            AnalysisTimeout,
            cancellationToken).ConfigureAwait(false);

        // loudnorm writes its JSON report to stderr, after the usual progress output.
        return ParseMeasurement(result.StandardError);
    }

    /// <summary>Extracts the loudnorm JSON report from ffmpeg's stderr.</summary>
    /// <param name="stderr">Everything ffmpeg wrote to stderr.</param>
    /// <returns>The measurement, or null when no complete report was found.</returns>
    internal static LoudnessMeasurement? ParseMeasurement(string stderr)
    {
        if (string.IsNullOrEmpty(stderr))
        {
            return null;
        }

        var start = stderr.LastIndexOf('{');
        var end = stderr.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(stderr[start..(end + 1)]);
            var root = document.RootElement;

            // loudnorm reports "-inf" for silent input, which parses as no measurement at all.
            if (!TryReadDouble(root, "input_i", out var integrated)
                || !TryReadDouble(root, "input_tp", out var truePeak)
                || !TryReadDouble(root, "input_lra", out var range)
                || !TryReadDouble(root, "input_thresh", out var threshold))
            {
                return null;
            }

            TryReadDouble(root, "target_offset", out var offset);
            return new LoudnessMeasurement(integrated, truePeak, range, threshold, offset);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        if (!root.TryGetProperty(name, out var element))
        {
            return false;
        }

        var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)
               && !double.IsInfinity(value)
               && !double.IsNaN(value);
    }

    /// <summary>Builds the loudnorm filter, in either measuring or applying form.</summary>
    private static string BuildLoudnormFilter(PluginConfiguration configuration, LoudnessMeasurement? measured)
    {
        var filter = string.Create(
            CultureInfo.InvariantCulture,
            $"loudnorm=I={configuration.TargetLoudnessLufs:0.##}:TP={configuration.TargetTruePeakDb:0.##}:LRA={configuration.TargetLoudnessRange:0.##}");

        if (measured is null)
        {
            return filter;
        }

        // Feeding the measured values back turns loudnorm from a dynamic compressor into a
        // fixed gain correction, which leaves the recording's own dynamics intact.
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{filter}:measured_I={measured.IntegratedLufs:0.##}:measured_TP={measured.TruePeakDb:0.##}:measured_LRA={measured.LoudnessRange:0.##}:measured_thresh={measured.ThresholdLufs:0.##}:offset={measured.TargetOffset:0.##}:linear=true");
    }

    /// <summary>Builds the full audio filter chain: normalisation first, then the fades.</summary>
    internal static List<string> BuildFilterChain(
        LoudnessMeasurement? measurement,
        PluginConfiguration configuration,
        double outputDuration)
    {
        var filters = new List<string>();

        if (configuration.EnableLoudnessNormalization && measurement is not null)
        {
            filters.Add(BuildLoudnormFilter(configuration, measurement));
        }

        if (configuration.FadeInSeconds > 0)
        {
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"afade=t=in:st=0:d={configuration.FadeInSeconds:0.###}"));
        }

        // The fade-out can only be placed if the final length is known, and it has to fit:
        // a fade longer than the track would start before zero and silence the whole thing.
        if (configuration.FadeOutSeconds > 0 && outputDuration > 0)
        {
            var fadeOut = Math.Min(configuration.FadeOutSeconds, outputDuration / 2);
            var start = outputDuration - fadeOut;
            if (start > 0)
            {
                filters.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"afade=t=out:st={start:0.###}:d={fadeOut:0.###}"));
            }
        }

        return filters;
    }

    /// <summary>Works out how long the finished file will be, given an optional length cap.</summary>
    internal static double ResolveOutputDuration(double sourceDuration, int maxSeconds) =>
        maxSeconds > 0 && sourceDuration > maxSeconds ? maxSeconds : sourceDuration;
}
