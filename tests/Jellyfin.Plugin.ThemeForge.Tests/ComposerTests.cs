using System;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the use of the composer Jellyfin already holds for an item.
/// </summary>
public class ComposerTests
{
    private static readonly string[] Edmonson = { "Greg Edmonson" };

    [Fact]
    public void ACandidateNamingTheComposerScoresFullMarks()
    {
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Firefly - Main Title Theme", channel: "Greg Edmonson - Topic"),
            TestData.Context(TestData.Series("Firefly", 2002, composers: Edmonson)));

        Assert.Equal(1.0, verdict.Raw);
        Assert.Contains("Greg Edmonson", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComposerIsFoundOnTheArtistCreditToo()
    {
        var candidate = TestData.Candidate("Main Title", channel: "Some Channel") with { Artist = "Greg Edmonson" };

        Assert.NotNull(ComposerRule.NamedComposer(Edmonson, candidate));
    }

    [Fact]
    public void ACandidateNotNamingTheComposerIsNotPenalised()
    {
        // Most fan uploads never mention the composer. Holding that against them would push down
        // the ordinary correct answer to reward the rare perfect one.
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Firefly Theme Song", channel: "Some Channel"),
            TestData.Context(TestData.Series("Firefly", 2002, composers: Edmonson)));

        Assert.Equal(0, verdict.Raw);
        Assert.False(verdict.IsVeto);
    }

    [Fact]
    public void AnItemWithNoComposerAbstains()
    {
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Firefly - Greg Edmonson"),
            TestData.Context(TestData.Series("Firefly", 2002)));

        Assert.Equal(0, verdict.Raw);
    }

    [Fact]
    public void ASimilarSurnameIsNotTheComposer() =>
        Assert.Null(ComposerRule.NamedComposer(new[] { "Hans Zimmer" }, TestData.Candidate("Hans Zimmermann Piano Tutorial")));

    [Fact]
    public void AComposerTemplateIsExpandedOncePerComposer()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} {composer} theme", "{title} theme" };

        var queries = new QueryPlanner().Plan(
            TestData.Series("Firefly", 2002, composers: new[] { "Greg Edmonson", "Someone Else" }),
            configuration);

        Assert.Equal(
            new[] { "Firefly Greg Edmonson theme", "Firefly Someone Else theme", "Firefly theme" },
            queries.Select(query => query.Text).ToArray());
    }

    [Fact]
    public void AComposerTemplateIsSkippedWhenThereIsNoComposer()
    {
        // Collapsing the placeholder would only repeat a generic search the ladder already runs.
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} {composer} theme", "{title} theme" };

        var queries = new QueryPlanner().Plan(TestData.Series("Firefly", 2002), configuration);

        Assert.Equal(new[] { "Firefly theme" }, queries.Select(query => query.Text).ToArray());
    }

    [Fact]
    public void TheComposerCorroboratesAnOrdinaryTitle()
    {
        // "Lost" is too ordinary to trust on its own; the composer's name is what makes it safe.
        var without = TestData.Engine().Score(
            TestData.Candidate("Lost - Michael Giacchino", duration: 70),
            TestData.Context(TestData.Series("Lost", 2004)));

        var with = TestData.Engine().Score(
            TestData.Candidate("Lost - Michael Giacchino", duration: 70),
            TestData.Context(TestData.Series("Lost", 2004, composers: new[] { "Michael Giacchino" })));

        Assert.True(with.Total > without.Total, $"{with.Total:0.0} should exceed {without.Total:0.0}");
    }

    [Fact]
    public void TheShippedLaddersNameTheComposer()
    {
        Assert.Contains(ShippedTemplates.Series, template => template.Contains(QueryPlanner.ComposerPlaceholder, StringComparison.Ordinal));
        Assert.Contains(ShippedTemplates.Movies, template => template.Contains(QueryPlanner.ComposerPlaceholder, StringComparison.Ordinal));
    }

    [Fact]
    public void ALadderStillOnThePreviousDefaultIsBroughtUpToDate()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[]
        {
            "{title} opening theme song",
            "{title} main title theme",
            "{title} theme song",
            "{title} intro",
            "{title} soundtrack main theme",
        };

        Assert.True(ShippedTemplates.Upgrade(configuration));
        Assert.Equal(ShippedTemplates.Series, configuration.SeriesQueryTemplates);
    }

    [Fact]
    public void AnEditedLadderIsLeftAlone()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} my own query" };
        configuration.MovieQueryTemplates = ShippedTemplates.Movies.ToArray();

        Assert.False(ShippedTemplates.Upgrade(configuration));
        Assert.Equal(new[] { "{title} my own query" }, configuration.SeriesQueryTemplates);
    }
}
