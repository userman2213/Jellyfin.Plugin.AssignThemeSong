using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// The failures actually reported from a real library, scored end to end.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests prove each mechanism. These prove the arithmetic that matters: what the whole
/// weighted sum does with the candidates a live search really returned, and whether the decision
/// policy would have written them into the library.
/// </para>
/// <para>
/// The worked example from the report: searching for the HBO series <c>Girls</c> returned the
/// themes for <i>The Golden Girls</i>, <i>Gilmore Girls</i>, <i>2 Broke Girls</i> and
/// <i>The Powerpuff Girls</i> — the correct answer was not among them at all — and the first of
/// them scored 79.6, comfortably past the auto-assign threshold. Every one of those is a genuine
/// theme song of a real show, of exactly the right length, so no amount of re-weighting separates
/// them. Only refusing to accept the identity does.
/// </para>
/// </remarks>
public class ReportedMisassignmentTests
{
    private readonly ITestOutputHelper _output;

    public ReportedMisassignmentTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void TheSearchThatReturnedNothingCorrectNowAssignsNothing()
    {
        var identity = TestData.Series("Girls", 2012);
        var context = TestData.Context(identity);

        var candidates = new[]
        {
            TestData.Candidate("The Golden Girls Theme Song", duration: 40),
            TestData.Candidate("Gilmore Girls Theme Song - Where You Lead", duration: 55),
            TestData.Candidate("2 Broke Girls Theme Song", duration: 35),
            TestData.Candidate("The Powerpuff Girls Theme Song", duration: 45),
            TestData.Candidate("Equestria Girls Theme", duration: 60),
        };

        var ranked = TestData.Engine().Rank(candidates, context);

        foreach (var result in ranked)
        {
            _output.WriteLine($"{result.Total,6:0.0}  {result.Candidate.Title}");
        }

        Assert.All(ranked, result => Assert.True(
            result.IsVetoed,
            $"\"{result.Candidate.Title}\" scored {result.Total:0.0} for the series Girls"));

        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());
        Assert.Equal(DecisionOutcome.NoAcceptableCandidate, decision.Outcome);
    }

    [Fact]
    public void TheRealThemeForTheSameShowIsStillAssigned()
    {
        // The point of the reject set is not to reject everything. When the correct answer does
        // turn up, it still has to be taken.
        var context = TestData.Context(TestData.Series("Girls", 2012));
        var ranked = TestData.Engine().Rank(
            new[] { TestData.Candidate("Girls (HBO) - Main Title Theme", duration: 45) },
            context);

        _output.WriteLine($"{ranked[0].Total:0.0}  {ranked[0].Candidate.Title}");

        Assert.False(ranked[0].IsVetoed);
        Assert.Equal(
            DecisionOutcome.AutoAssign,
            new DecisionPolicy().Decide(ranked, TestData.Config()).Outcome);
    }

    [Theory]
    [InlineData("Lost", "Getting lost in the woods ASMR")]
    [InlineData("House", "Building a house timelapse")]
    [InlineData("The Office", "office chair review")]
    [InlineData("Bones", "Bones - Imagine Dragons")]
    [InlineData("Lost", "Lost in Space Theme")]
    [InlineData("Star Trek", "Star Trek: Deep Space Nine - Theme")]
    public void NoneOfTheReportedMisassignmentsWouldBeWrittenNow(string show, string candidateTitle)
    {
        var ranked = TestData.Engine().Rank(
            new[] { TestData.Candidate(candidateTitle, duration: 50) },
            TestData.Context(TestData.Series(show)));

        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        _output.WriteLine($"{show,-12} <- {candidateTitle,-40} {ranked[0].Total,6:0.0}  {decision.Outcome}");
        Assert.NotEqual(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    [Theory]
    [InlineData("Firefly", "Sonny Rhodes - The Ballad of Serenity (Firefly Opening Theme Song)")]
    [InlineData("Battlestar Galactica", "Battlestar Galactica Main Title Theme")]
    [InlineData("Stranger Things", "Stranger Things Theme - Kyle Dixon & Michael Stein")]
    [InlineData("Cowboy Bebop", "Cowboy Bebop - Tank! (Opening)")]
    [InlineData("The Office", "The Office Theme Song")]
    [InlineData("Peaky Blinders", "Peaky Blinders Theme - Red Right Hand")]
    public void ACorrectThemeIsStillAssignedWithoutAsking(string show, string candidateTitle)
    {
        var ranked = TestData.Engine().Rank(
            new[] { TestData.Candidate(candidateTitle, duration: 60) },
            TestData.Context(TestData.Series(show)));

        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        _output.WriteLine($"{show,-22} <- {candidateTitle,-64} {ranked[0].Total,6:0.0}  {decision.Outcome}");
        Assert.Equal(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    [Fact]
    public void AnOrdinaryTitleWithNothingToBackItUpGoesToReviewRatherThanBeingWritten()
    {
        // "Marshmello - FRIENDS" is a song. It names the show perfectly and leaves nothing over,
        // so no identity rule can reject it -- only the absence of any claim to be a theme
        // separates it, and that is a reason to ask rather than to refuse.
        var ranked = TestData.Engine().Rank(
            new[] { TestData.Candidate("Marshmello - FRIENDS", duration: 62) },
            TestData.Context(TestData.Series("Friends", 1994)));

        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        _output.WriteLine($"{ranked[0].Total:0.0}  {decision.Outcome}: {decision.Reason}");
        Assert.NotEqual(DecisionOutcome.AutoAssign, decision.Outcome);
    }
}
