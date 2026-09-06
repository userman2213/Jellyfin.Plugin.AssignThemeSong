using System;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class TitleSimilarityRuleTests
{
    private readonly TitleSimilarityRule _rule = new();

    [Fact]
    public void MatchingTitleScoresHighly()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("Firefly - Main Title Theme"),
            TestData.Context(TestData.Series("Firefly")));

        Assert.False(verdict.IsVeto);
        Assert.True(verdict.Raw > 0.9);
    }

    [Fact]
    public void UnrelatedTitleIsVetoed()
    {
        // The most important behaviour in the plugin: a theme from the wrong show must never
        // be assignable, because it lands in the library and nobody notices.
        var verdict = _rule.Evaluate(
            TestData.Candidate("Relaxing Jazz for Studying"),
            TestData.Context(TestData.Series("Firefly")));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void AlternateTitlesAreConsidered()
    {
        var identity = TestData.Series("Ghost in the Shell", alternates: new[] { "koukaku kidoutai" });

        var verdict = _rule.Evaluate(
            TestData.Candidate("Koukaku Kidoutai opening"),
            TestData.Context(identity));

        Assert.False(verdict.IsVeto);
        Assert.True(verdict.Raw > 0.9);
    }
}

public class NegativeKeywordRuleTests
{
    private readonly NegativeKeywordRule _rule = new();

    [Theory]
    [InlineData("Firefly theme REACTION")]
    [InlineData("Firefly theme song 1 hour loop")]
    [InlineData("Firefly theme piano tutorial")]
    [InlineData("Firefly opening cover by me")]
    public void DisqualifyingWordsProduceANegativeScore(string title)
    {
        var verdict = _rule.Evaluate(TestData.Candidate(title), TestData.Context(TestData.Series("Firefly")));
        Assert.True(verdict.Raw < 0, $"expected a penalty for \"{title}\"");
    }

    [Fact]
    public void CleanTitleAbstains()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("Firefly - Main Title"),
            TestData.Context(TestData.Series("Firefly")));

        Assert.Equal(0, verdict.Raw);
    }

    [Fact]
    public void NeverContributesPositively() => Assert.False(_rule.ContributesPositively);

    [Fact]
    public void OnlyTheTitleIsInspected()
    {
        // Descriptions mention "cover" and "reaction" constantly; judging on them would
        // penalise almost every genuine upload.
        var candidate = TestData.Candidate("Firefly - Main Title") with
        {
            Description = "Check out my reaction videos and guitar covers!",
        };

        Assert.Equal(0, _rule.Evaluate(candidate, TestData.Context(TestData.Series("Firefly"))).Raw);
    }
}

public class DurationPlausibilityRuleTests
{
    private readonly DurationPlausibilityRule _rule = new();

    [Fact]
    public void TypicalSeriesThemeLengthScoresBest()
    {
        var verdict = _rule.Evaluate(TestData.Candidate("theme", duration: 60), TestData.Context(TestData.Series("Show")));
        Assert.Equal(1.0, verdict.Raw);
    }

    [Fact]
    public void TenHourLoopIsVetoed()
    {
        var verdict = _rule.Evaluate(TestData.Candidate("theme", duration: 36_000), TestData.Context(TestData.Series("Show")));
        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void ThreeSecondClipIsVetoed()
    {
        var verdict = _rule.Evaluate(TestData.Candidate("theme", duration: 3), TestData.Context(TestData.Series("Show")));
        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void UnknownDurationAbstainsRatherThanGuessing()
    {
        var verdict = _rule.Evaluate(TestData.Candidate("theme", duration: null), TestData.Context(TestData.Series("Show")));
        Assert.False(verdict.IsVeto);
        Assert.Equal(0, verdict.Raw);
    }

    [Fact]
    public void FilmsAllowLongerThemesThanSeries()
    {
        var candidate = TestData.Candidate("theme", duration: 240);
        var asSeries = _rule.Evaluate(candidate, TestData.Context(TestData.Series("Show")));
        var asMovie = _rule.Evaluate(candidate, TestData.Context(TestData.Movie("Film")));

        Assert.Equal(1.0, asMovie.Raw);
        Assert.True(asSeries.Raw < asMovie.Raw);
    }
}

public class ChannelReputationRuleTests
{
    private readonly ChannelReputationRule _rule = new();

    [Fact]
    public void TopicChannelsAreTrusted()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", channel: "Bear McCreary - Topic"),
            TestData.Context(TestData.Series("Show")));

        Assert.True(verdict.Raw >= 0.8);
    }

    [Fact]
    public void BlockedChannelIsVetoed()
    {
        var configuration = TestData.Config();
        configuration.BlockedChannels.Add("SpamChannel");

        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", channel: "SpamChannel Uploads"),
            TestData.Context(TestData.Series("Show"), configuration));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void UnknownChannelAbstains()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", channel: "Some Person"),
            TestData.Context(TestData.Series("Show")));

        Assert.Equal(0, verdict.Raw);
    }
}

public class AvailabilityRuleTests
{
    private readonly AvailabilityRule _rule = new();

    [Fact]
    public void LiveStreamIsVetoed() =>
        Assert.True(_rule.Evaluate(TestData.Candidate("theme", isLive: true), TestData.Context(TestData.Series("S"))).IsVeto);

    [Fact]
    public void PrivateVideoIsVetoed() =>
        Assert.True(_rule.Evaluate(TestData.Candidate("theme", availability: "private"), TestData.Context(TestData.Series("S"))).IsVeto);

    [Fact]
    public void BlockedVideoIdIsVetoed()
    {
        var configuration = TestData.Config();
        configuration.BlockedVideoIds.Add("abc12345678");

        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", id: "abc12345678"),
            TestData.Context(TestData.Series("S"), configuration));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void NeverContributesPositively() => Assert.False(_rule.ContributesPositively);
}

public class DuplicateRuleTests
{
    private readonly DuplicateRule _rule = new();

    [Fact]
    public void VideoUsedElsewhereIsPenalised()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", id: "shared00001"),
            TestData.Context(TestData.Series("S"), duplicates: _ => "Some Other Show"));

        Assert.Equal(-1, verdict.Raw);
        Assert.Contains("Some Other Show", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AbstainsWhenNoIndexIsAvailable()
    {
        // Without a lookup the rule cannot know, and guessing "no clash" would be wrong.
        var verdict = _rule.Evaluate(TestData.Candidate("theme"), TestData.Context(TestData.Series("S")));
        Assert.Equal(0, verdict.Raw);
    }
}

public class RecencyRuleTests
{
    private readonly RecencyRule _rule = new();

    [Fact]
    public void UploadLongBeforeReleaseIsPenalised()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", uploaded: new DateTime(1999, 1, 1)),
            TestData.Context(TestData.Series("Show", year: 2015)));

        Assert.Equal(-1, verdict.Raw);
    }

    [Fact]
    public void UploadAfterReleaseIsFine()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme", uploaded: new DateTime(2016, 1, 1)),
            TestData.Context(TestData.Series("Show", year: 2015)));

        Assert.Equal(1, verdict.Raw);
    }

    [Fact]
    public void AbstainsWithoutAYear()
    {
        var verdict = _rule.Evaluate(
            TestData.Candidate("theme"),
            TestData.Context(TestData.Series("Show", year: null)));

        Assert.Equal(0, verdict.Raw);
    }
}

public class PopularityRuleTests
{
    private readonly PopularityRule _rule = new();

    [Fact]
    public void MoreViewsScoreHigher()
    {
        var few = _rule.Evaluate(TestData.Candidate("t", views: 100), TestData.Context(TestData.Series("S"))).Raw;
        var many = _rule.Evaluate(TestData.Candidate("t", views: 5_000_000), TestData.Context(TestData.Series("S"))).Raw;
        Assert.True(many > few);
    }

    [Fact]
    public void IsCappedAtOne() =>
        Assert.Equal(1, _rule.Evaluate(TestData.Candidate("t", views: 900_000_000), TestData.Context(TestData.Series("S"))).Raw);

    [Fact]
    public void AbstainsWithoutAViewCount() =>
        Assert.Equal(0, _rule.Evaluate(TestData.Candidate("t", views: null), TestData.Context(TestData.Series("S"))).Raw);
}
