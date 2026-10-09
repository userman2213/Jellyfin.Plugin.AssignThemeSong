using System;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class LoudnessMeasurementParsingTests
{
    private const string RealFfmpegOutput = @"
[Parsed_loudnorm_0 @ 0x55d1] 
{
	""input_i"" : ""-14.83"",
	""input_tp"" : ""-0.35"",
	""input_lra"" : ""6.60"",
	""input_thresh"" : ""-25.01"",
	""output_i"" : ""-23.01"",
	""output_tp"" : ""-8.53"",
	""output_lra"" : ""6.50"",
	""output_thresh"" : ""-33.18"",
	""normalization_type"" : ""dynamic"",
	""target_offset"" : ""0.01""
}
";

    [Fact]
    public void ReadsTheReportFromFfmpegStderr()
    {
        var measurement = ThemeEncoder.ParseMeasurement(RealFfmpegOutput);

        Assert.NotNull(measurement);
        Assert.Equal(-14.83, measurement!.IntegratedLufs, 2);
        Assert.Equal(-0.35, measurement.TruePeakDb, 2);
        Assert.Equal(6.60, measurement.LoudnessRange, 2);
        Assert.Equal(-25.01, measurement.ThresholdLufs, 2);
        Assert.Equal(0.01, measurement.TargetOffset, 2);
    }

    [Fact]
    public void ReturnsNullForSilentInput()
    {
        // loudnorm reports "-inf" for silence, which is not a usable measurement.
        var silent = @"{ ""input_i"" : ""-inf"", ""input_tp"" : ""-inf"", ""input_lra"" : ""0.00"", ""input_thresh"" : ""-inf"" }";
        Assert.Null(ThemeEncoder.ParseMeasurement(silent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ffmpeg version 6.1.1\nno json at all")]
    [InlineData("{ broken")]
    public void ReturnsNullRatherThanThrowingOnUnusableOutput(string stderr) =>
        Assert.Null(ThemeEncoder.ParseMeasurement(stderr));
}

/// <summary>
/// The encoder's decision: copy the download untouched, or decode, filter and re-encode it.
/// By default it must be the former — that is the whole point of the 2.3 change.
/// </summary>
public class EncodePlanTests
{
    private static AudioProbeResult Source(string codec = "opus", double seconds = 90) =>
        new(true, seconds, codec, false, null);

    private static LoudnessMeasurement Measured(double integrated, double truePeak) =>
        new(integrated, truePeak, 6.6, integrated - 10, 0.0);

    [Fact]
    public void ByDefaultTheStreamIsCopiedExactlyAsDelivered()
    {
        var plan = ThemeEncoder.Plan(Source("opus"), null, TestData.Config());

        Assert.True(plan.Copy);
        Assert.Equal(".opus", plan.Extension);
        Assert.Empty(plan.Filters);
        Assert.Null(plan.CutToSeconds);
        Assert.Contains("copied as delivered", plan.Treatment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("opus", ".opus")]
    [InlineData("OPUS", ".opus")]
    [InlineData("vorbis", ".ogg")]
    [InlineData("aac", ".m4a")]
    [InlineData("alac", ".m4a")]
    [InlineData("mp3", ".mp3")]
    [InlineData("flac", ".flac")]
    [InlineData("pcm_s16le", ".wav")]
    public void TheExtensionSaysWhatIsInsideTheFile(string codec, string expected) =>
        Assert.Equal(expected, ThemeEncoder.CopyExtensionFor(codec));

    [Theory]
    [InlineData("wmav2")]
    [InlineData(null)]
    public void ACodecWithNoContainerOfItsOwnIsEncodedToMp3EvenWithNothingElseToDo(string? codec)
    {
        var plan = ThemeEncoder.Plan(Source(codec!), null, TestData.Config());

        Assert.False(plan.Copy);
        Assert.Equal(".mp3", plan.Extension);
        Assert.Empty(plan.Filters);
    }

    [Fact]
    public void ForcingMp3EncodesWithoutTouchingTheAudio()
    {
        var configuration = TestData.Config();
        configuration.AlwaysConvertToMp3 = true;

        var plan = ThemeEncoder.Plan(Source("opus"), null, configuration);

        Assert.False(plan.Copy);
        Assert.Equal(".mp3", plan.Extension);
        Assert.Empty(plan.Filters);
        Assert.StartsWith("encoded to MP3 at 192 kbit/s", plan.Treatment, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisingAQuietThemeIsAPlainGainUpToTheFloor()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        // -24 LUFS against a -16 floor wants +8 dB; the peaks at -12 dBTP leave room for it.
        var plan = ThemeEncoder.Plan(Source(), Measured(-24, truePeak: -12), configuration);

        Assert.False(plan.Copy);
        Assert.Equal("volume=8dB", Assert.Single(plan.Filters));
        Assert.Contains("raised 8 dB from -24 LUFS", plan.Treatment, StringComparison.Ordinal);
    }

    [Fact]
    public void RaisingStopsShortOfClipping()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        // The same -24 LUFS, but peaks already at -8 dBTP: only 6.5 dB fit under the -1.5 ceiling.
        var plan = ThemeEncoder.Plan(Source(), Measured(-24, truePeak: -8), configuration);

        Assert.Equal("volume=6.5dB", Assert.Single(plan.Filters));
    }

    [Fact]
    public void RaisingNeverLowersAThemeThatIsAlreadyLoudEnough()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        Assert.Null(ThemeEncoder.RaiseGain(Measured(-10, truePeak: -0.5), configuration));

        // Nothing else asked for, so the loud theme is simply copied.
        Assert.True(ThemeEncoder.Plan(Source(), Measured(-10, truePeak: -0.5), configuration).Copy);
    }

    [Fact]
    public void RaisingByLessThanHalfADecibelIsNotWorthAReencode()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        Assert.Null(ThemeEncoder.RaiseGain(Measured(-16.3, truePeak: -5), configuration));
        Assert.True(ThemeEncoder.Plan(Source(), Measured(-16.3, truePeak: -5), configuration).Copy);
    }

    [Fact]
    public void RaisingWithoutAMeasurementDoesNothing()
    {
        var configuration = TestData.Config();
        configuration.RaiseQuietThemes = true;

        Assert.True(ThemeEncoder.Plan(Source(), null, configuration).Copy);
    }

    [Fact]
    public void NormalisationUsesTheMeasuredValuesSoDynamicsArePreserved()
    {
        var configuration = TestData.Config();
        configuration.EnableLoudnessNormalization = true;

        var plan = ThemeEncoder.Plan(Source(), Measured(-14.83, truePeak: -0.35), configuration);
        var loudnorm = Assert.Single(plan.Filters);

        // Without measured_* values loudnorm acts as a dynamic compressor, which flattens an
        // orchestral main title.
        Assert.StartsWith("loudnorm", loudnorm, StringComparison.Ordinal);
        Assert.Contains("measured_I=-14.83", loudnorm, StringComparison.Ordinal);
        Assert.Contains("linear=true", loudnorm, StringComparison.Ordinal);
        Assert.Contains("I=-23", loudnorm, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisationTakesPrecedenceOverRaising()
    {
        var configuration = TestData.Config();
        configuration.EnableLoudnessNormalization = true;
        configuration.RaiseQuietThemes = true;

        var plan = ThemeEncoder.Plan(Source(), Measured(-24, truePeak: -12), configuration);

        Assert.Single(plan.Filters);
        Assert.StartsWith("loudnorm", plan.Filters[0], StringComparison.Ordinal);
    }

    [Fact]
    public void NormalisationIsSkippedWhenTheMeasurementFailed()
    {
        var configuration = TestData.Config();
        configuration.EnableLoudnessNormalization = true;

        var plan = ThemeEncoder.Plan(Source(), null, configuration);

        Assert.True(plan.Copy);
        Assert.DoesNotContain(plan.Filters, f => f.StartsWith("loudnorm", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFadeOutIsPlacedAtTheEndOfTheOutput()
    {
        var configuration = TestData.Config();
        configuration.FadeOutSeconds = 3;

        var plan = ThemeEncoder.Plan(Source(seconds: 60), null, configuration);

        Assert.Equal("afade=t=out:st=57:d=3", Assert.Single(plan.Filters));
        Assert.False(plan.Copy);
    }

    [Fact]
    public void AFadeThatWouldNotFitIsShortened()
    {
        // A four-second fade on a two-second clip would start before zero and silence it.
        var configuration = TestData.Config();
        configuration.FadeOutSeconds = 4;

        var plan = ThemeEncoder.Plan(Source(seconds: 2), null, configuration);

        Assert.Equal("afade=t=out:st=1:d=1", Assert.Single(plan.Filters));
    }

    [Fact]
    public void TrimmingSilenceFadesTheEndWithoutKnowingWhereItIs()
    {
        var configuration = TestData.Config();
        configuration.TrimSilence = true;
        configuration.FadeOutSeconds = 3;

        var plan = ThemeEncoder.Plan(Source(seconds: 60), null, configuration);

        Assert.Equal(2, plan.Filters.Count(f => f.StartsWith("silenceremove=start_periods=1", StringComparison.Ordinal)));
        Assert.Equal(2, plan.Filters.Count(f => f == "areverse"));
        Assert.Contains("afade=t=in:st=0:d=3", plan.Filters);
        Assert.DoesNotContain(plan.Filters, f => f.Contains("t=out", StringComparison.Ordinal));
        Assert.Contains("silence trimmed", plan.Treatment, StringComparison.Ordinal);
        Assert.Contains("faded", plan.Treatment, StringComparison.Ordinal);
    }

    [Fact]
    public void ACutAppliesOnlyToSourcesLongerThanTheCap()
    {
        var configuration = TestData.Config();
        configuration.MaxThemeSeconds = 60;

        Assert.True(ThemeEncoder.Plan(Source(seconds: 30), null, configuration).Copy);

        var cut = ThemeEncoder.Plan(Source(seconds: 120), null, configuration);
        Assert.False(cut.Copy);
        Assert.Equal(60, cut.CutToSeconds);
        Assert.Contains("cut to 60s", cut.Treatment, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(120, 0, 120)]
    [InlineData(120, 60, 60)]
    [InlineData(30, 60, 30)]
    public void OutputDurationRespectsTheTrimCap(double source, int cap, double expected) =>
        Assert.Equal(expected, ThemeEncoder.ResolveOutputDuration(source, cap));
}

public class AudioDefaultsTests
{
    /// <summary>What every installation before 2.3 has in its saved settings, untouched.</summary>
    private static PluginConfiguration SavedBefore23() => new()
    {
        EnableLoudnessNormalization = true,
        TargetLoudnessLufs = -23,
        TargetTruePeakDb = -1.5,
        TargetLoudnessRange = 11,
        FadeInSeconds = 0.5,
        FadeOutSeconds = 3,
    };

    [Fact]
    public void SettingsStillAtThePreviousDefaultsAreMovedToTheCurrentOnes()
    {
        var configuration = SavedBefore23();

        Assert.True(AudioDefaults.Upgrade(configuration));

        Assert.False(configuration.EnableLoudnessNormalization);
        Assert.Equal(0, configuration.FadeInSeconds);
        Assert.Equal(0, configuration.FadeOutSeconds);
    }

    [Fact]
    public void AnEditedValueMarksTheSettingsAsTheUsersAndTheyAreLeftAlone()
    {
        var configuration = SavedBefore23();
        configuration.TargetLoudnessLufs = -18;

        Assert.False(AudioDefaults.Upgrade(configuration));

        Assert.True(configuration.EnableLoudnessNormalization);
        Assert.Equal(3, configuration.FadeOutSeconds);
    }

    [Fact]
    public void FreshSettingsHaveNothingToUpgrade() =>
        Assert.False(AudioDefaults.Upgrade(new PluginConfiguration()));

    [Fact]
    public void UpgradingTwiceChangesNothingTheSecondTime()
    {
        var configuration = SavedBefore23();
        AudioDefaults.Upgrade(configuration);

        Assert.False(AudioDefaults.Upgrade(configuration));
    }
}
