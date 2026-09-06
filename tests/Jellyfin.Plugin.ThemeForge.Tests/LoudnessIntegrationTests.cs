using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Exercises the real encoder, because the volume behaviour this plugin exists to fix cannot be
/// demonstrated by inspecting an argument list.
/// </summary>
/// <remarks>
/// Jellyfin plays theme songs through the same player as everything else, sets no volume of its
/// own, and reloads its saved global volume whenever a media element is created — so a plugin
/// cannot reliably impose a volume at playback time, and anything it writes leaks into the
/// user's normal playback volume. ThemeForge's answer is to make every theme file the same
/// loudness before it is ever written. These tests prove that actually happens.
/// </remarks>
public class LoudnessIntegrationTests : IDisposable
{
    private readonly string _workspace =
        Path.Combine(Path.GetTempPath(), "themeforge-tests-" + Guid.NewGuid().ToString("N"));

    public LoudnessIntegrationTests() => Directory.CreateDirectory(_workspace);

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
    private async Task<string> MakeToneAsync(string name, double amplitude, double seconds)
    {
        var path = Path.Combine(_workspace, name);
        var result = await TestEngines.Runner().RunAsync(
            FfmpegAvailability.Path!,
            new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-f", "lavfi",
                "-i", string.Create(CultureInfo.InvariantCulture, $"sine=frequency=440:duration={seconds}:sample_rate=44100"),
                "-af", string.Create(CultureInfo.InvariantCulture, $"volume={amplitude}"),
                "-c:a", "pcm_s16le",
                path,
            },
            TimeSpan.FromMinutes(2),
            CancellationToken.None).ConfigureAwait(false);

        Assert.True(result.Success, "could not render the test tone: " + result.ErrorSummary);
        return path;
    }

    /// <summary>Measures a finished file's integrated loudness with a fresh loudnorm analysis pass.</summary>
    private async Task<double> MeasureLoudnessAsync(string path)
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

        var measurement = LoudnessNormalizer.ParseMeasurement(result.StandardError);
        Assert.NotNull(measurement);
        return measurement!.IntegratedLufs;
    }

    [FfmpegFact]
    public async Task LoudAndQuietSourcesEndUpAtTheSameLoudness()
    {
        var configuration = TestData.Config();
        configuration.FadeInSeconds = 0;
        configuration.FadeOutSeconds = 0;

        // Two sources 20 dB apart — the everyday case of one uploader mastering hot and
        // another leaving plenty of headroom.
        var loud = await MakeToneAsync("loud.wav", amplitude: 0.9, seconds: 12).ConfigureAwait(false);
        var quiet = await MakeToneAsync("quiet.wav", amplitude: 0.09, seconds: 12).ConfigureAwait(false);

        var normalizer = TestEngines.Normalizer();
        var results = new List<double>();

        foreach (var (source, name) in new[] { (loud, "loud.mp3"), (quiet, "quiet.mp3") })
        {
            var target = Path.Combine(_workspace, name);
            await normalizer.EncodeAsync(
                source,
                target,
                sourceDuration: 12,
                configuration,
                new ThemeTag("Test Theme", "https://example.invalid/test"),
                CancellationToken.None).ConfigureAwait(false);

            results.Add(await MeasureLoudnessAsync(target).ConfigureAwait(false));
        }

        // Each lands on the configured target...
        Assert.All(results, loudness =>
            Assert.True(
                Math.Abs(loudness - configuration.TargetLoudnessLufs) <= 1.5,
                $"expected about {configuration.TargetLoudnessLufs} LUFS, measured {loudness:0.00}"));

        // ...and, the point of the exercise, they now match each other.
        Assert.True(
            Math.Abs(results[0] - results[1]) <= 1.0,
            $"sources 20 dB apart should end up within 1 LU of each other, got {results[0]:0.00} and {results[1]:0.00}");
    }

    [FfmpegFact]
    public async Task TheEncodedFileIsAValidStereoMp3WithNoVideoStream()
    {
        var source = await MakeToneAsync("source.wav", amplitude: 0.5, seconds: 10).ConfigureAwait(false);
        var target = Path.Combine(_workspace, "theme.mp3");

        await TestEngines.Normalizer().EncodeAsync(
            source,
            target,
            sourceDuration: 10,
            TestData.Config(),
            new ThemeTag("Test Theme", "https://example.invalid/test"),
            CancellationToken.None).ConfigureAwait(false);

        // This is the verification gate that keeps unplayable files out of the library.
        var probe = await TestEngines.Probe().ProbeAsync(target, CancellationToken.None).ConfigureAwait(false);

        Assert.True(probe.IsValid, probe.Problem);
        Assert.Equal("mp3", probe.CodecName);
        Assert.False(probe.HasVideoStream);
        Assert.InRange(probe.DurationSeconds, 9.5, 10.5);
    }

    [FfmpegFact]
    public async Task TrimmingProducesAShorterThemeWithAFadeThatFits()
    {
        var configuration = TestData.Config();
        configuration.MaxThemeSeconds = 5;
        configuration.FadeOutSeconds = 2;

        var source = await MakeToneAsync("long.wav", amplitude: 0.5, seconds: 20).ConfigureAwait(false);
        var target = Path.Combine(_workspace, "trimmed.mp3");

        await TestEngines.Normalizer().EncodeAsync(
            source,
            target,
            sourceDuration: 20,
            configuration,
            new ThemeTag("Test Theme", "https://example.invalid/test"),
            CancellationToken.None).ConfigureAwait(false);

        var probe = await TestEngines.Probe().ProbeAsync(target, CancellationToken.None).ConfigureAwait(false);

        Assert.True(probe.IsValid, probe.Problem);
        Assert.InRange(probe.DurationSeconds, 4.5, 5.6);
    }

    [FfmpegFact]
    public async Task ProbeRejectsAFileThatIsNotAudio()
    {
        var garbage = Path.Combine(_workspace, "garbage.mp3");
        await File.WriteAllTextAsync(garbage, "this is not an mp3").ConfigureAwait(false);

        var probe = await TestEngines.Probe().ProbeAsync(garbage, CancellationToken.None).ConfigureAwait(false);

        Assert.False(probe.IsValid);
        Assert.NotNull(probe.Problem);
    }

    [FfmpegFact]
    public async Task ProbeRejectsAnEmptyFile()
    {
        var empty = Path.Combine(_workspace, "empty.mp3");
        await File.WriteAllBytesAsync(empty, Array.Empty<byte>()).ConfigureAwait(false);

        var probe = await TestEngines.Probe().ProbeAsync(empty, CancellationToken.None).ConfigureAwait(false);

        Assert.False(probe.IsValid);
    }
}
