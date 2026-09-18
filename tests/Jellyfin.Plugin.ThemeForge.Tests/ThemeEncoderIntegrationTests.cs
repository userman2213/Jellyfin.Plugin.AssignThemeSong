using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Exercises the real encoder, because what happens to the audio cannot be demonstrated by
/// inspecting an argument list.
/// </summary>
/// <remarks>
/// The default has to be proved as carefully as the options: a theme written with nothing turned
/// on must be the download, bit for bit, at the volume it came in. The options are then proved to
/// do exactly what they say -- raising never lowers, normalising levels, trimming shortens.
/// </remarks>
public class ThemeEncoderIntegrationTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "themeforge-tests-" + Guid.NewGuid().ToString("N"));

    public ThemeEncoderIntegrationTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_workspace, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a test over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Renders a test tone at a chosen amplitude, standing in for a downloaded theme.</summary>
    private async Task<string> MakeToneAsync(string name, double amplitude, double seconds, string? extraFilter = null, string[]? codec = null)
    {
        var path = Path.Combine(_workspace, name);
        var filter = string.Create(CultureInfo.InvariantCulture, $"volume={amplitude}");
        if (extraFilter is not null)
        {
            filter += "," + extraFilter;
        }

        var arguments = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency=440:duration={seconds}:sample_rate=44100"),
            "-af", filter,
        };
        arguments.AddRange(codec ?? new[] { "-c:a", "pcm_s16le" });
        arguments.Add(path);

        var result = await TestEngines.Runner()
            .RunAsync(FfmpegAvailability.Path!, arguments, TimeSpan.FromMinutes(2), CancellationToken.None)
            .ConfigureAwait(false);

        Assert.True(result.Success, "could not render the test tone: " + result.ErrorSummary);
        return path;
    }

    /// <summary>Measures a file's integrated loudness with a fresh loudnorm analysis pass.</summary>
    private static async Task<double> MeasureLoudnessAsync(string path)
    {
        var result = await TestEngines.Runner().RunAsync(
            FfmpegAvailability.Path!,
            new[]
            {
                "-hide_banner", "-nostdin", "-loglevel", "info",
                "-i", path,
                "-af", "loudnorm=print_format=json",
                "-f", "null", "-",
            },
            TimeSpan.FromMinutes(2),
            CancellationToken.None).ConfigureAwait(false);

        var measurement = ThemeEncoder.ParseMeasurement(result.StandardError);
        Assert.NotNull(measurement);
        return measurement!.IntegratedLufs;
    }

    private static Task<AudioProbeResult> ProbeAsync(string path) =>
        TestEngines.Probe().ProbeAsync(path, CancellationToken.None);

    private async Task<EncodedTheme> EncodeAsync(string source, PluginConfiguration configuration, string name)
    {
        var directory = Path.Combine(_workspace, name);
        Directory.CreateDirectory(directory);

        var probe = await ProbeAsync(source).ConfigureAwait(false);
        Assert.True(probe.IsValid, probe.Problem);

        return await TestEngines.Encoder().EncodeAsync(
            source,
            directory,
            probe,
            configuration,
            new ThemeTag("Test Theme", "https://example.invalid/test"),
            CancellationToken.None).ConfigureAwait(false);
    }

    [FfmpegFact]
    public async Task ByDefaultTheAudioIsCopiedExactlyAsDelivered()
    {
        var source = await MakeToneAsync("source.wav", amplitude: 0.5, seconds: 10);
        var before = await MeasureLoudnessAsync(source);

        var encoded = await EncodeAsync(source, TestData.Config(), "copy");

        Assert.Equal("theme.wav", Path.GetFileName(encoded.Path));
        Assert.Contains("copied as delivered", encoded.Treatment, StringComparison.Ordinal);
        Assert.Null(encoded.Measurement);

        var probe = await ProbeAsync(encoded.Path);
        Assert.True(probe.IsValid, probe.Problem);
        Assert.Equal("pcm_s16le", probe.CodecName);
        Assert.False(probe.HasVideoStream);
        Assert.InRange(probe.DurationSeconds, 9.5, 10.5);

        var after = await MeasureLoudnessAsync(encoded.Path);
        Assert.True(Math.Abs(after - before) <= 0.5, $"a copy must not change the level: {before:0.00} became {after:0.00} LUFS");
    }

    [FfmpegFact]
    public async Task AnOpusDownloadStaysOpus()
    {
        // What yt-dlp delivers from YouTube most of the time.
        var source = await MakeToneAsync("source.webm", amplitude: 0.5, seconds: 10, codec: new[] { "-c:a", "libopus", "-b:a", "96k" });

        var encoded = await EncodeAsync(source, TestData.Config(), "opus");

        Assert.Equal("theme.opus", Path.GetFileName(encoded.Path));
        var probe = await ProbeAsync(encoded.Path);
        Assert.True(probe.IsValid, probe.Problem);
        Assert.Equal("opus", probe.CodecName);
        Assert.InRange(probe.DurationSeconds, 9.5, 10.5);
    }

    [FfmpegFact]
    public async Task RaisingBringsAQuietThemeUpAndLeavesALoudOneAlone()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        // ffmpeg's sine source sits near -22 LUFS at unity, so these land at about -42 and about
        // -12 LUFS: one well under the -16 floor, one well over it.
        var quiet = await MakeToneAsync("quiet.wav", amplitude: 0.1, seconds: 12);
        var loud = await MakeToneAsync("loud.wav", amplitude: 3.0, seconds: 12);
        var loudBefore = await MeasureLoudnessAsync(loud);

        var quietOut = await EncodeAsync(quiet, configuration, "quiet");
        var loudOut = await EncodeAsync(loud, configuration, "loud");

        Assert.Equal(".mp3", Path.GetExtension(quietOut.Path));
        var raised = await MeasureLoudnessAsync(quietOut.Path);
        Assert.True(
            Math.Abs(raised - configuration.QuietThemeFloorLufs) <= 1.5,
            $"expected the quiet theme at about {configuration.QuietThemeFloorLufs} LUFS, measured {raised:0.00}");

        // The loud one was never re-encoded, let alone turned down.
        Assert.Equal(".wav", Path.GetExtension(loudOut.Path));
        var loudAfter = await MeasureLoudnessAsync(loudOut.Path);
        Assert.True(Math.Abs(loudAfter - loudBefore) <= 0.5, $"the loud theme changed from {loudBefore:0.00} to {loudAfter:0.00} LUFS");
    }

    [FfmpegFact]
    public async Task NormalisationLevelsLoudAndQuietSourcesWhenAskedFor()
    {
        var configuration = TestData.Config();
        configuration.EnableLoudnessNormalization = true;

        // Two sources 20 dB apart — the everyday case of one uploader mastering hot and
        // another leaving plenty of headroom.
        var loud = await MakeToneAsync("loud.wav", amplitude: 0.9, seconds: 12);
        var quiet = await MakeToneAsync("quiet.wav", amplitude: 0.09, seconds: 12);

        var results = new List<double>();
        foreach (var (source, name) in new[] { (loud, "normalised-loud"), (quiet, "normalised-quiet") })
        {
            var encoded = await EncodeAsync(source, configuration, name);
            Assert.Equal(".mp3", Path.GetExtension(encoded.Path));
            results.Add(await MeasureLoudnessAsync(encoded.Path));
        }

        Assert.All(results, loudness =>
            Assert.True(
                Math.Abs(loudness - configuration.TargetLoudnessLufs) <= 1.5,
                $"expected about {configuration.TargetLoudnessLufs} LUFS, measured {loudness:0.00}"));

        Assert.True(
            Math.Abs(results[0] - results[1]) <= 1.0,
            $"sources 20 dB apart should end up within 1 LU of each other, got {results[0]:0.00} and {results[1]:0.00}");
    }

    [FfmpegFact]
    public async Task AForcedMp3IsAValidStereoMp3WithNoVideoStream()
    {
        var configuration = TestData.Config();
        configuration.AlwaysConvertToMp3 = true;

        var source = await MakeToneAsync("source.wav", amplitude: 0.5, seconds: 10);
        var encoded = await EncodeAsync(source, configuration, "mp3");

        Assert.Equal("theme.mp3", Path.GetFileName(encoded.Path));
        Assert.StartsWith("encoded to MP3", encoded.Treatment, StringComparison.Ordinal);

        // This is the verification gate that keeps unplayable files out of the library.
        var probe = await ProbeAsync(encoded.Path);
        Assert.True(probe.IsValid, probe.Problem);
        Assert.Equal("mp3", probe.CodecName);
        Assert.False(probe.HasVideoStream);
        Assert.InRange(probe.DurationSeconds, 9.5, 10.5);
    }

    [FfmpegFact]
    public async Task CuttingProducesAShorterThemeWithAFadeThatFits()
    {
        var configuration = TestData.Config();
        configuration.MaxThemeSeconds = 5;
        configuration.FadeOutSeconds = 2;

        var source = await MakeToneAsync("long.wav", amplitude: 0.5, seconds: 20);
        var encoded = await EncodeAsync(source, configuration, "cut");

        var probe = await ProbeAsync(encoded.Path);
        Assert.True(probe.IsValid, probe.Problem);
        Assert.Equal("mp3", probe.CodecName);
        Assert.InRange(probe.DurationSeconds, 4.5, 5.6);
    }

    [FfmpegFact]
    public async Task TrimmingSilenceRemovesTheDeadAirAtBothEnds()
    {
        var configuration = TestData.Config();
        configuration.TrimSilence = true;

        // Six seconds of tone with two seconds of nothing before and after it.
        var source = await MakeToneAsync("padded.wav", amplitude: 0.5, seconds: 6, extraFilter: "adelay=2000,apad=pad_dur=2");
        var sourceProbe = await ProbeAsync(source);
        Assert.InRange(sourceProbe.DurationSeconds, 9.5, 10.5);

        var encoded = await EncodeAsync(source, configuration, "trimmed");

        var probe = await ProbeAsync(encoded.Path);
        Assert.True(probe.IsValid, probe.Problem);
        Assert.InRange(probe.DurationSeconds, 5.5, 6.6);
    }

    [FfmpegFact]
    public async Task ProbeRejectsAFileThatIsNotAudio()
    {
        var garbage = Path.Combine(_workspace, "garbage.mp3");
        await File.WriteAllTextAsync(garbage, "this is not an mp3");

        var probe = await ProbeAsync(garbage);

        Assert.False(probe.IsValid);
        Assert.NotNull(probe.Problem);
    }

    [FfmpegFact]
    public async Task ProbeRejectsAnEmptyFile()
    {
        var empty = Path.Combine(_workspace, "empty.mp3");
        await File.WriteAllBytesAsync(empty, Array.Empty<byte>());

        var probe = await ProbeAsync(empty);

        Assert.False(probe.IsValid);
    }
}
