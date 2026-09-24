using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers searching for a theme by what it is called, and recognising it when it is found.
/// </summary>
/// <remarks>
/// For a show whose theme is a song, the song's own uploads are titled by song and artist --
/// "Alabama 3 - Woke Up This Morning" -- and never name the show. Searching for them needs the
/// song's title; recognising them needs the song too, or they are rejected for not naming the show.
/// Both halves are pinned here, with the performer requirement that keeps an ordinary song title
/// from passing as somebody's theme.
/// </remarks>
public class ThemeSearchTests
{
    private static readonly ThemeSong WokeUp = new("Woke Up This Morning", "Alabama 3");

    private static readonly ThemeSong Superman = new("Superman", "Lazlo Bane");

    private readonly QueryPlanner _planner = new();

    // ---- The ladder ----

    [Fact]
    public void TheThemeIsSearchedForFirst()
    {
        var plan = _planner.Plan(TestData.Series("The Sopranos", 1999, theme: WokeUp), TestData.Config());

        Assert.Equal("The Sopranos Woke Up This Morning Alabama 3", plan[0].Text);
        Assert.Equal(0, plan[0].Rank);
    }

    [Fact]
    public void TheRungIsSkippedWhenTheThemeIsNotKnown()
    {
        var configuration = TestData.Config();
        var plan = _planner.Plan(TestData.Series("Lost", 2004), configuration);

        // Exactly the ladder as it was, and no query with the placeholder collapsed to nothing.
        Assert.DoesNotContain(plan, query => query.Template.Contains(QueryPlanner.ThemePlaceholder, StringComparison.Ordinal));
        Assert.Equal("Lost opening theme song", plan[0].Text);
    }

    [Fact]
    public void AThemeWithNoPerformerIsSearchedForByTitle()
    {
        var plan = _planner.Plan(TestData.Series("Game of Thrones", 2011, theme: new ThemeSong("Main Title", null)), TestData.Config());

        Assert.Equal("Game of Thrones Main Title", plan[0].Text);
    }

    [Fact]
    public void TheTitleIsNotRepeatedWhenTheSongCarriesIt()
    {
        var plan = _planner.Plan(TestData.Movie("Skyfall", 2012, theme: new ThemeSong("Skyfall", "Adele")), TestData.Config());

        Assert.Equal("Skyfall Adele", plan[0].Text);
    }

    [Fact]
    public void ATitleInsideAnotherWordIsStillATitle()
    {
        // Whole words: "Her" is not repeated in "Other", so it must stay.
        var plan = _planner.Plan(TestData.Movie("Her", 2013, theme: new ThemeSong("The Other Song", "Karen O")), TestData.Config());

        Assert.Equal("Her The Other Song Karen O", plan[0].Text);
    }

    [Fact]
    public void TheThemeIsSearchedForUnderThePrimaryTitleOnly()
    {
        // A song is called the same thing in every market; searching it once per alternate title
        // would only spend searches.
        var identity = TestData.Series("The Sopranos", 1999, alternates: new[] { "les soprano" }, theme: WokeUp);

        var plan = _planner.Plan(identity, TestData.Config());

        Assert.Single(plan, query => query.Template.Contains(QueryPlanner.ThemePlaceholder, StringComparison.Ordinal));
    }

    [Fact]
    public void ALadderNobodyEditedIsUpgradedFrom25()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[]
        {
            "{title} opening theme song",
            "{title} {composer} theme",
            "{title} main title theme",
            "{title} theme song",
            "{title} original soundtrack main title",
            "{title} intro",
            "{title} soundtrack main theme",
        };
        configuration.MovieQueryTemplates = new[]
        {
            "{title} {year} main theme soundtrack",
            "{title} {composer} main theme",
            "{title} main title theme",
            "{title} original soundtrack",
            "{title} theme song",
            "{title} soundtrack suite",
        };

        Assert.True(ShippedTemplates.Upgrade(configuration));
        Assert.Equal("{title} {theme}", configuration.SeriesQueryTemplates[0]);
        Assert.Equal("{title} {theme}", configuration.MovieQueryTemplates[0]);
    }

    [Fact]
    public void AnEditedLadderIsLeftAlone()
    {
        var configuration = TestData.Config();
        configuration.SeriesQueryTemplates = new[] { "{title} opening theme song", "{title} my own phrasing" };

        ShippedTemplates.Upgrade(configuration);

        Assert.Equal(new[] { "{title} opening theme song", "{title} my own phrasing" }, configuration.SeriesQueryTemplates);
    }

    // ---- Recognising it ----

    [Fact]
    public void TheSongsOwnUploadIsNotRejectedForNotNamingTheShow()
    {
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Alabama 3 - Woke Up This Morning (Chosen One Mix)"),
            TestData.Context(TestData.Series("The Sopranos", 1999, theme: WokeUp)));

        Assert.False(verdict.IsVeto);
        Assert.Equal(0.9, verdict.Raw);
        Assert.Contains("Woke Up This Morning", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheThemeTheSameUploadIsRejected()
    {
        // The control for the test above: this is the veto the theme song lifts.
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Alabama 3 - Woke Up This Morning (Chosen One Mix)"),
            TestData.Context(TestData.Series("The Sopranos", 1999)));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void ARightsHolderUploadTitledByTrackIsRecognisedByItsChannel()
    {
        // A distributor upload is titled by track and credited to the artist's "- Topic" channel.
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Woke Up This Morning (Chosen One Mix)", channel: "Alabama 3 - Topic"),
            TestData.Context(TestData.Series("The Sopranos", 1999, theme: WokeUp)));

        Assert.False(verdict.IsVeto);
        Assert.Contains("theme song", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherSongWithTheSameTitleIsStillRejected()
    {
        // "Superman" is Scrubs' theme only as Lazlo Bane sings it.
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Five for Fighting - Superman (It's Not Easy)"),
            TestData.Context(TestData.Series("Scrubs", 2001, theme: Superman)));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void ASongWithNoKnownPerformerIsNotEnoughOnItsOwn()
    {
        // "Main Title" names half the soundtracks ever released.
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Main Title (From the Original Soundtrack)"),
            TestData.Context(TestData.Series("Game of Thrones", 2011, theme: new ThemeSong("Main Title", null))));

        Assert.True(verdict.IsVeto);
    }

    [Fact]
    public void AnOrdinaryTitleIsCorroboratedByItsThemeSong()
    {
        // "Friends" is an ordinary word and is capped unless something backs it up. Its theme song,
        // named by its performer, does.
        var theme = new ThemeSong("I'll Be There for You", "The Rembrandts");
        var verdict = new TitleSimilarityRule().Evaluate(
            TestData.Candidate("Friends - I'll Be There For You - The Rembrandts"),
            TestData.Context(TestData.Series("Friends", 1994, theme: theme)));

        Assert.DoesNotContain("nothing here backs it up", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void NamingTheThemeSongEarnsTheFullBonus()
    {
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Alabama 3 - Woke Up This Morning"),
            TestData.Context(TestData.Series("The Sopranos", 1999, theme: WokeUp)));

        Assert.Equal(1.0, verdict.Raw);
        Assert.Contains("names the theme song", verdict.Reason, StringComparison.Ordinal);
    }

    // ---- Where it comes from ----

    private sealed class Knows : IComposerCatalogue
    {
        private readonly ResearchedCredits _credits;

        public Knows(ResearchedCredits credits) => _credits = credits;

        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ComposerSnapshot());

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public ResearchedCredits Known(IReadOnlyList<string> keys) => _credits;
    }

    private sealed class Jellyfin : IPeopleLookup
    {
        private readonly string[] _composers;

        public Jellyfin(params string[] composers) => _composers = composers;

        public IReadOnlyList<string> Composers(BaseItem item) => _composers;

        public IReadOnlyList<string> MusicCredits(BaseItem item) => Array.Empty<string>();

        public ThemeSong? Theme(BaseItem item) => null;
    }

    private static Series Dexter()
    {
        var series = new Series { Name = "Dexter" };
        series.SetProviderId(MetadataProvider.Imdb, "tt0773262");
        return series;
    }

    [Fact]
    public void TheThemeComesFromResearch()
    {
        var lookup = new ResearchedPeopleLookup(new Jellyfin(), new Knows(ResearchedCredits.None with { Theme = WokeUp }));

        Assert.Equal(WokeUp, lookup.Theme(Dexter()));
    }

    [Fact]
    public void WhoeverWroteTheThemeGoesFirstEvenBesideJellyfinsOwnCredits()
    {
        // Jellyfin says Daniel Licht, and is right about the score. The theme is Rolfe Kent's, and a
        // theme is what is being searched for. Jellyfin's name stays.
        var lookup = new ResearchedPeopleLookup(
            new Jellyfin("Daniel Licht"),
            new Knows(ResearchedCredits.None with { ThemeComposers = new[] { "Rolfe Kent" } }));

        Assert.Equal(new[] { "Rolfe Kent", "Daniel Licht" }, lookup.Composers(Dexter()));
    }

    [Fact]
    public void AThemeComposerAlreadyCreditedIsNotListedTwice()
    {
        var lookup = new ResearchedPeopleLookup(
            new Jellyfin("Ramin Djawadi"),
            new Knows(ResearchedCredits.None with { ThemeComposers = new[] { "Ramin Djawadi" } }));

        Assert.Equal(new[] { "Ramin Djawadi" }, lookup.Composers(Dexter()));
    }
}
