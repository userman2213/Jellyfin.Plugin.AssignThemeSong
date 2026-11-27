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

        Assert.Contains(plan, query => query.Text == "Blade Runner 1982 Vangelis main theme");
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

        var series = _planner.Plan(TestData.Series("X", composers: Edmonson), configuration).Select(query => query.Text).ToList();
        var film = _planner.Plan(TestData.Movie("X", composers: Edmonson), configuration).Select(query => query.Text).ToList();

        Assert.Contains(series, text => text.StartsWith("series ", System.StringComparison.Ordinal));
        Assert.DoesNotContain(series, text => text.StartsWith("movie ", System.StringComparison.Ordinal));
        Assert.Contains(film, text => text.StartsWith("movie ", System.StringComparison.Ordinal));
        Assert.DoesNotContain(film, text => text.StartsWith("series ", System.StringComparison.Ordinal));
    }

    // ---- the credit searches do not depend on the saved templates -------------------------

    public static TheoryData<string[]?> TemplatesThatNameNoCredit() => new()
    {
        new[] { "{title} opening theme song", "{title} main title theme", "{title} soundtrack" },
        System.Array.Empty<string>(),
        null,
    };

    [Theory]
    [MemberData(nameof(TemplatesThatNameNoCredit))]
    public void ATitleWithAComposerIsSearchedWhateverTheSavedTemplatesHold(string[]? saved)
    {
        // 3.1.0 took its searches from the saved templates alone. A server whose templates named
        // no credit searched for nothing: 844 of 963 titles, every one with a composer known.
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = saved!;
        configuration.SeriesQueryTemplates = saved!;

        var film = _planner.Plan(TestData.Movie("Lucky Strike", 2026, composers: new[] { "Larry Groupé" }), configuration);
        var series = _planner.Plan(TestData.Series("Firefly", composers: Edmonson), configuration);

        Assert.Equal("Lucky Strike Larry Groupé main theme", film[0].Text);
        Assert.Equal("Firefly Greg Edmonson theme", series[0].Text);
        Assert.All(film.Concat(series), query => Assert.True(
            query.Text.Contains("Larry Groupé", System.StringComparison.Ordinal) || query.Text.Contains("Greg Edmonson", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void AKnownThemeIsSearchedFirstWhateverTheSavedTemplatesHold()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} intro" };

        var plan = _planner.Plan(
            TestData.Series("The Sopranos", theme: new Engines.Credits.ThemeSong("Woke Up This Morning", "Alabama 3")),
            configuration);

        Assert.Equal("The Sopranos Woke Up This Morning Alabama 3", Assert.Single(plan).Text);
    }

    [Fact]
    public void ASavedTemplateNamingACreditRunsAfterTheBuiltInOnes()
    {
        var configuration = TestData.Config();
        configuration.MovieQueryTemplates = new[] { "{title} {composer} suite", "{title} {composer} main theme" };

        var plan = _planner.Plan(TestData.Movie("Alien", 1979, composers: new[] { "Jerry Goldsmith" }), configuration).Select(query => query.Text).ToList();

        Assert.Equal("Alien Jerry Goldsmith main theme", plan[0]);
        Assert.Equal("Alien Jerry Goldsmith suite", plan[^1]);
        Assert.Equal(plan.Distinct().Count(), plan.Count);
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

        Assert.Equal(2, plan.Count(query => query.Text.EndsWith("Kenji Kawai theme", System.StringComparison.Ordinal)));
        Assert.Contains(plan, query => query.Text.Contains("koukaku", System.StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicateQueriesAreNotRepeated()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} {composer} theme", "{title} {composer} theme", "  " };

        var plan = _planner.Plan(TestData.Series("Firefly", composers: Edmonson), configuration);

        Assert.Single(plan, query => query.Text == "Firefly Greg Edmonson theme");
        Assert.Equal(plan.Select(query => query.Text).Distinct().Count(), plan.Count);
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

        Assert.Equal(
            new[] { "Lucky Strike Larry Groupé main theme", "Lucky Strike Larry Groupé main title", "Lucky Strike Larry Groupé soundtrack" },
            plan.Select(query => query.Text));
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
