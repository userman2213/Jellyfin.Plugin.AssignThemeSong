using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Runs the ranking over search results actually captured from YouTube.
/// </summary>
/// <remarks>
/// Hand-written cases only test what their author already thought of. These fixtures are real
/// yt-dlp output, and they immediately exposed two defects that the synthetic set missed: the
/// genuine Firefly theme was being disqualified because bracket-stripping removed the half of
/// its title naming the show, and a clip with no theme-related words in it could out-rank real
/// themes purely on the show name appearing.
/// </remarks>
public class LiveSearchTests
{
    private static IReadOnlyList<ScoreResult> Rank(string fixture, string show, int year)
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        var candidates = YtDlpJson.ParseLines(text, TestData.DefaultQuery, hydrated: true);

        Assert.NotEmpty(candidates);
        return TestData.Engine().Rank(candidates, TestData.Context(TestData.Series(show, year)));
    }

    [Fact]
    public void FireflyPicksTheRealThemeEvenWhenTheShowNameIsOnlyInBrackets()
    {
        var ranked = Rank("live-firefly.jsonl", "Firefly", 2002);
        var winner = ranked[0];

        Assert.Contains("Firefly Opening Theme Song", winner.Candidate.Title, StringComparison.OrdinalIgnoreCase);
        Assert.False(winner.IsVetoed);

        // Confident enough to assign without asking.
        Assert.Equal(
            DecisionOutcome.AutoAssign,
            new DecisionPolicy().Decide(ranked, TestData.Config()).Outcome);
    }

    [Fact]
    public void FireflyRejectsTheHourLongVersionAndDemotesTheClip()
    {
        var ranked = Rank("live-firefly.jsonl", "Firefly", 2002);

        var hourLong = ranked.Single(r => r.Candidate.Title.Contains("1 hour", StringComparison.OrdinalIgnoreCase));
        Assert.True(hourLong.IsVetoed);

        // A clip that merely mentions the show must not out-rank an actual theme.
        var joke = ranked.Single(r => r.Candidate.Title.Contains("Joke", StringComparison.OrdinalIgnoreCase));
        Assert.True(joke.Total < ranked[0].Total);
        Assert.True(joke.Total < TestData.Config().AutoAssignThreshold, "a clip must never be assigned unattended");
    }

    [Fact]
    public void BattlestarPicksAnOpeningTitleSequence()
    {
        var ranked = Rank("live-battlestar.jsonl", "Battlestar Galactica", 2004);
        var winner = ranked[0].Candidate.Title;

        Assert.Contains("Battlestar Galactica", winner, StringComparison.OrdinalIgnoreCase);
        Assert.False(ranked[0].IsVetoed);
    }

    [Fact]
    public void AnAbbreviatedTitleThatNamesNoShowIsRejected()
    {
        var ranked = Rank("live-battlestar.jsonl", "Battlestar Galactica", 2004);

        // "BSG full opening music" is probably right, but nothing in the title says so, and
        // guessing is how the wrong theme ends up in somebody's library.
        var abbreviated = ranked.Single(r => r.Candidate.Title.StartsWith("BSG", StringComparison.Ordinal));
        Assert.True(abbreviated.IsVetoed);
    }

    [Fact]
    public void ExpansePrefersTheOpeningOverACoverAndRejectsALoop()
    {
        var ranked = Rank("live-expanse.jsonl", "The Expanse", 2015);

        Assert.Contains("Expanse", ranked[0].Candidate.Title, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cover", ranked[0].Candidate.Title, StringComparison.OrdinalIgnoreCase);

        var loop = ranked.Single(r => r.Candidate.Title.Contains("looped", StringComparison.OrdinalIgnoreCase));
        Assert.True(loop.IsVetoed);

        var cover = ranked.Single(r => r.Candidate.Title.Contains("Cover", StringComparison.OrdinalIgnoreCase));
        Assert.True(cover.Total < ranked[0].Total);
    }

    [Theory]
    [InlineData("live-firefly.jsonl", "Firefly", 2002)]
    [InlineData("live-battlestar.jsonl", "Battlestar Galactica", 2004)]
    [InlineData("live-expanse.jsonl", "The Expanse", 2015)]
    public void EveryWinnerIsAtLeastGoodEnoughToOffer(string fixture, string show, int year)
    {
        var ranked = Rank(fixture, show, year);
        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        Assert.NotEqual(DecisionOutcome.NoAcceptableCandidate, decision.Outcome);
    }
}
