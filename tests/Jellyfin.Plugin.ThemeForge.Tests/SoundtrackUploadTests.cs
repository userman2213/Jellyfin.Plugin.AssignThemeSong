using System;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers rights-holder uploads titled by track rather than by show.
/// </summary>
/// <remarks>
/// "Main Title" on the composer's "- Topic" channel is the best-sourced recording of a theme
/// there is, and it cannot name the show in its title. It used to be vetoed on sight before
/// hydration -- a veto that is permanent -- and vanished without a trace.
/// </remarks>
public class SoundtrackUploadTests
{
    private const string Album = "Battlestar Galactica: Season 1 (Original Soundtrack)";

    private static Candidate Topic(string title, bool hydrated, string? album = null) =>
        TestData.Candidate(title, channel: "Bear McCreary - Topic") with { IsHydrated = hydrated, Album = album };

    [Fact]
    public void BeforeHydrationATopicUploadIsHeldNotVetoed()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Main Title", hydrated: false),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.False(verdict.IsVeto);
        Assert.True(verdict.Raw > 0, "it must rank above every veto");
        Assert.True(verdict.Raw < 0.5, "and below every candidate that names the show");
    }

    [Fact]
    public void AnOrdinaryUploadThatDoesNotNameTheShowIsStillVetoedBeforeHydration()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Main Title", channel: "Some Channel") with { IsHydrated = false },
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void AfterHydrationTheAlbumNamesTheShow()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Main Title", hydrated: true, album: Album),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.False(verdict.IsVeto, verdict.Reason);
        Assert.True(verdict.Raw >= 0.85, $"scored {verdict.Raw:0.00}");
        Assert.Contains("album", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AfterHydrationWithNoAlbumItIsVetoedForReal()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Main Title", hydrated: true),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void AnAlbumForADifferentShowIsVetoed()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Main Title", hydrated: true, album: "Firefly (Original Television Soundtrack)"),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void TheAlbumIsNotJudgedOnItsYear()
    {
        // A soundtrack's release or reissue year is not the production's year.
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Main Title", hydrated: true, album: "Battlestar Galactica (2010 Anniversary Edition)"),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.False(verdict.IsVeto, verdict.Reason);
    }

    [Fact]
    public void AnUploadWholeTitleNamesTheShowNeedsNoAlbum()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            Topic("Battlestar Galactica Main Title", hydrated: false),
            TestData.Context(TestData.Series("Battlestar Galactica", 2004)));

        Assert.False(verdict.IsVeto);
        Assert.Equal(1.0, verdict.Raw);
    }

    [Fact]
    public void TheWholeEngineRanksAHeldUploadBelowANamedOneAndAboveAVeto()
    {
        var context = TestData.Context(TestData.Series("Battlestar Galactica", 2004));
        var ranked = TestData.Engine().Rank(
            new[]
            {
                TestData.Candidate("Battlestar Galactica Opening Credits", duration: 60) with { IsHydrated = false },
                Topic("Main Title", hydrated: false),
                TestData.Candidate("Star Trek Opening", duration: 60) with { IsHydrated = false },
            },
            context);

        Assert.Contains("Battlestar", ranked[0].Candidate.Title, StringComparison.Ordinal);
        Assert.Equal("Main Title", ranked[1].Candidate.Title);
        Assert.False(ranked[1].IsVetoed);
        Assert.True(ranked[2].IsVetoed);
    }
}
