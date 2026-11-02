using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class QueryPlannerTests
{
    private static readonly string[] Vangelis = { "Vangelis" };

    private static readonly string[] Edmonson = { "Greg Edmonson" };

    private readonly QueryPlanner _planner = new();

    [Fact]
    public void SubstitutesTitleYearAndComposer()
    {
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {year} {composer} main theme" };

        var plan = _planner.Plan(TestData.Movie("Blade Runner", 1982, composers: Vangelis), configuration);

        Assert.Equal("Blade Runner 1982 Vangelis main theme", plan[0].Text);
    }

    [Fact]
    public void CollapsesTheYearPlaceholderWhenNoYearIsKnown()
    {
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {year} {composer} main theme" };

        var plan = _planner.Plan(TestData.Movie("Nosferatu", year: null, composers: new[] { "Hans Erdmann" }), configuration);

        // A literal "{year}" or a double space in the search text would hurt the results.
        Assert.Equal("Nosferatu Hans Erdmann main theme", plan[0].Text);
    }

    [Fact]
    public void UsesTheSeriesLadderForSeriesAndTheMovieLadderForFilms()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "series {title} {composer}" };
        configuration.MovieQueryTemplates = new[] { "movie {title} {composer}" };

        Assert.StartsWith("series ", _planner.Plan(TestData.Series("X", composers: Edmonson), configuration)[0].Text, System.StringComparison.Ordinal);
        Assert.StartsWith("movie ", _planner.Plan(TestData.Movie("X", composers: Edmonson), configuration)[0].Text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RanksQueriesInTemplateOrder()
    {
        var plan = _planner.Plan(TestData.Series("Firefly", composers: Edmonson), TestData.Config());

        Assert.NotEmpty(plan);
        Assert.Equal(Enumerable.Range(0, plan.Count), plan.Select(query => query.Rank));
    }

    [Fact]
    public void MoreSpecificQueriesCarryAHigherBonus()
    {
        var plan = _planner.Plan(TestData.Series("Firefly", composers: Edmonson), TestData.Config());

        Assert.True(plan.Count > 1);
        Assert.True(plan[0].SpecificityBonus > plan[^1].SpecificityBonus);
    }

    [Fact]
    public void AlternateTitlesAreSearchedToo()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} {composer} theme" };

        var plan = _planner.Plan(
            TestData.Series("Ghost in the Shell", alternates: new[] { "koukaku kidoutai" }, composers: new[] { "Kenji Kawai" }),
            configuration);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, query => query.Text.Contains("koukaku", System.StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateQueriesAreNotRepeated()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} {composer} theme", "{title} {composer} theme", "  " };

        var plan = _planner.Plan(TestData.Series("Firefly", composers: Edmonson), configuration);

        Assert.Single(plan);
    }

    // ---- never the title on its own, unattended --------------------------------------------

    [Fact]
    public void ATitleNobodyHasCreditedIsNotSearchedUnattended()
    {
        // What "{title} theme song" finds for a title with no credits is what was assigned to
        // Lucky Strike: an AI-generated "theme song", or any song that happens to share its name.
        var plan = _planner.Plan(TestData.Movie("Lucky Strike", 2026), TestData.Config());

        Assert.Empty(plan);
    }

    [Fact]
    public void EvenAnEditedLadderCannotMakeAnUnattendedRunSearchTheTitle()
    {
        // The rule lives here rather than in the shipped ladder, so a ladder saved by hand with the
        // old title searches in it still searches only the credits.
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} theme song", "{title} {composer} main theme", "{title} original soundtrack" };

        var plan = _planner.Plan(TestData.Movie("Lucky Strike", 2026, composers: new[] { "Larry Groupé" }), configuration);

        Assert.Equal(new[] { "Lucky Strike Larry Groupé main theme" }, plan.Select(query => query.Text));
    }

    [Fact]
    public void APersonSearchingByHandStillGetsTitleSearches()
    {
        // Nothing a person's search finds is assigned unless they pick it, so it may cast wider.
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {composer} main theme", "{title} theme song" };

        var plan = _planner.Plan(TestData.Movie("Lucky Strike", 2026, composers: new[] { "Larry Groupé" }), configuration, includeTitleSearches: true);

        Assert.Equal("Lucky Strike Larry Groupé main theme", plan[0].Text);
        Assert.Contains(plan, query => query.Text == "Lucky Strike theme song");
    }

    [Fact]
    public void APersonGetsSomethingToLookAtEvenForATitleNobodyHasCredited()
    {
        var plan = _planner.Plan(TestData.Movie("Lucky Strike", 2026), TestData.Config(), includeTitleSearches: true);

        Assert.Equal(new[] { "Lucky Strike 2026 theme", "Lucky Strike soundtrack" }, plan.Select(query => query.Text));
    }
}
