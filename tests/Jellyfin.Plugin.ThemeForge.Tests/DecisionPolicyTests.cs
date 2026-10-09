using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class DecisionPolicyTests
{
    private readonly DecisionPolicy _policy = new();

    private static ScoreResult Scored(double total, bool vetoed = false) => new()
    {
        Candidate = TestData.Candidate("candidate"),
        Total = vetoed ? 0 : total,
        Breakdown = vetoed
            ? new List<Signal> { Signal.Veto("Test", "disqualified for testing") }
            : new List<Signal> { new("Test", 1, total, "scored") },
    };

    [Fact]
    public void AboveTheAutoAssignThresholdIsAssigned()
    {
        var decision = _policy.Decide(new[] { Scored(90) }, TestData.Config());
        Assert.Equal(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    [Fact]
    public void BetweenTheThresholdsGoesToReview()
    {
        var decision = _policy.Decide(new[] { Scored(60) }, TestData.Config());
        Assert.Equal(DecisionOutcome.Review, decision.Outcome);
    }

    [Fact]
    public void BelowTheReviewThresholdIsRejected()
    {
        var decision = _policy.Decide(new[] { Scored(10) }, TestData.Config());
        Assert.Equal(DecisionOutcome.NoAcceptableCandidate, decision.Outcome);
    }

    [Fact]
    public void ExactlyOnTheThresholdIsAssigned()
    {
        var configuration = TestData.Config();
        var decision = _policy.Decide(new[] { Scored(configuration.AutoAssignThreshold) }, configuration);
        Assert.Equal(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    [Fact]
    public void AnEmptyCandidateListIsHandled()
    {
        var decision = _policy.Decide(Array.Empty<ScoreResult>(), TestData.Config());
        Assert.Equal(DecisionOutcome.NoAcceptableCandidate, decision.Outcome);
        Assert.Null(decision.Best);
    }

    [Fact]
    public void AVetoedBestCandidateIsNeverAssigned()
    {
        var decision = _policy.Decide(new[] { Scored(0, vetoed: true) }, TestData.Config());
        Assert.Equal(DecisionOutcome.NoAcceptableCandidate, decision.Outcome);
        Assert.Contains("disqualified for testing", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void InvertedThresholdsDoNotStrandConfidentMatches()
    {
        // A user who types the two thresholds the wrong way round should still get sensible
        // behaviour rather than every confident match silently going to review.
        var configuration = TestData.Config();
        configuration.AutoAssignThreshold = 40;
        configuration.ReviewThreshold = 80;

        Assert.Equal(DecisionOutcome.AutoAssign, _policy.Decide(new[] { Scored(90) }, configuration).Outcome);
        Assert.Equal(DecisionOutcome.Review, _policy.Decide(new[] { Scored(60) }, configuration).Outcome);
    }
}
