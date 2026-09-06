using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the check that what was downloaded is actually music.
/// </summary>
/// <remarks>
/// The classification thresholds come from a 66-clip corpus that is not shipped with the plugin,
/// so what is pinned here is everything that can be established from first principles or from
/// audio generated on the spot: the guards, the arithmetic, and the refusal to have an opinion
/// when there is nothing to have one about. The integration tests below run the real ffmpeg
/// filter chain, which is the part most likely to break on a different ffmpeg build.
/// </remarks>
public class AudioVerifierTests
{
    private readonly ITestOutputHelper _output;

    public AudioVerifierTests(ITestOutputHelper output) => _output = output;

    private static AudioVerifier Verifier() =>
        new(new SystemToolProvisioner(), TestEngines.Runner(), NullThemeForgeLogger<AudioVerifier>.Instance);

    [Fact]
    public void SilenceIsRejectedBeforeTheMusicMeasureIsConsulted()
    {
        // Deliberately paired with a measure that says "music": the guard has to win, because a
        // file with nothing in it measures as the most musical thing there is.
        var assessment = AudioVerifier.Classify(0.2, -95, 0.1, TestData.Config());

        Assert.Equal(AudioVerdict.Silent, assessment.Verdict);
        Assert.False(assessment.IsAcceptable);
    }

    [Fact]
    public void UnchangingNoiseIsRejectedBeforeTheMusicMeasureIsConsulted()
    {
        // Room tone measures around 0.72 -- lower, that is more musical, than any real music. It
        // is the one case the measure gets confidently wrong in the direction that matters.
        var assessment = AudioVerifier.Classify(0.72, -20, 0.1, TestData.Config());

        Assert.Equal(AudioVerdict.SteadyNoise, assessment.Verdict);
        Assert.False(assessment.IsAcceptable);
    }

    [Fact]
    public void ACompressedThemeIsNotMistakenForNoise()
    {
        // A measured theme had only 1.8 LU of range, so loudness range on its own is not far off
        // rejecting real music. Both signals have to be degenerate before anything is thrown away.
        var assessment = AudioVerifier.Classify(3.56, -18, 0.6, TestData.Config());

        Assert.Equal(AudioVerdict.Music, assessment.Verdict);
        Assert.True(assessment.IsAcceptable);
    }

    [Theory]
    [InlineData(0.31, AudioVerdict.Music)]
    [InlineData(3.55, AudioVerdict.Music)]
    [InlineData(5.57, AudioVerdict.Music)]
    [InlineData(5.9, AudioVerdict.Undecided)]
    [InlineData(6.20, AudioVerdict.Undecided)]
    [InlineData(6.21, AudioVerdict.Speech)]
    [InlineData(12.32, AudioVerdict.Speech)]
    public void TheMeasureIsClassifiedAgainstTheConfiguredThresholds(double measure, AudioVerdict expected)
    {
        // 6.20 is the lowest speech clip in the corpus and sits exactly on the boundary. It is
        // classified as undecided rather than speech deliberately: on the edge, asking a person
        // costs a review card and rejecting costs a theme that was probably right.
        Assert.Equal(expected, AudioVerifier.Classify(measure, -18, 8, TestData.Config()).Verdict);
    }

    [Fact]
    public void TheUndecidedBandAsksRatherThanDecides()
    {
        var assessment = AudioVerifier.Classify(5.9, -18, 8, TestData.Config());

        Assert.True(assessment.IsAcceptable, "an undecided file is not thrown away");
        Assert.True(assessment.NeedsReview, "but it is not used unattended either");
    }

    [Fact]
    public void AMeasurementThatCouldNotBeTakenNeverRejects()
    {
        // If ffmpeg is missing, broken or built without a filter, the alternative to this is a
        // plugin that silently discards every theme it downloads.
        var assessment = AudioVerifier.Classify(null, null, null, TestData.Config());

        Assert.Equal(AudioVerdict.NotMeasured, assessment.Verdict);
        Assert.True(assessment.IsAcceptable);
        Assert.False(assessment.NeedsReview);
    }

    [Theory]
    [InlineData("-inf", -120)]
    [InlineData("nan", -120)]
    [InlineData("-2000", -120)]
    [InlineData("", -120)]
    [InlineData("-13.5", -13.5)]
    public void ANonNumericLevelBecomesSilenceRatherThanPoisoningTheSeries(string printed, double expected)
    {
        // ffmpeg reports an empty window as -inf or nan. Dropping those windows would be worse
        // than mapping them: the pauses between words are exactly what marks speech out.
        Assert.Equal(expected, AudioVerifier.ParseLevel(printed));
    }

    [Fact]
    public void TheMeasureIsTheSpreadOfTheBalanceBetweenAdjacentBands()
    {
        // Two bands whose difference never changes: no variation, whatever the levels do.
        var steady = Enumerable.Range(0, 300).Select(i => (double)(-20 + (i % 7))).ToList();
        var offset = steady.Select(v => v - 6).ToList();

        Assert.Equal(0, AudioVerifier.BandDiffStd(new[] { steady, offset })!.Value, 6);
    }

    [Fact]
    public void TooFewWindowsProduceNoOpinion()
    {
        var short1 = Enumerable.Repeat(-20.0, 10).ToList();
        var short2 = Enumerable.Repeat(-26.0, 10).ToList();

        Assert.Null(AudioVerifier.BandDiffStd(new[] { short1, short2 }));
        Assert.Null(AudioVerifier.BandDiffStd(Array.Empty<IReadOnlyList<double>>()));
    }

    [Fact]
    public void TheGuardsAreReadOutOfWhatFfmpegActuallyPrints()
    {
        const string stderr = """
            [Parsed_volumedetect_1 @ 0x1] n_samples: 1440000
            [Parsed_volumedetect_1 @ 0x1] mean_volume: -23.7 dB
            [Parsed_volumedetect_1 @ 0x1] max_volume: -0.3 dB
            [Parsed_ebur128_2 @ 0x2] t: 4.9  M: -22.6 S: -22.4 I: -23.1 LUFS  LRA:   0.7 LU
            [Parsed_ebur128_2 @ 0x2] Summary:
              Integrated loudness:
                I:         -23.1 LUFS
              Loudness range:
                LRA:         4.8 LU
            """;

        Assert.Equal(-23.7, AudioVerifier.ParseMeanVolume(stderr));

        // The per-frame lines carry LRA too, so the summary is the last one -- taking the first
        // would read a value measured five seconds in as the value for the whole file.
        Assert.Equal(4.8, AudioVerifier.ParseLoudnessRange(stderr));
    }

    [Fact]
    public void ASilentFileReportsMinusInfinityRatherThanNothing() =>
        Assert.Equal(-120, AudioVerifier.ParseMeanVolume("mean_volume: -inf dB"));

    [Fact]
    public void NothingIsClaimedWhenFfmpegSaidNothing()
    {
        Assert.Null(AudioVerifier.ParseMeanVolume("some unrelated output"));
        Assert.Null(AudioVerifier.ParseLoudnessRange("some unrelated output"));
    }

    // ---- Integration: the real filter chain against real audio ----------------------

    private static string Generate(string name, string source, int seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"themeforge-verify-{name}-{Guid.NewGuid():N}.wav");
        var result = TestEngines.Runner().RunAsync(
            FfmpegAvailability.Path!,
            new[] { "-nostdin", "-hide_banner", "-v", "error", "-y", "-f", "lavfi", "-i", source, "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), path },
            TimeSpan.FromMinutes(2),
            CancellationToken.None).GetAwaiter().GetResult();

        Assert.True(result.Success, result.ErrorSummary);
        return path;
    }

    [FfmpegFact]
    public async Task SilenceIsCaughtByTheRealPipeline()
    {
        var path = Generate("silence", "anullsrc=r=44100:cl=mono", 90);
        try
        {
            var assessment = await Verifier().AssessAsync(path, 90, TestData.Config(), CancellationToken.None);
            _output.WriteLine($"{assessment.Verdict} band={assessment.BandDiffStd:0.00} vol={assessment.MeanVolumeDb:0.0} lra={assessment.LoudnessRange:0.00}");

            Assert.Equal(AudioVerdict.Silent, assessment.Verdict);
            Assert.False(assessment.IsAcceptable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FfmpegFact]
    public async Task SteadyNoiseIsCaughtByTheRealPipeline()
    {
        // The documented trap: this is loud enough to pass the silence guard and measures as more
        // musical than music. Only the loudness-range guard stops it.
        var path = Generate("pink", "anoisesrc=color=pink:a=0.3", 90);
        try
        {
            var assessment = await Verifier().AssessAsync(path, 90, TestData.Config(), CancellationToken.None);
            _output.WriteLine($"{assessment.Verdict} band={assessment.BandDiffStd:0.00} vol={assessment.MeanVolumeDb:0.0} lra={assessment.LoudnessRange:0.00}");

            Assert.True(assessment.MeanVolumeDb > TestData.Config().SilenceThresholdDb, "it is not quiet");
            Assert.True(assessment.BandDiffStd < TestData.Config().MusicThreshold, "and it measures as music");
            Assert.Equal(AudioVerdict.SteadyNoise, assessment.Verdict);
            Assert.False(assessment.IsAcceptable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FfmpegFact]
    public async Task MusicIsAcceptedByTheRealPipeline()
    {
        // A chord with vibrato and tremolo: a dense, sustained, changing spectrum, which is the
        // property the measure is looking for. Real music measured 3.56 against this signal's
        // 2.28, both comfortably inside the accepting band. The point of this test is not the
        // exact number -- it is that the accept path works, so a gate that rejected everything
        // could not pass the suite.
        var path = Generate(
            "music",
            "aevalsrc='(0.35+0.3*sin(2*PI*t/9))*(0.25*sin(2*PI*220*t+3*sin(2*PI*5*t))"
            + "+0.2*sin(2*PI*277*t)+0.18*sin(2*PI*330*t)+0.12*sin(2*PI*440*t)"
            + "+0.06*sin(2*PI*880*t)*(1+sin(2*PI*0.5*t)))':s=44100",
            100);
        try
        {
            var assessment = await Verifier().AssessAsync(path, 100, TestData.Config(), CancellationToken.None);
            _output.WriteLine($"{assessment.Verdict} band={assessment.BandDiffStd:0.00} vol={assessment.MeanVolumeDb:0.0} lra={assessment.LoudnessRange:0.00}");

            Assert.True(assessment.IsAcceptable, assessment.Reason);
            Assert.False(assessment.NeedsReview, assessment.Reason);
            Assert.Equal(AudioVerdict.Music, assessment.Verdict);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FfmpegFact]
    public async Task AShortFileIsPassedWithoutAnOpinion()
    {
        // A catalogue theme is thirty seconds. Judging it on a measure that cannot separate the
        // two populations at that length would reject correct answers for no reason.
        var path = Generate("short", "anoisesrc=color=pink:a=0.3", 30);
        try
        {
            var assessment = await Verifier().AssessAsync(path, 30, TestData.Config(), CancellationToken.None);

            Assert.Equal(AudioVerdict.NotMeasured, assessment.Verdict);
            Assert.True(assessment.IsAcceptable);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [FfmpegFact]
    public async Task NothingIsMeasuredWhenTheCheckIsSwitchedOff()
    {
        var configuration = TestData.Config();
        configuration.RejectNonMusic = false;

        var assessment = await Verifier().AssessAsync("/does/not/exist.mp3", 300, configuration, CancellationToken.None);

        Assert.Equal(AudioVerdict.NotMeasured, assessment.Verdict);
        Assert.True(assessment.IsAcceptable);
    }

    [FfmpegFact]
    public async Task AFileFfmpegCannotReadIsNotRejected()
    {
        var assessment = await Verifier().AssessAsync("/does/not/exist.mp3", 300, TestData.Config(), CancellationToken.None);

        Assert.Equal(AudioVerdict.NotMeasured, assessment.Verdict);
        Assert.True(assessment.IsAcceptable);
    }
}
