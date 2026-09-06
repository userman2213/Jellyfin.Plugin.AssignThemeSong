using System;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class ScoringEngineTests
{
    [Fact]
    public void AVetoForcesTheScoreToZero()
    {
        var engine = TestData.Engine();

        // Everything else about this candidate is excellent; the ten-hour length must still
        // sink it. A veto that could be outvoted would not be a veto.
        var result = engine.Score(
            TestData.Candidate("Firefly - Main Title Theme", duration: 36_000, views: 9_000_000, channel: "Firefly - Topic"),
            TestData.Context(TestData.Series("Firefly")));

        Assert.True(result.IsVetoed);
        Assert.Equal(0, result.Total);
    }

    [Fact]
    public void AnIdealCandidateScoresNearTheTop()
    {
        var result = TestData.Engine().Score(
            TestData.Candidate(
                "Firefly - Main Title Theme",
                duration: 60,
                views: 2_000_000,
                channel: "Sonny Rhodes - Topic",
                uploaded: new DateTime(2010, 1, 1)),
            TestData.Context(TestData.Series("Firefly", year: 2002)));

        Assert.False(result.IsVetoed);
        Assert.True(result.Total > 85, $"expected a high score, got {result.Total:0.0}");
    }

    [Fact]
    public void ScoresAreBoundedToTheZeroToHundredRange()
    {
        var engine = TestData.Engine();
        var context = TestData.Context(TestData.Series("Firefly"));

        foreach (var candidate in new[]
                 {
                     TestData.Candidate("Firefly opening theme main title intro soundtrack"),
                     TestData.Candidate("Firefly theme reaction cover remix tutorial 1 hour loop"),
                 })
        {
            var total = engine.Score(candidate, context).Total;
            Assert.InRange(total, 0, 100);
        }
    }

    [Fact]
    public void EveryRuleContributesANamedSignal()
    {
        var result = TestData.Engine().Score(
            TestData.Candidate("Firefly - Main Title"),
            TestData.Context(TestData.Series("Firefly")));

        // The review queue is only useful if every point is traceable to a named rule.
        Assert.Equal(10, result.Breakdown.Count);
        Assert.All(result.Breakdown, signal =>
        {
            Assert.False(string.IsNullOrWhiteSpace(signal.Rule));
            Assert.False(string.IsNullOrWhiteSpace(signal.Reason));
        });
    }

    [Fact]
    public void RankPutsTheBestCandidateFirst()
    {
        var ranked = TestData.Engine().Rank(
            new[]
            {
                TestData.Candidate("Firefly theme REACTION!!", duration: 400),
                TestData.Candidate("Firefly - Main Title Theme", duration: 60, channel: "Greg Edmonson - Topic"),
                TestData.Candidate("Firefly theme piano cover", duration: 90),
            },
            TestData.Context(TestData.Series("Firefly")));

        Assert.Equal("Firefly - Main Title Theme", ranked[0].Candidate.Title);
    }

    [Fact]
    public void DescribeProducesAReadableBreakdown()
    {
        var result = TestData.Engine().Score(
            TestData.Candidate("Firefly - Main Title"),
            TestData.Context(TestData.Series("Firefly")));

        var lines = ScoringEngine.Describe(result);
        Assert.StartsWith("Total:", lines[0], StringComparison.Ordinal);
        Assert.Equal(11, lines.Count);
    }

    [Fact]
    public void AThrowingRuleDoesNotSinkTheCandidate()
    {
        // One broken rule must not cost a candidate its chance or abort an unattended run.
        var engine = new ScoringEngine(new IScoringRule[] { new ThrowingRule(), new Engines.Scoring.Rules.QuerySpecificityRule() });
        var result = engine.Score(TestData.Candidate("anything"), TestData.Context(TestData.Series("S")));

        Assert.False(result.IsVetoed);
        Assert.Contains(result.Breakdown, signal => signal.Reason.Contains("rule failed", StringComparison.Ordinal));
    }

    private sealed class ThrowingRule : IScoringRule
    {
        public string Name => "Throwing";

        public double WeightFrom(Jellyfin.Plugin.ThemeForge.Configuration.ScoringWeights weights) => 10;

        public RuleVerdict Evaluate(Engines.Discovery.Candidate candidate, ScoringContext context) =>
            throw new InvalidOperationException("boom");
    }
}

/// <summary>
/// The behavioural contract of the whole ranking system, expressed as realistic searches.
/// </summary>
/// <remarks>
/// These are the cases that matter in practice: a YouTube search for a theme returns the real
/// thing buried among reactions, covers, hour-long loops and unrelated uploads. Unit tests on
/// individual rules cannot show that the weights combine to pick the right one, so this pins
/// down the outcome rather than the mechanism.
/// </remarks>
public class GoldenSetTests
{
    [Theory]
    [MemberData(nameof(Cases))]
    public void TheCorrectCandidateWins(string show, int year, bool isSeries, string expected, string[] candidates)
    {
        var identity = isSeries ? TestData.Series(show, year) : TestData.Movie(show, year);
        var scored = TestData.Engine().Rank(
            candidates.Select((title, index) => TestData.Candidate(
                title,
                duration: DurationFor(title),
                views: 500_000,
                channel: "Uploads",
                id: "vid" + index.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture))),
            TestData.Context(identity));

        Assert.Equal(expected, scored[0].Candidate.Title);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void NoKnownBadCandidateEverWins(string show, int year, bool isSeries, string expected, string[] candidates)
    {
        var identity = isSeries ? TestData.Series(show, year) : TestData.Movie(show, year);
        var scored = TestData.Engine().Rank(
            candidates.Select((title, index) => TestData.Candidate(
                title,
                duration: DurationFor(title),
                id: "vid" + index.ToString("00000000", System.Globalization.CultureInfo.InvariantCulture))),
            TestData.Context(identity));

        var winner = scored[0].Candidate.Title;
        Assert.Equal(expected, winner);
        Assert.DoesNotContain("REACTION", winner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cover", winner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hour", winner, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Gives the ten-hour loops and short clips realistic lengths.</summary>
    private static double DurationFor(string title)
    {
        if (title.Contains("10 hours", StringComparison.OrdinalIgnoreCase) || title.Contains("1 hour", StringComparison.OrdinalIgnoreCase))
        {
            return 36_000;
        }

        return title.Contains("REACTION", StringComparison.OrdinalIgnoreCase) ? 900 : 85;
    }

    public static TheoryData<string, int, bool, string, string[]> Cases() => new()
    {
        {
            "Firefly", 2002, true, "Firefly - Main Title Theme",
            new[]
            {
                "Firefly theme song REACTION - first time hearing!",
                "Firefly - Main Title Theme",
                "Firefly theme song 10 hours loop",
                "Ballad of Serenity acoustic cover",
                "Top 10 Sci-Fi Shows You Missed",
            }
        },
        {
            "Battlestar Galactica", 2004, true, "Battlestar Galactica opening theme",
            new[]
            {
                "Battlestar Galactica opening theme",
                "Battlestar Galactica theme piano tutorial",
                "Battlestar Galactica full episode 1",
                "Battlestar Galactica theme 1 hour",
            }
        },
        {
            "The Expanse", 2015, true, "The Expanse - Main Title",
            new[]
            {
                "The Expanse - Main Title",
                "The Expanse opening REACTION",
                "The Expanse season 6 explained",
                "Expanse theme guitar cover",
            }
        },
        {
            "Blade Runner", 1982, false, "Blade Runner Main Titles - Vangelis",
            new[]
            {
                "Blade Runner Main Titles - Vangelis",
                "Blade Runner 2049 trailer",
                "Blade Runner theme 10 hours",
                "Blade Runner ending explained",
            }
        },
        {
            "Interstellar", 2014, false, "Interstellar Main Theme - Hans Zimmer",
            new[]
            {
                "Interstellar Main Theme - Hans Zimmer",
                "Interstellar docking scene REACTION",
                "Interstellar soundtrack 1 hour extended",
                "Interstellar organ cover",
            }
        },
        {
            "Doctor Who", 2005, true, "Doctor Who Theme - Opening Titles",
            new[]
            {
                "Doctor Who Theme - Opening Titles",
                "Doctor Who theme remix nightcore",
                "Every Doctor Who regeneration",
                "Doctor Who theme song REACTION",
            }
        },
    };
}
