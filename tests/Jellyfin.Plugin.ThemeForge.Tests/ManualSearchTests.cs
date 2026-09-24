using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Searching by hand is the way out of a theme that is simply wrong, so the two things it must
/// get right are showing what the rules rejected -- that is usually the video the person is
/// after -- and preferring what the fuller metadata says over the bare listing.
/// </summary>
public class ManualSearchTests
{
    private static Candidate Video(string id, string title, bool hydrated) => new()
    {
        Id = id,
        Url = "https://www.youtube.com/watch?v=" + id,
        Title = title,
        FoundBy = new SearchQuery("whatever", 0, "manual search"),
        IsHydrated = hydrated,
    };

    private static ScoreResult Scored(Candidate candidate, double total, string reason) => new()
    {
        Candidate = candidate,
        Total = total,
        Breakdown = new[] { new Signal("TitleSimilarity", 1, total, reason) },
    };

    private static ScoreResult Rejected(Candidate candidate, string reason) => new()
    {
        Candidate = candidate,
        Total = 0,
        Breakdown = new[] { Signal.Veto("TitleSimilarity", reason) },
    };

    [Fact]
    public void WhatTheFullMetadataSaysReplacesWhatTheListingSaid()
    {
        var listed = Video("aaaaaaaaaaa", "Main Title", hydrated: false);
        var inspected = Video("aaaaaaaaaaa", "Main Title", hydrated: true);

        var merged = ThemeOrchestrator.Merge(
            new[] { Scored(listed, 35, "held until its album is known") },
            new[] { Scored(inspected, 88, "its album names the show") });

        var only = Assert.Single(merged);
        Assert.Equal(88, only.Total);
        Assert.True(only.Candidate.IsHydrated);
    }

    [Fact]
    public void ACandidateRejectedOnlyOnceInspectedShowsTheLaterVerdict()
    {
        // The everyday case: a listing with no duration cannot be judged on length, and the fetch
        // reveals an hour-long upload.
        var listed = Video("bbbbbbbbbbb", "The Show Theme", hydrated: false);
        var inspected = Video("bbbbbbbbbbb", "The Show Theme", hydrated: true);

        var merged = ThemeOrchestrator.Merge(
            new[] { Scored(listed, 70, "names the show") },
            new[] { Rejected(inspected, "at 62 minutes it is far too long to be a theme") });

        var only = Assert.Single(merged);
        Assert.True(only.IsVetoed);
        Assert.Equal("at 62 minutes it is far too long to be a theme", only.VetoReason);
    }

    [Fact]
    public void RejectedCandidatesAreKeptAndSortLast()
    {
        // The whole point of searching by hand: the rules' verdict is advice, not a filter.
        var good = Scored(Video("ccccccccccc", "Best Theme", hydrated: true), 90, "names the show");
        var bad = Rejected(Video("ddddddddddd", "Something Else", hydrated: true), "it names something else");
        var middling = Scored(Video("eeeeeeeeeee", "Maybe Theme", hydrated: true), 40, "ordinary title");

        var merged = ThemeOrchestrator.Merge(new[] { bad, middling, good }, Array.Empty<ScoreResult>());

        Assert.Equal(3, merged.Count);
        Assert.Equal(new[] { "ccccccccccc", "eeeeeeeeeee", "ddddddddddd" }, merged.Select(r => r.Candidate.Id));
    }

    [Fact]
    public void ResultsNobodyInspectedAreStillReturned()
    {
        var inspected = Scored(Video("fffffffffff", "Inspected", hydrated: true), 80, "names the show");
        var listedOnly = Scored(Video("ggggggggggg", "Only Listed", hydrated: false), 60, "names the show");

        var merged = ThemeOrchestrator.Merge(new[] { inspected, listedOnly }, new[] { inspected });

        Assert.Equal(2, merged.Count);
        Assert.False(merged.Single(r => r.Candidate.Id == "ggggggggggg").Candidate.IsHydrated);
    }

    [Fact]
    public void AnEmptyQueryFallsBackToWhatTheItemWouldBeSearchedForAnyway()
    {
        var identity = TestData.Series("Battlestar Galactica", year: 2004);
        var configuration = TestData.Config();
        var firstRung = new QueryPlanner().Plan(identity, configuration)[0].Text;

        foreach (var nothing in new[] { null, string.Empty, "   " })
        {
            Assert.Equal(firstRung, ThemeOrchestrator.ResolveQuery(nothing, () => firstRung));
        }

        Assert.Contains("Battlestar Galactica", firstRung, StringComparison.Ordinal);
    }

    [Fact]
    public void WhatWasTypedWinsOverTheFallback() =>
        Assert.Equal(
            "bear mccreary prelude to war",
            ThemeOrchestrator.ResolveQuery("  bear mccreary prelude to war  ", () => "something else"));

    [Theory]
    [InlineData("Bear McCreary - \"Main Title\"")]
    [InlineData("theme; rm -rf /")]
    [InlineData("進撃の巨人 opening")]
    [InlineData("$(whoami) & `id`")]
    public void AwkwardCharactersReachTheSearchUnchanged(string typed)
    {
        // Arguments reach yt-dlp as an array and never through a shell, so none of this is
        // dangerous -- but it must also not be mangled on the way.
        var resolved = ThemeOrchestrator.ResolveQuery(typed, () => "unused");

        Assert.Equal(typed, resolved);
        Assert.Equal(typed, new SearchQuery(resolved, 0, "manual search").Text);
    }

    [Fact]
    public void AQueryNobodyCouldMeanIsCutToLength()
    {
        var rambling = new string('a', 500);

        var resolved = ThemeOrchestrator.ResolveQuery(rambling, () => "unused");

        Assert.Equal(200, resolved.Length);
    }

    [Fact]
    public void WithNothingTypedAndNothingToFallBackOnThereIsNoSearch() =>
        Assert.Empty(ThemeOrchestrator.ResolveQuery(null, () => null));
}

/// <summary>
/// Choosing a theme by hand has to leave the record describing the theme that is actually there.
/// </summary>
public class ManualAssignmentTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

    private static ThemeIndexEntry Assigned() => new()
    {
        ItemId = Guid.NewGuid(),
        Label = "Battlestar Galactica",
        State = ThemeItemState.AutoAssigned,
        ChosenId = "originalvid",
        ChosenTitle = "Battlestar Galactica Theme",
        ChosenUrl = "https://www.youtube.com/watch?v=originalvid",
        Score = 88.5,
        ScoreBreakdown = new List<string> { "Total: 88.5/100", "TitleSimilarity: +1.00 x30 = +30.00 (names the show)" },
        Rejected = new List<RejectedCandidateRecord>
        {
            new() { Id = "runnerupvid", Title = "Prelude to War", Url = "https://www.youtube.com/watch?v=runnerupvid", Score = 61, Reason = "ordinary title" },
            new() { Id = "otherrunner", Title = "Something Else", Url = "https://www.youtube.com/watch?v=otherrunner", Score = 40, Reason = "names something else" },
        },
    };

    [Fact]
    public void ChoosingADifferentVideoDiscardsTheScoreThatDescribedTheOldOne()
    {
        var entry = Assigned();

        ThemeOrchestrator.SettleScoreForNewChoice(entry, "runnerupvid", Now);

        Assert.Null(entry.Score);
        Assert.Equal(new[] { "Chosen by hand on 2026-09-17." }, entry.ScoreBreakdown);
    }

    [Fact]
    public void TheChosenVideoStopsBeingListedAsOneThatWasPassedOver()
    {
        var entry = Assigned();

        ThemeOrchestrator.SettleScoreForNewChoice(entry, "runnerupvid", Now);

        Assert.DoesNotContain(entry.Rejected, rejected => rejected.Id == "runnerupvid");
        Assert.Contains(entry.Rejected, rejected => rejected.Id == "otherrunner");
    }

    [Fact]
    public void ApprovingTheVideoThatWasAlreadyProposedKeepsItsScore()
    {
        var entry = Assigned();

        ThemeOrchestrator.SettleScoreForNewChoice(entry, "originalvid", Now);

        Assert.Equal(88.5, entry.Score);
        Assert.Equal(2, entry.ScoreBreakdown.Count);
        Assert.Equal(2, entry.Rejected.Count);
    }

    [Fact]
    public void AnItemThatNeverHadAThemeSimplyGetsTheNote()
    {
        var entry = new ThemeIndexEntry { ItemId = Guid.NewGuid(), Label = "Alien", State = ThemeItemState.NoCandidate };

        ThemeOrchestrator.SettleScoreForNewChoice(entry, "somevideoid", Now);

        Assert.Null(entry.Score);
        Assert.Single(entry.ScoreBreakdown);
    }
}

/// <summary>
/// Covers pooling a manual search's phrasings before deciding what is worth inspecting.
/// </summary>
/// <remarks>
/// This is the thing a run does not do. A run takes the best few of each rung separately, so a
/// video that comes sixth on two different phrasings is never looked at although it would be near
/// the top of the pooled set. The manual search pools first and inspects the best of the lot in
/// one request.
/// </remarks>
public class ManualSearchPoolingTests
{
    private static Candidate FoundBy(string id, int rank, string template) => new()
    {
        Id = id,
        Url = "https://www.youtube.com/watch?v=" + id,
        Title = "Main Title",
        FoundBy = new SearchQuery(template, rank, template),
        IsHydrated = false,
    };

    [Fact]
    public void EveryPhrasingsResultsArePutTogether()
    {
        var pooled = ThemeOrchestrator.Pool(new[]
        {
            new[] { FoundBy("aaaaaaaaaaa", 0, "{title} opening theme song") },
            new[] { FoundBy("bbbbbbbbbbb", 1, "{title} {composer} theme") },
        });

        Assert.Equal(2, pooled.Count);
    }

    [Fact]
    public void AVideoFoundTwiceIsShownOnce()
    {
        var pooled = ThemeOrchestrator.Pool(new[]
        {
            new[] { FoundBy("aaaaaaaaaaa", 0, "{title} opening theme song") },
            new[] { FoundBy("aaaaaaaaaaa", 4, "{title} intro") },
        });

        Assert.Single(pooled);
    }

    [Fact]
    public void ItKeepsTheMostSpecificPhrasingThatFoundIt()
    {
        // The specificity bonus is worth real points. A video that turns up under both the
        // composer phrasing and the vaguest rung was found by the composer phrasing.
        var pooled = ThemeOrchestrator.Pool(new[]
        {
            new[] { FoundBy("aaaaaaaaaaa", 5, "{title} soundtrack main theme") },
            new[] { FoundBy("aaaaaaaaaaa", 1, "{title} {composer} theme") },
            new[] { FoundBy("aaaaaaaaaaa", 3, "{title} theme song") },
        });

        Assert.Equal(1, Assert.Single(pooled).FoundBy.Rank);
    }

    [Fact]
    public void APhrasingThatFoundNothingCostsNothing()
    {
        var pooled = ThemeOrchestrator.Pool(new[]
        {
            Array.Empty<Candidate>(),
            new[] { FoundBy("aaaaaaaaaaa", 2, "{title} main title theme") },
            Array.Empty<Candidate>(),
        });

        Assert.Single(pooled);
    }

    [Fact]
    public void NothingFoundAtAllIsEmptyRatherThanAnError()
    {
        Assert.Empty(ThemeOrchestrator.Pool(Array.Empty<IReadOnlyList<Candidate>>()));
    }

    [Fact]
    public void TheSameVideoTwiceInOneBatchDoesNotFaultTheSearch()
    {
        // yt-dlp has been seen to return one video twice in a batch. Merge used to build its
        // lookup with ToDictionary, which throws on a duplicate key -- so the page showed an
        // error instead of results, for a reason that had nothing to do with the search.
        var twice = new[] { FoundBy("aaaaaaaaaaa", 0, "manual search"), FoundBy("aaaaaaaaaaa", 0, "manual search") };

        var merged = ThemeOrchestrator.Merge(
            twice.Select(candidate => new ScoreResult
            {
                Candidate = candidate,
                Total = 40,
                Breakdown = new[] { new Signal("TitleSimilarity", 1, 40, "looks right") },
            }).ToList(),
            twice.Select(candidate => new ScoreResult
            {
                Candidate = candidate,
                Total = 80,
                Breakdown = new[] { new Signal("TitleSimilarity", 1, 80, "looks right") },
            }).ToList());

        var only = Assert.Single(merged);
        Assert.Equal(80, only.Total);
    }
}
