using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

/// <summary>Metadata written into the finished theme so its origin stays recoverable.</summary>
/// <param name="Title">Track title.</param>
/// <param name="SourceUrl">Where the audio came from.</param>
public sealed record ThemeTag(string Title, string SourceUrl);

/// <summary>What the encoder produced.</summary>
/// <param name="Path">The finished file. Its extension says what is in it.</param>
/// <param name="Measurement">The source's loudness, when a setting asked for it to be measured.</param>
/// <param name="Treatment">What was done to the audio, in a few words, for the log.</param>
public sealed record EncodedTheme(string Path, LoudnessMeasurement? Measurement, string Treatment);

/// <summary>Turns a downloaded file into the finished theme.</summary>
public interface IThemeEncoder
{
    /// <summary>
    /// Writes the finished theme into <paramref name="outputDirectory"/> as <c>theme.&lt;ext&gt;</c>,
    /// where the extension depends on whether the audio could be kept as it was.
    /// </summary>
    /// <param name="sourcePath">The downloaded audio.</param>
    /// <param name="outputDirectory">The staging directory to write into.</param>
    /// <param name="source">What ffprobe found in the download: its codec and its length.</param>
    /// <param name="configuration">Settings deciding what, if anything, is done to the audio.</param>
    /// <param name="tag">Metadata written into the file, for traceability.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Where the theme was written and what was done to it.</returns>
    Task<EncodedTheme> EncodeAsync(
        string sourcePath,
        string outputDirectory,
        AudioProbeResult source,
        PluginConfiguration configuration,
        ThemeTag tag,
        CancellationToken cancellationToken);
}

/// <summary>How the encoder intends to treat one file.</summary>
/// <param name="Copy">Whether the audio stream is copied untouched rather than decoded and re-encoded.</param>
/// <param name="Extension">The output extension, chosen by what the file will contain.</param>
/// <param name="Filters">The ffmpeg audio filters to apply, in order. Empty for a copy.</param>
/// <param name="CutToSeconds">A length cap that applies, or null.</param>
/// <param name="Treatment">A description of the treatment, for the log.</param>
internal sealed record EncodePlan(
    bool Copy,
    string Extension,
    IReadOnlyList<string> Filters,
    int? CutToSeconds,
    string Treatment);

/// <summary>
/// Produces the finished theme file: by default a copy of the audio exactly as it was delivered,
/// and only when a setting asks for it a processed, re-encoded MP3.
/// </summary>
/// <remarks>
/// <para>
/// yt-dlp already fetches the best audio stream there is, so with nothing to change the right
/// thing to do is to keep it: the stream is copied into a container Jellyfin recognises
/// (<c>theme.opus</c>, <c>theme.m4a</c>, <c>theme.mp3</c>, …) with its volume, its dynamics and
/// every bit of its quality intact. Re-encoding an Opus or AAC stream to MP3 for no reason is a
/// second lossy step, and normalising it to a broadcast level made most uploads noticeably
/// quieter than the rest of the library.
/// </para>
/// <para>
/// When something does have to change — a quiet theme to be raised, silence to trim, a fade, a
/// length cap, or normalisation for people who want one level across the library — the audio is
/// decoded, filtered and written as a strictly specified MP3, because that is the one format every
/// client plays without help. Raising is a plain gain and never lowers anything; normalisation is
/// two-pass, measured first and then applied as a fixed correction, so the recording's own
/// dynamics survive.
/// </para>
/// </remarks>
public sealed class ThemeEncoder : IThemeEncoder
{
    /// <summary>The least a quiet theme is raised by. Anything smaller is not worth a re-encode.</summary>
    internal const double MinimumRaiseDb = 0.5;

    private static readonly TimeSpan AnalysisTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly IThemeForgeLogger<ThemeEncoder> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemeEncoder"/> class.</summary>
    /// <param name="toolProvisioner">Supplies the ffmpeg path.</param>
    /// <param name="processRunner">Runs ffmpeg.</param>
    /// <param name="logger">Logger.</param>
    public ThemeEncoder(IToolProvisioner toolProvisioner, IProcessRunner processRunner, IThemeForgeLogger<ThemeEncoder> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EncodedTheme> EncodeAsync(
        string sourcePath,
        string outputDirectory,
        AudioProbeResult source,
        PluginConfiguration configuration,
        ThemeTag tag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(tag);

        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);

        // Measured only when a setting is going to act on the answer: the analysis pass decodes
        // the whole file, and by default nothing about the level is changed.
        LoudnessMeasurement? measurement = null;
        if (configuration.EnableLoudnessNormalization || configuration.RaiseQuietThemes)
        {
            measurement = await MeasureAsync(tools.Ffmpeg, sourcePath, configuration, cancellationToken).ConfigureAwait(false);
            if (measurement is null)
            {
                _logger.LogWarning(
                    "ThemeForge: could not measure the loudness of {Path}; its level is left as it is.",
                    sourcePath);
            }
        }

        var plan = Plan(source, measurement, configuration);
        var target = Path.Combine(outputDirectory, "theme" + plan.Extension);
        var arguments = plan.Copy
            ? CopyArguments(sourcePath, target, plan.Extension, tag)
            : EncodeArguments(sourcePath, target, plan, configuration, tag);

        var result = await _processRunner
            .RunAsync(tools.Ffmpeg, arguments, EncodeTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            throw new InvalidOperationException(
                $"ffmpeg could not {(plan.Copy ? "copy" : "encode")} the theme: {result.ErrorSummary}");
        }

        return new EncodedTheme(target, measurement, plan.Treatment);
    }

    /// <summary>
    /// Decides what happens to a file: nothing but a copy, or a filtered MP3 encode.
    /// </summary>
    /// <param name="source">What ffprobe found in the download.</param>
    /// <param name="measurement">Its loudness, when it was measured.</param>
    /// <param name="configuration">The settings in force.</param>
    /// <returns>The plan.</returns>
    internal static EncodePlan Plan(AudioProbeResult source, LoudnessMeasurement? measurement, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(configuration);

        var filters = new List<string>();
        var notes = new List<string>();

        int? cut = configuration.MaxThemeSeconds > 0 && source.DurationSeconds > configuration.MaxThemeSeconds
            ? configuration.MaxThemeSeconds
            : null;
        var outputDuration = ResolveOutputDuration(source.DurationSeconds, configuration.MaxThemeSeconds);
        var fadeOut = FadeOutLength(configuration.FadeOutSeconds, outputDuration);

        // The end of the file first. Trimming silence changes the length, so the fade-out can no
        // longer be placed at a known time; reversing, fading in and reversing back fades the end
        // without needing to know where it is.
        if (configuration.TrimSilence)
        {
            var silence = string.Create(
                CultureInfo.InvariantCulture,
                $"silenceremove=start_periods=1:start_threshold={configuration.SilenceThresholdDb:0.#}dB");

            filters.Add(silence);
            filters.Add("areverse");
            filters.Add(silence);
            if (fadeOut > 0)
            {
                filters.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={fadeOut:0.###}"));
            }

            filters.Add("areverse");
            notes.Add("silence trimmed");
        }
        else if (fadeOut > 0)
        {
            filters.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"afade=t=out:st={outputDuration - fadeOut:0.###}:d={fadeOut:0.###}"));
        }

        if (configuration.EnableLoudnessNormalization && measurement is not null)
        {
            filters.Add(BuildLoudnormFilter(configuration, measurement));
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"normalised from {measurement.IntegratedLufs:0.#} to {configuration.TargetLoudnessLufs:0.#} LUFS"));
        }
        else if (RaiseGain(measurement, configuration) is { } gain)
        {
            // A plain gain: nothing is compressed or limited, the whole recording simply comes up.
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"volume={gain:0.##}dB"));
            notes.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"raised {gain:0.#} dB from {measurement!.IntegratedLufs:0.#} LUFS"));
        }

        if (configuration.FadeInSeconds > 0)
        {
            filters.Add(string.Create(CultureInfo.InvariantCulture, $"afade=t=in:st=0:d={configuration.FadeInSeconds:0.###}"));
        }

        if (configuration.FadeInSeconds > 0 || fadeOut > 0)
        {
            notes.Add("faded");
        }

        if (cut is { } seconds)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"cut to {seconds}s"));
        }

        var copyExtension = CopyExtensionFor(source.CodecName);
        var mustEncode = filters.Count > 0 || cut is not null || configuration.AlwaysConvertToMp3 || copyExtension is null;

        if (!mustEncode)
        {
            return new EncodePlan(
                Copy: true,
                Extension: copyExtension!,
                Filters: Array.Empty<string>(),
                CutToSeconds: null,
                Treatment: $"copied as delivered ({source.CodecName})");
        }

        var treatment = string.Create(CultureInfo.InvariantCulture, $"encoded to MP3 at {configuration.AudioBitrate} kbit/s");
        if (notes.Count > 0)
        {
            treatment += ": " + string.Join(", ", notes);
        }
        else if (copyExtension is null)
        {
            treatment += $" because a {source.CodecName ?? "unknown"} stream cannot be kept as it is";
        }

        return new EncodePlan(Copy: false, Extension: ".mp3", Filters: filters, CutToSeconds: cut, Treatment: treatment);
    }

    /// <summary>
    /// How far a quiet theme is raised: up to the floor, but never so far that its peaks would
    /// clip, and never at all when it is already loud enough.
    /// </summary>
    /// <param name="measurement">The source's loudness, or null when it could not be measured.</param>
    /// <param name="configuration">The settings in force.</param>
    /// <returns>A positive gain in dB, or null when nothing should change.</returns>
    internal static double? RaiseGain(LoudnessMeasurement? measurement, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.RaiseQuietThemes || measurement is null)
        {
            return null;
        }

        var toFloor = configuration.QuietThemeFloorLufs - measurement.IntegratedLufs;
        var toCeiling = configuration.TargetTruePeakDb - measurement.TruePeakDb;
        var gain = Math.Min(toFloor, toCeiling);

        return gain >= MinimumRaiseDb ? Math.Round(gain, 2) : null;
    }

    /// <summary>
    /// The extension a copied stream is written under, chosen so Jellyfin recognises the file as
    /// theme music and every player knows what is inside. Null when the codec has no such container.
    /// </summary>
    /// <param name="codec">The codec name ffprobe reported.</param>
    /// <returns>An extension with its dot, or null.</returns>
    internal static string? CopyExtensionFor(string? codec) => codec?.ToLowerInvariant() switch
    {
        "opus" => ".opus",
        "vorbis" => ".ogg",
        "aac" or "alac" => ".m4a",
        "mp3" => ".mp3",
        "flac" => ".flac",
        { } pcm when pcm.StartsWith("pcm_", StringComparison.Ordinal) => ".wav",
        _ => null,
    };

    /// <summary>Works out how long the finished file will be, given an optional length cap.</summary>
    /// <param name="sourceDuration">The source length in seconds.</param>
    /// <param name="maxSeconds">The cap, or zero for none.</param>
    /// <returns>The output length in seconds.</returns>
    internal static double ResolveOutputDuration(double sourceDuration, int maxSeconds) =>
        maxSeconds > 0 && sourceDuration > maxSeconds ? maxSeconds : sourceDuration;

    /// <summary>
    /// The fade-out that fits: a fade longer than the track would start before zero and silence
    /// the whole thing, so it is shortened to half the length at most.
    /// </summary>
    internal static double FadeOutLength(double requestedSeconds, double outputDuration)
    {
        if (requestedSeconds <= 0 || outputDuration <= 0)
        {
            return 0;
        }

        var fade = Math.Min(requestedSeconds, outputDuration / 2);
        return outputDuration - fade > 0 ? fade : 0;
    }

    private static List<string> CopyArguments(string sourcePath, string target, string extension, ThemeTag tag)
    {
        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-nostdin", "-loglevel", "error",
            "-i", sourcePath,

            // Exactly one audio stream and nothing else, so embedded cover art cannot end up as a
            // video stream that some clients then refuse to play. The audio itself is untouched.
            "-map", "0:a:0",
            "-vn",
            "-c:a", "copy",
            "-map_metadata", "-1",
        };

        if (extension == ".m4a")
        {
            arguments.AddRange(new[] { "-movflags", "+faststart" });
        }
        else if (extension == ".mp3")
        {
            arguments.AddRange(new[] { "-id3v2_version", "3" });
        }

        arguments.AddRange(new[]
        {
            "-metadata", "title=" + tag.Title,
            "-metadata", "comment=Downloaded by ThemeForge from " + tag.SourceUrl,
            target,
        });

        return arguments;
    }

    private static List<string> EncodeArguments(string sourcePath, string target, EncodePlan plan, PluginConfiguration configuration, ThemeTag tag)
    {
        var arguments = new List<string> { "-y", "-hide_banner", "-nostdin", "-loglevel", "error", "-i", sourcePath };

        if (plan.CutToSeconds is { } seconds)
        {
            arguments.AddRange(new[] { "-t", seconds.ToString(CultureInfo.InvariantCulture) });
        }

        if (plan.Filters.Count > 0)
        {
            arguments.AddRange(new[] { "-af", string.Join(',', plan.Filters) });
        }

        arguments.AddRange(new[]
        {
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
            target,
        });

        return arguments;
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
}
