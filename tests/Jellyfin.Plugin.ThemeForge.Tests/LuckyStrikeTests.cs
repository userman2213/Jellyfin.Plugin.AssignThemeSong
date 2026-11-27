using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// The reported failure, end to end through the real pieces: the 2026 war film Lucky Strike was
/// given a western song called "Lucky Strike" as its theme, and other films AI-generated
/// "official theme songs".
/// </summary>
/// <remarks>
/// IMDb's soundtrack listing for the film names a licensed song of the same name; IMDb's crew
/// data names Larry Groupé as its composer. What is asserted is the whole chain: the song is not
/// taken as the theme, the composer is what is searched for, the generated upload is rejected,
/// and the western song is never assigned -- whether or not the real theme turns up.
/// </remarks>
public class LuckyStrikeTests
{
    private static readonly SoundtrackEntry[] Listing =
    {
        new("Lucky Strike", "A Country Act", "A Songwriter"),
        new("Whiskey River Waltz", "Another Act", "Another Songwriter"),
    };

    private static readonly string[] CrewComposers = { "Larry Groupé" };

    [Fact]
    public void TheListingsSameNamedSongIsNotTakenAsTheFilmsTheme() =>
        Assert.Null(ImdbSoundtrackPage.ChooseTheme(Listing, isSeries: false, "Lucky Strike"));

    [Fact]
    public void TheFirstSearchIsForTheComposerAndNoneIsForTheTitleAlone()
    {
        var plan = new QueryPlanner().Plan(Film(), TestData.Config());

        Assert.Equal("Lucky Strike Larry Groupé main theme", plan[0].Text);
        Assert.All(plan, query => Assert.Contains("Larry Groupé", query.Text, StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealThemeIsAssignedAndNeitherTheSongNorTheGeneratedOneIs()
    {
        var decision = Decide(GeneratedTheme(), WesternSong(), RealTheme());

        Assert.Equal(DecisionOutcome.AutoAssign, decision.Outcome);
        Assert.Equal("realtheme01", decision.Best!.Candidate.Id);
    }

    [Fact]
    public void WithoutTheRealThemeTheWesternSongIsStillNeverAssigned()
    {
        var decision = Decide(GeneratedTheme(), WesternSong());

        Assert.NotEqual(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    [Fact]
    public void TheGeneratedThemeSongIsDisqualified()
    {
        var ranked = TestData.Engine().Rank(new[] { GeneratedTheme() }, TestData.Context(Film()));

        Assert.True(ranked[0].IsVetoed);
    }

    private static ThemeDecision Decide(params Candidate[] found)
    {
        var configuration = TestData.Config();
        var ranked = TestData.Engine().Rank(found, TestData.Context(Film(), configuration));
        return new DecisionPolicy().Decide(ranked, configuration);
    }

    /// <summary>The film as the pipeline sees it: no theme from the listing, the composer from IMDb's crew data.</summary>
    private static Engines.Identity.MediaIdentity Film()
    {
        var theme = ImdbSoundtrackPage.ChooseTheme(Listing, isSeries: false, "Lucky Strike");
        return TestData.Movie(
            "Lucky Strike",
            2026,
            composers: CrewComposers,
            theme: theme is null ? null : new ThemeSong(theme.Title, theme.Performer));
    }

    private static SearchQuery Query() => new("Lucky Strike Larry Groupé main theme", 0, "{title} {composer} main theme");

    private static Candidate GeneratedTheme() => TestData.Candidate(
        "Lucky Strike – Official Theme Song (AI Generated)",
        duration: 154,
        views: 2_400,
        channel: "Movie Theme Songs",
        id: "generated01",
        foundBy: Query());

    private static Candidate WesternSong() => TestData.Candidate(
        "A Country Act - Lucky Strike (Official Audio)",
        duration: 201,
        views: 310_000,
        channel: "A Country Act",
        id: "western0001",
        foundBy: Query());

    private static Candidate RealTheme() => TestData.Candidate(
        "Lucky Strike (2026) - Main Theme - Larry Groupé",
        duration: 132,
        views: 5_100,
        channel: "Soundtrack World",
        id: "realtheme01",
        foundBy: Query());
}
