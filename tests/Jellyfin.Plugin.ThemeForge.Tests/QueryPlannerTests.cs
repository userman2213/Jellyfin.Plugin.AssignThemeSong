using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class QueryPlannerTests
{
    private readonly QueryPlanner _planner = new();

    [Fact]
    public void SubstitutesTitleAndYear()
    {
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {year} main theme" };

        var plan = _planner.Plan(TestData.Movie("Blade Runner", 1982), configuration);

        Assert.Equal("Blade Runner 1982 main theme", plan[0].Text);
    }

    [Fact]
    public void CollapsesTheYearPlaceholderWhenNoYearIsKnown()
    {
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {year} main theme" };

        var plan = _planner.Plan(TestData.Movie("Nosferatu", year: null), configuration);

        // A literal "{year}" or a double space in the search text would hurt the results.
        Assert.Equal("Nosferatu main theme", plan[0].Text);
    }

    [Fact]
    public void UsesTheSeriesLadderForSeriesAndTheMovieLadderForFilms()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "series {title}" };
        configuration.MovieQueryTemplates = new[] { "movie {title}" };

        Assert.StartsWith("series ", _planner.Plan(TestData.Series("X"), configuration)[0].Text, System.StringComparison.Ordinal);
        Assert.StartsWith("movie ", _planner.Plan(TestData.Movie("X"), configuration)[0].Text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void RanksQueriesInTemplateOrder()
    {
        var plan = _planner.Plan(TestData.Series("Firefly"), TestData.Config());
        Assert.Equal(Enumerable.Range(0, plan.Count), plan.Select(query => query.Rank));
    }

    [Fact]
    public void MoreSpecificQueriesCarryAHigherBonus()
    {
        var plan = _planner.Plan(TestData.Series("Firefly"), TestData.Config());
        Assert.True(plan[0].SpecificityBonus > plan[^1].SpecificityBonus);
    }

    [Fact]
    public void AlternateTitlesAreSearchedToo()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} theme" };

        var plan = _planner.Plan(
            TestData.Series("Ghost in the Shell", alternates: new[] { "koukaku kidoutai" }),
            configuration);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, query => query.Text.Contains("koukaku", System.StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateQueriesAreNotRepeated()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} theme", "{title} theme", "  " };

        var plan = _planner.Plan(TestData.Series("Firefly"), configuration);

        Assert.Single(plan);
    }
}
