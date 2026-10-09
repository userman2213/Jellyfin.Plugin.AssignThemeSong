using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>What listening to the downloaded file concluded.</summary>
public enum AudioVerdict
{
    /// <summary>Nothing was measured, so nothing is claimed. Never a reason to reject.</summary>
    NotMeasured = 0,

    /// <summary>It behaves like music.</summary>
    Music = 1,

    /// <summary>Between the two, which is a reason to ask rather than to decide.</summary>
    Undecided = 2,

    /// <summary>It behaves like speech: a clip, a discussion, a reaction.</summary>
    Speech = 3,

    /// <summary>There is essentially nothing there.</summary>
    Silent = 4,

    /// <summary>Steady, unchanging sound: room tone, hiss, a hum.</summary>
    SteadyNoise = 5,
}

/// <summary>The measurements, and what they add up to.</summary>
/// <param name="Verdict">The conclusion.</param>
/// <param name="BandDiffStd">The music/speech measure, or null when it was not computed.</param>
/// <param name="MeanVolumeDb">Mean volume, used to catch a file with nothing in it.</param>
/// <param name="LoudnessRange">EBU R128 loudness range, used to catch unchanging sound.</param>
/// <param name="Reason">A sentence for the log and the review queue.</param>
public sealed record AudioAssessment(
    AudioVerdict Verdict,
    double? BandDiffStd,
    double? MeanVolumeDb,
    double? LoudnessRange,
    string Reason)
{
    /// <summary>Gets a value indicating whether this file may be written to the library at all.</summary>
    public bool IsAcceptable => Verdict is AudioVerdict.NotMeasured or AudioVerdict.Music or AudioVerdict.Undecided;

    /// <summary>Gets a value indicating whether a person should look at it before it is used.</summary>
    public bool NeedsReview => Verdict == AudioVerdict.Undecided;
}

/// <summary>Listens to a downloaded file and says whether it is music.</summary>
public interface IAudioVerifier
{
    /// <summary>
    /// Measures the file and classifies it.
    /// </summary>
    /// <param name="path">The downloaded file, before normalisation.</param>
    /// <param name="durationSeconds">Its duration, as ffprobe reported it.</param>
    /// <param name="configuration">Settings supplying the thresholds.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The assessment. Never throws: a failed measurement returns <see cref="AudioVerdict.NotMeasured"/>.</returns>
    Task<AudioAssessment> AssessAsync(
        string path,
        double durationSeconds,
        PluginConfiguration configuration,
        CancellationToken cancellationToken);
}

/// <summary>
/// Decides whether a downloaded file is music, by measuring it rather than by reading its title.
/// </summary>
/// <remarks>
/// <para>
/// This is not a scoring rule and does not belong with them. Every rule in the scoring engine
/// judges a listing — a title, a channel, a duration — and a listing can be wrong about what the
/// file contains. This judges the file. It runs after the download and before the encode, so a
/// recap, a reaction video or a stretch of dialogue is thrown away before anything reaches the
/// library and before the time is spent normalising it.
/// </para>
/// <para>
/// The measure is <c>band_diff_std</c>: the audio is split into four frequency bands, each band's
/// level is taken every tenth of a second, and the measure is how much the balance between
/// adjacent bands varies over time. Speech moves its formants constantly and pauses between
/// words, so the balance swings; music holds a fuller, steadier spectrum. Measured on a 66-clip
/// corpus, music fell between 0.31 and 5.57 and speech between 6.20 and 12.32.
/// </para>
/// <para>
/// Three things it gets wrong if they are not handled first, all verified rather than assumed.
/// <b>Steady noise scores as the most musical thing there is</b> — room tone measures around 0.74,
/// lower than any real music — so a file with no dynamic range at all is rejected before the
/// measure is consulted. <b>Silence</b> likewise. And <b>the measure needs about a minute of
/// audio</b>: below that the two populations overlap and it says nothing useful, so a short file
/// is passed without an opinion rather than judged on a bad one. That last point is also why a
/// theme taken from a catalogue is never judged here: those clips are thirty seconds long, and
/// their provenance is a better answer than any measurement anyway.
/// </para>
/// <para>
/// The thresholds are settings, and the measured value is recorded on every item whatever the
/// verdict. A gap of 0.6 between the populations will be crossed eventually on a library of a
/// few hundred titles, and having the number next to each assignment is what makes it possible
/// to tell where.
/// </para>
/// </remarks>
public sealed class AudioVerifier : IAudioVerifier
{
    /// <summary>Below this, the two populations overlap and the measure means nothing.</summary>
    public const double MinimumSecondsToJudge = 60;

    /// <summary>How much audio is measured. Enough to be representative, short enough to be quick.</summary>
    private const int SecondsToMeasure = 180;

    /// <summary>Levels are sampled this often.</summary>
    private const double WindowSeconds = 0.1;

    /// <summary>Too few windows to compute a meaningful spread.</summary>
    private const int MinimumWindows = 200;

    /// <summary>What ffmpeg reports for a window with nothing in it, mapped to a real number.</summary>
    private const double SilentLevelDb = -120;

    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// The bands. Four is enough to see a formant move and few enough to stay one cheap pass.
    /// </summary>
    private static readonly string[] BandFilters =
    {
        "lowpass=f=500",
        "bandpass=f=1000:width_type=h:w=500",
        "bandpass=f=2500:width_type=h:w=1000",
        "highpass=f=3500",
    };

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly IThemeForgeLogger<AudioVerifier> _logger;

    /// <summary>Initializes a new instance of the <see cref="AudioVerifier"/> class.</summary>
    /// <param name="toolProvisioner">Supplies ffmpeg.</param>
    /// <param name="processRunner">Runs ffmpeg.</param>
    /// <param name="logger">Logger.</param>
    public AudioVerifier(
        IToolProvisioner toolProvisioner,
        IProcessRunner processRunner,
        IThemeForgeLogger<AudioVerifier> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AudioAssessment> AssessAsync(
        string path,
        double durationSeconds,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!configuration.RejectNonMusic)
        {
            return NotMeasured("checking is switched off");
        }

        if (durationSeconds < MinimumSecondsToJudge)
        {
            return NotMeasured(string.Format(
                CultureInfo.InvariantCulture,
                "it is only {0:0}s long, and the measure needs at least {1:0}s to mean anything",
                durationSeconds,
                MinimumSecondsToJudge));
        }

        var workingDirectory = Path.Combine(
            Path.GetDirectoryName(path) ?? Path.GetTempPath(),
            "verify");

        try
        {
            Directory.CreateDirectory(workingDirectory);

            var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);
            var result = await _processRunner
                .RunAsync(tools.Ffmpeg, BuildArguments(path, workingDirectory), Timeout, cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                // A measurement that could not be taken is not evidence of anything, so it never
                // rejects: the alternative is a broken ffmpeg silently throwing away every theme.
                _logger.LogWarning("ThemeForge: could not measure the downloaded audio: {Error}", result.ErrorSummary);
                return NotMeasured("the audio could not be measured");
            }

            var meanVolume = ParseMeanVolume(result.StandardError);
            var loudnessRange = ParseLoudnessRange(result.StandardError);
            var bands = ReadBandLevels(workingDirectory);
            var measure = BandDiffStd(bands);

            return Classify(measure, meanVolume, loudnessRange, configuration);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not measure the downloaded audio.");
            return NotMeasured("the audio could not be measured");
        }
        finally
        {
            TryDelete(workingDirectory);
        }
    }

    /// <summary>
    /// Turns the measurements into a verdict.
    /// </summary>
    /// <remarks>
    /// Order matters. The two guards run first because an unchanging file is the one case where
    /// the music measure is confidently wrong in the dangerous direction: room tone scores lower
    /// — that is, more musical — than any real music does.
    /// </remarks>
    /// <param name="measure">The band measure, or null if it could not be computed.</param>
    /// <param name="meanVolumeDb">Mean volume in dB, or null.</param>
    /// <param name="loudnessRange">EBU R128 loudness range, or null.</param>
    /// <param name="configuration">The thresholds.</param>
    /// <returns>The assessment.</returns>
    public static AudioAssessment Classify(
        double? measure,
        double? meanVolumeDb,
        double? loudnessRange,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (meanVolumeDb is { } volume && volume < configuration.SilenceThresholdDb)
        {
            return new AudioAssessment(
                AudioVerdict.Silent,
                measure,
                meanVolumeDb,
                loudnessRange,
                string.Format(CultureInfo.InvariantCulture, "there is nothing audible in it ({0:0.#} dB)", volume));
        }

        // Both have to agree. Loudness range alone would reject a heavily compressed theme -- a
        // real one measured 1.8 LU, barely above this threshold -- and the band measure alone
        // cannot tell noise from music, because noise scores inside the music range. Together
        // they describe a file with neither dynamics nor spectral movement, which is not a theme.
        if (loudnessRange is { } range
            && range < configuration.MinimumLoudnessRange
            && measure is { } flat
            && flat < configuration.SteadyNoiseCeiling)
        {
            return new AudioAssessment(
                AudioVerdict.SteadyNoise,
                measure,
                meanVolumeDb,
                loudnessRange,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "it never changes ({0:0.##} LU of range, measured {1:0.00}), so it is hiss or room tone rather than music",
                    range,
                    flat));
        }

        if (measure is not { } value)
        {
            return new AudioAssessment(AudioVerdict.NotMeasured, null, meanVolumeDb, loudnessRange, "the audio could not be measured");
        }

        if (value > configuration.SpeechThreshold)
        {
            return new AudioAssessment(
                AudioVerdict.Speech,
                value,
                meanVolumeDb,
                loudnessRange,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "it sounds like talking rather than music (measured {0:0.00}, above {1:0.00})",
                    value,
                    configuration.SpeechThreshold));
        }

        if (value > configuration.MusicThreshold)
        {
            return new AudioAssessment(
                AudioVerdict.Undecided,
                value,
                meanVolumeDb,
                loudnessRange,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "it is between music and speech (measured {0:0.00}), so it is worth a listen before it is used",
                    value));
        }

        return new AudioAssessment(
            AudioVerdict.Music,
            value,
            meanVolumeDb,
            loudnessRange,
            string.Format(CultureInfo.InvariantCulture, "it sounds like music (measured {0:0.00})", value));
    }

    /// <summary>
    /// How much the balance between adjacent bands varies over time.
    /// </summary>
    /// <param name="bands">One level series per band, in band order.</param>
    /// <returns>The measure, or null when there is not enough to measure.</returns>
    public static double? BandDiffStd(IReadOnlyList<IReadOnlyList<double>> bands)
    {
        if (bands is null || bands.Count < 2)
        {
            return null;
        }

        var windows = bands.Min(band => band.Count);
        if (windows < MinimumWindows)
        {
            return null;
        }

        var spreads = new List<double>(bands.Count - 1);

        for (var band = 0; band < bands.Count - 1; band++)
        {
            var differences = new double[windows];
            for (var i = 0; i < windows; i++)
            {
                differences[i] = bands[band][i] - bands[band + 1][i];
            }

            spreads.Add(StandardDeviation(differences));
        }

        return spreads.Average();
    }

    /// <summary>Reads a level ffmpeg printed, mapping "no signal" to a real number.</summary>
    /// <remarks>
    /// ffmpeg reports an empty window as <c>-inf</c> or <c>nan</c>. Both have to become a number
    /// or the whole series is poisoned; -120 dB is far below anything audible and keeps a silent
    /// passage looking like the silence it is. Dropping such windows instead would be worse: the
    /// pauses between words are exactly what distinguishes speech.
    /// </remarks>
    /// <param name="text">The value as printed.</param>
    /// <returns>The level in dB.</returns>
    public static double ParseLevel(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            return SilentLevelDb;
        }

        return Math.Max(value, SilentLevelDb);
    }

    /// <summary>Reads the mean volume out of ffmpeg's volumedetect output.</summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>The mean volume in dB, or null when it was not reported.</returns>
    public static double? ParseMeanVolume(string stderr)
    {
        var match = Regex.Match(stderr ?? string.Empty, @"mean_volume:\s*(-?[\d.]+|-inf) dB");
        if (!match.Success)
        {
            return null;
        }

        return match.Groups[1].Value == "-inf" ? SilentLevelDb : ParseLevel(match.Groups[1].Value);
    }

    /// <summary>Reads the loudness range out of ffmpeg's ebur128 summary.</summary>
    /// <param name="stderr">ffmpeg's stderr.</param>
    /// <returns>The range in LU, or null when it was not reported.</returns>
    public static double? ParseLoudnessRange(string stderr)
    {
        // The summary block prints "LRA:" on its own line under "Loudness range:"; the per-frame
        // lines carry "LRA:" too, so the last one is the summary.
        var matches = Regex.Matches(stderr ?? string.Empty, @"LRA:\s*(-?[\d.]+) LU");
        if (matches.Count == 0)
        {
            return null;
        }

        return double.TryParse(
            matches[^1].Groups[1].Value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;
    }

    private static AudioAssessment NotMeasured(string reason) =>
        new(AudioVerdict.NotMeasured, null, null, null, reason);

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        var sum = values.Sum(value => (value - mean) * (value - mean));
        return Math.Sqrt(sum / values.Count);
    }

    /// <summary>
    /// One ffmpeg invocation that measures everything: four band level series written to files,
    /// and the two guards reported on stderr.
    /// </summary>
    private static IReadOnlyList<string> BuildArguments(string path, string workingDirectory)
    {
        var samplesPerWindow = (int)(16000 * WindowSeconds);
        var chain = new List<string>
        {
            "[0:a]aformat=channel_layouts=mono:sample_fmts=fltp,aresample=16000,"
            + $"asetnsamples=n={samplesPerWindow.ToString(CultureInfo.InvariantCulture)}:p=0,"
            + "asplit=5[guard][a0][a1][a2][a3]",
            "[guard]volumedetect,ebur128=peak=none[og]",
        };

        for (var i = 0; i < BandFilters.Length; i++)
        {
            var file = Path.Combine(workingDirectory, $"band{i.ToString(CultureInfo.InvariantCulture)}.txt")
                .Replace("\\", "/", StringComparison.Ordinal)
                .Replace(":", "\\:", StringComparison.Ordinal);

            chain.Add(
                $"[a{i.ToString(CultureInfo.InvariantCulture)}]{BandFilters[i]},astats=metadata=1:reset=1,"
                + $"ametadata=print:key=lavfi.astats.Overall.RMS_level:file={file}"
                + $"[o{i.ToString(CultureInfo.InvariantCulture)}]");
        }

        var arguments = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-t", SecondsToMeasure.ToString(CultureInfo.InvariantCulture),
            "-i", path,
            "-filter_complex", string.Join(";", chain),
            "-map", "[og]", "-f", "null", "-",
        };

        for (var i = 0; i < BandFilters.Length; i++)
        {
            arguments.Add("-map");
            arguments.Add($"[o{i.ToString(CultureInfo.InvariantCulture)}]");
            arguments.Add("-f");
            arguments.Add("null");
            arguments.Add("-");
        }

        return arguments;
    }

    private static IReadOnlyList<IReadOnlyList<double>> ReadBandLevels(string workingDirectory)
    {
        var bands = new List<IReadOnlyList<double>>(BandFilters.Length);

        for (var i = 0; i < BandFilters.Length; i++)
        {
            var file = Path.Combine(workingDirectory, $"band{i.ToString(CultureInfo.InvariantCulture)}.txt");
            if (!File.Exists(file))
            {
                return Array.Empty<IReadOnlyList<double>>();
            }

            var levels = new List<double>();
            foreach (var line in File.ReadLines(file))
            {
                var separator = line.IndexOf('=', StringComparison.Ordinal);
                if (separator > 0 && line.StartsWith("lavfi.astats", StringComparison.Ordinal))
                {
                    levels.Add(ParseLevel(line[(separator + 1)..].Trim()));
                }
            }

            bands.Add(levels);
        }

        return bands;
    }

    private void TryDelete(string directory)
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
            _logger.LogDebug(ex, "ThemeForge: could not remove {Directory}.", directory);
        }
    }
}
