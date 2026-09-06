using System;
using System.Linq;
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
        var measurement = LoudnessNormalizer.ParseMeasurement(RealFfmpegOutput);

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
        Assert.Null(LoudnessNormalizer.ParseMeasurement(silent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ffmpeg version 6.1.1\nno json at all")]
    [InlineData("{ broken")]
    public void ReturnsNullRatherThanThrowingOnUnusableOutput(string stderr) =>
        Assert.Null(LoudnessNormalizer.ParseMeasurement(stderr));
}

public class FilterChainTests
{
    private static LoudnessMeasurement Measured => new(-14.83, -0.35, 6.6, -25.01, 0.01);

    [Fact]
    public void UsesTheMeasuredValuesSoDynamicsArePreserved()
    {
        var filters = LoudnessNormalizer.BuildFilterChain(Measured, TestData.Config(), outputDuration: 60);
        var loudnorm = filters.First(f => f.StartsWith("loudnorm", StringComparison.Ordinal));

        // Without measured_* values loudnorm acts as a dynamic compressor, which flattens an
        // orchestral main title.
        Assert.Contains("measured_I=-14.83", loudnorm, StringComparison.Ordinal);
        Assert.Contains("linear=true", loudnorm, StringComparison.Ordinal);
        Assert.Contains("I=-23", loudnorm, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsNormalisationWhenItIsTurnedOff()
    {
        var configuration = TestData.Config();
        configuration.EnableLoudnessNormalization = false;

        var filters = LoudnessNormalizer.BuildFilterChain(Measured, configuration, outputDuration: 60);

        Assert.DoesNotContain(filters, f => f.StartsWith("loudnorm", StringComparison.Ordinal));
    }

    [Fact]
    public void OmitsNormalisationWhenMeasurementFailed()
    {
        var filters = LoudnessNormalizer.BuildFilterChain(null, TestData.Config(), outputDuration: 60);
        Assert.DoesNotContain(filters, f => f.StartsWith("loudnorm", StringComparison.Ordinal));
    }

    [Fact]
    public void PlacesTheFadeOutAtTheEndOfTheOutput()
    {
        var configuration = TestData.Config();
        configuration.FadeOutSeconds = 3;

        var filters = LoudnessNormalizer.BuildFilterChain(Measured, configuration, outputDuration: 60);
        var fadeOut = filters.First(f => f.Contains("t=out", StringComparison.Ordinal));

        Assert.Contains("st=57", fadeOut, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortensAFadeThatWouldNotFit()
    {
        // A four-second fade on a two-second clip would start before zero and silence it.
        var configuration = TestData.Config();
        configuration.FadeOutSeconds = 4;

        var filters = LoudnessNormalizer.BuildFilterChain(Measured, configuration, outputDuration: 2);
        var fadeOut = filters.First(f => f.Contains("t=out", StringComparison.Ordinal));

        Assert.Contains("st=1", fadeOut, StringComparison.Ordinal);
        Assert.Contains("d=1", fadeOut, StringComparison.Ordinal);
    }

    [Fact]
    public void OmitsFadesWhenTheyAreDisabled()
    {
        var configuration = TestData.Config();
        configuration.FadeInSeconds = 0;
        configuration.FadeOutSeconds = 0;

        var filters = LoudnessNormalizer.BuildFilterChain(Measured, configuration, outputDuration: 60);

        Assert.DoesNotContain(filters, f => f.StartsWith("afade", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(120, 0, 120)]
    [InlineData(120, 60, 60)]
    [InlineData(30, 60, 30)]
    public void OutputDurationRespectsTheTrimCap(double source, int cap, double expected) =>
        Assert.Equal(expected, LoudnessNormalizer.ResolveOutputDuration(source, cap));
}
