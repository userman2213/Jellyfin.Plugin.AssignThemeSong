using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Re-scores candidates YouTube actually returned during the assignment benchmark, for the titles
/// the benchmark got wrong, and pins that the right one now wins.
/// </summary>
/// <remarks>
/// Each case is the real title, channel, length and view count of what the searches turned up, and
/// the query that found it, copied from the benchmark run. The comment on each says what was
/// assigned before and why it was wrong.
/// </remarks>
public class BenchmarkRegressionTests
{
    private static readonly ScoringEngine Engine = TestData.Engine();

    private static SearchQuery Query(string text, int rank, string template) => new(text, rank, template);

    private static Candidate Real(string title, string channel, double seconds, long views, SearchQuery foundBy) =>
        TestData.Candidate(title, seconds, views, channel, new DateTime(2015, 1, 1), foundBy: foundBy);

    private static IReadOnlyList<ScoreResult> Rank(MediaIdentity identity, params Candidate[] candidates) =>
        Engine.Rank(candidates, TestData.Context(identity));

    [Fact]
    public void PulpFictionTakesDickDalesMisirlouOverAGenericOpeningThemeUpload()
    {
        // Before: "Pulp Fiction - Opening Theme" by Minotaures, 79.3. The research had named the
        // theme -- "Misirlou" by Dick Dale -- and the very first search found Dick Dale's own
        // recording, which scored 71.3: marked down for not saying "theme", and given 0.9 of the
        // credit for naming the theme that naming the film would have earned.
        var themed = Query("Pulp Fiction Misirlou Dick Dale", 0, "{title} {theme}");
        var generic = Query("Pulp Fiction 1994 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var identity = TestData.Movie("Pulp Fiction", 1994, theme: new ThemeSong("Misirlou", "Dick Dale"));

        var ranked = Rank(
            identity,
            Real("Pulp Fiction - Opening Theme", "Minotaures", 150, 4_578_850, generic),
            Real("Pulp Fiction Soundtrack - Girl, You'll Be A Woman Soon", "Motega82", 191, 4_862_195, generic),
            Real("Pulp Fiction Theme: Surf Rider", "EdD1EgJiNaJ", 198, 14_346_609, generic),
            Real("Dick Dale & The Del Tones \"Misirlou\" 1963", "FairDealDan", 156, 12_756_830, themed));

        Assert.Contains("Misirlou", ranked[0].Candidate.Title, StringComparison.Ordinal);
        Assert.True(ranked[0].Total >= TestData.Config().AutoAssignThreshold, $"scored {ranked[0].Total:F1}");
    }

    [Fact]
    public void DriveNoLongerTakesTheEighthTrackOfASoundtrackAlbum()
    {
        // Before: "Drive Original Soundtrack - 8. He Had a Good Time", a minor score cue uploaded
        // to a channel with 823 views, assigned at 75.9 -- because "soundtrack" earned as much
        // as "main title" would have.
        var year = Query("Drive 2011 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var composer = Query("Drive Cliff Martinez main theme", 2, "{title} {composer} main theme");
        var identity = TestData.Movie("Drive", 2011, composers: new[] { "Cliff Martinez" });

        var ranked = Rank(
            identity,
            Real("Drive Original Soundtrack - 8. He Had a Good Time", "Goat Music Hall of Fame", 97, 823, composer),
            Real("Cliff Martinez - I Drive", "bnk57", 124, 423_116, composer));

        Assert.Equal("Cliff Martinez - I Drive", ranked[0].Candidate.Title);
        var eighth = ranked.Single(r => r.Candidate.Title.Contains("8.", StringComparison.Ordinal));
        Assert.Contains(eighth.Breakdown, s => s.Rule == "KeywordAffinity" && s.Reason.Contains("track 8", StringComparison.Ordinal));
    }

    [Fact]
    public void InterstellarTakesTheMainThemeOverAnotherTrackOfTheScore()
    {
        // Before: three candidates tied at 90.6, and the tie went alphabetically to
        // "Hans Zimmer - Mountains" -- a track of the score, not its theme.
        var query = Query("Interstellar 2014 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var identity = TestData.Movie("Interstellar", 2014, composers: new[] { "Hans Zimmer" });

        var ranked = Rank(
            identity,
            Real("Hans Zimmer - Mountains (Interstellar Soundtrack)", "Jennyni20 (Epic Music)", 220, 21_017_687, query),
            Real("Interstellar Main Theme - Hans Zimmer", "Aura Music", 240, 16_125_404, query),
            Real("Interstellar • Main Theme • Hans Zimmer", "HD Film Tributes", 230, 2_114_001, query));

        Assert.Contains("Main Theme", ranked[0].Candidate.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAmericanOfficeIsNotGivenTheBritishOnesTheme()
    {
        // Before: "The Office (UK) Opening Theme and Closing Credits", assigned at 82.9 to the 2005
        // American series. It says which show it is in its own title.
        var query = Query("The Office opening theme song", 0, "{title} opening theme song");
        var identity = Series("The Office", 2005, "United States of America");

        var ranked = Rank(
            identity,
            Real("The Office (UK) Opening Theme and Closing Credits", "35bassman", 70, 812_954, query),
            Real("The Office (US) - Intro", "davfreim", 33, 1_373_140, query),
            Real("The Office Theme Song", "benblack25", 33, 5_256_654, query));

        var british = ranked.Single(r => r.Candidate.Title.Contains("(UK)", StringComparison.Ordinal));
        Assert.True(british.IsVetoed);
        Assert.Contains("British version", british.VetoReason, StringComparison.Ordinal);
        Assert.False(ranked[0].IsVetoed);
        Assert.DoesNotContain("(UK)", ranked[0].Candidate.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBritishOfficeStillTakesItsOwnTheme()
    {
        var query = Query("The Office opening theme song", 0, "{title} opening theme song");
        var identity = Series("The Office", 2001, "United Kingdom");

        var ranked = Rank(
            identity,
            Real("The Office (UK) Opening Theme and Closing Credits", "35bassman", 70, 812_954, query),
            Real("The Office (US) - Intro", "davfreim", 33, 1_373_140, query));

        Assert.Contains("(UK)", ranked[0].Candidate.Title, StringComparison.Ordinal);
        Assert.True(ranked.Single(r => r.Candidate.Title.Contains("(US)", StringComparison.Ordinal)).IsVetoed);
    }

    [Fact]
    public void GameOfThronesTakesTheSoundtracksOwnMainTitleOverACoverAndAnArrangement()
    {
        // After the first round of fixes: "Game of Thrones (Theme) by Ramin Djawadi/arr. Brown", a
        // concert band's arrangement, at 97.0 -- Wikidata names the theme "Game of Thrones Theme",
        // and the upload matched it word for word. Behind it, an orchestra's concert performance
        // tied with the soundtrack's own upload and won on views.
        var query = Query("Game of Thrones Game of Thrones Theme Ramin Djawadi", 0, "{title} {theme}");
        var identity = TestData.Series(
            "Game of Thrones",
            2011,
            composers: new[] { "Ramin Djawadi" },
            theme: new ThemeSong("Game of Thrones Theme", "Ramin Djawadi"));

        var ranked = Rank(
            identity,
            Real("Game of Thrones (Theme) by Ramin Djawadi/arr. Brown", "Hal Leonard Concert Band", 124, 593_595, query),
            Real("\"Main Theme\" - Game of Thrones (Ramin Djawadi) - Film Symphony Orchestra", "filmsymphony", 129, 5_280_953, query),
            Real("Game of Thrones S8 Official Soundtrack | Main Title - Ramin Djawadi | WaterTower", "WaterTower Music", 112, 4_762_992, query),
            Real("Ramin Djawadi performing \"Game Of Thrones Main Title\" Live on KCRW", "KCRW", 103, 296_536, query),
            Real("Game of Thrones S8 Official Soundtrack | A Song of Ice and Fire - Ramin Djawadi  | WaterTower", "WaterTower Music", 132, 11_610_363, query));

        Assert.Equal("WaterTower Music", ranked[0].Candidate.Channel);
        Assert.Contains("Main Title", ranked[0].Candidate.Title, StringComparison.Ordinal);

        var arrangement = ranked.Single(r => r.Candidate.Title.Contains("arr.", StringComparison.Ordinal));
        Assert.Contains(arrangement.Breakdown, s => s.Rule == "NegativeKeywords");
    }

    [Fact]
    public void DriveDoesNotAssignASongThatNeverSaysItIsTheTheme()
    {
        // After the first round of fixes: "Drive Soundtrack - Desire - Under Your Spell" at 73.0,
        // assigned -- a song that plays in the film, on nothing but its title, length and views.
        var query = Query("Drive 2011 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var identity = TestData.Movie("Drive", 2011, composers: new[] { "Cliff Martinez" });

        var ranked = Rank(
            identity,
            Real("Drive Soundtrack - Desire - Under Your Spell", "Eamon Nolan", 234, 833_968, query),
            Real("Electric Youth & College - A Real Hero (DRIVE)", "clarencito", 268, 11_935_104, query));
        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        Assert.True(ranked[0].Total >= TestData.Config().AutoAssignThreshold, $"scored {ranked[0].Total:F1}");
        Assert.True(ranked[0].IsReviewOnly);
        Assert.Equal(DecisionOutcome.Review, decision.Outcome);
    }

    [Fact]
    public void APieceByTheWorksOwnComposerIsStillAssigned()
    {
        // Inception is known by "Time", whose title says nothing about a theme. Its composer is
        // what vouches for it.
        var query = Query("Inception 2010 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var identity = TestData.Movie("Inception", 2010, composers: new[] { "Hans Zimmer" });

        var ranked = Rank(identity, Real("Hans Zimmer - Time (Inception)", "Hans Zimmer Fan", 275, 60_000_000, query));
        var decision = new DecisionPolicy().Decide(ranked, TestData.Config());

        Assert.False(ranked[0].IsReviewOnly);
        Assert.Equal(DecisionOutcome.AutoAssign, decision.Outcome);
    }

    // ---- the pieces, on their own ----------------------------------------------------

    [Theory]
    [InlineData("Misirlou", "Pulp Fiction", true)]
    [InlineData("Woke Up This Morning", "The Sopranos", true)]
    [InlineData("Game of Thrones Theme", "Game of Thrones", false)]
    [InlineData("The X-Files", "The X-Files", false)]
    [InlineData("Main Title", "Dexter", false)]
    [InlineData("Twin Peaks Theme", "Twin Peaks", false)]
    public void TellsASongsTitleFromADescriptionOfTheTheme(string theme, string work, bool isASong) =>
        Assert.Equal(isASong, KeywordAffinityRule.IsASongTitle(theme, work));

    [Theory]
    [InlineData("Drive Original Soundtrack - 8. He Had a Good Time", "Drive", 8)]
    [InlineData("Drive Soundtrack - 08 - He Had a Good Time", "Drive", 8)]
    [InlineData("Amelie Original Soundtrack - 1. J'y Suis Jamais Allé", "Amélie", null)]
    [InlineData("Saw 3. Hello Zepp", "Saw 3", null)]
    [InlineData("Drive 2011 - Main Theme", "Drive", null)]
    [InlineData("Beethoven - Symphony No. 7 (The Fall)", "The Fall", null)]
    [InlineData("Interstellar Main Theme - Hans Zimmer", "Interstellar", null)]
    public void RecognisesALaterTrackOfAnAlbum(string title, string work, int? expected) =>
        Assert.Equal(expected, KeywordAffinityRule.LaterTrack(title, work));

    [Fact]
    public void SoundtrackAloneEarnsLessThanCallingItselfTheTheme()
    {
        var context = TestData.Context(TestData.Movie("Gladiator", 2000));
        var rule = new KeywordAffinityRule();

        var fromTheSoundtrack = rule.Evaluate(TestData.Candidate("Gladiator Soundtrack - The Battle"), context);
        var aTheme = rule.Evaluate(TestData.Candidate("Gladiator Soundtrack - Victory Theme"), context);
        var theMainTheme = rule.Evaluate(TestData.Candidate("Gladiator Main Theme"), context);

        Assert.True(fromTheSoundtrack.Raw < aTheme.Raw);
        Assert.True(aTheme.Raw < theMainTheme.Raw);
        Assert.False(aTheme.MainTheme);
        Assert.True(theMainTheme.MainTheme);
    }

    // ---- the main theme ranks first ----------------------------------------------------

    [Fact]
    public void TheMainThemeRanksAboveAnOfficialSoundtrackTrackThatOutscoresIt()
    {
        // The soundtrack track has everything but being the theme: the composer's own upload,
        // official, millions of views, the right length. The main theme upload has fewer views
        // and was found by a later query. The main theme still comes first, and the track is held
        // a point below it.
        var first = Query("Interstellar 2014 main theme soundtrack", 1, "{title} {year} main theme soundtrack");
        var later = Query("Interstellar main title theme", 4, "{title} main title theme");
        var identity = TestData.Movie("Interstellar", 2014, composers: new[] { "Hans Zimmer" });

        var ranked = Rank(
            identity,
            Real("Hans Zimmer - Cornfield Chase (Official Soundtrack) | Interstellar", "Hans Zimmer", 126, 40_000_000, first),
            Real("Interstellar Main Theme", "Film Themes", 240, 90_000, later));

        Assert.Equal("Interstellar Main Theme", ranked[0].Candidate.Title);
        Assert.True(ranked[0].IsMainTheme);
        Assert.True(ranked[0].Total > ranked[1].Total, $"{ranked[0].Total:F1} against {ranked[1].Total:F1}");
        Assert.NotNull(ranked[1].HeldFrom);
        Assert.Contains(ranked[1].Breakdown, s => s.Rule == "MainTheme" && s.Reason.Contains("not the main theme", StringComparison.Ordinal));
    }

    [Fact]
    public void OfficialDoesNothingForATrackThatIsNotTheMainTheme()
    {
        var context = TestData.Context(TestData.Movie("Gladiator", 2000));
        var rule = new KeywordAffinityRule();

        var official = rule.Evaluate(TestData.Candidate("Gladiator Official Soundtrack - Victory Theme"), context);
        var plain = rule.Evaluate(TestData.Candidate("Gladiator Soundtrack - Victory Theme"), context);

        Assert.Equal(plain.Raw, official.Raw);
    }

    [Fact]
    public void ACoverIsNotTheMainThemeWhateverItCallsItself()
    {
        var identity = TestData.Movie("Interstellar", 2014, composers: new[] { "Hans Zimmer" });
        var query = Query("Interstellar 2014 main theme soundtrack", 1, "{title} {year} main theme soundtrack");

        var ranked = Rank(
            identity,
            Real("Interstellar Main Theme (Piano Cover)", "Some Pianist", 240, 3_000_000, query),
            Real("Hans Zimmer - Cornfield Chase (Official Soundtrack) | Interstellar", "Hans Zimmer", 126, 40_000_000, query));

        Assert.False(ranked.Single(r => r.Candidate.Title.Contains("Cover", StringComparison.Ordinal)).IsMainTheme);
        Assert.Equal("Hans Zimmer", ranked[0].Candidate.Channel);
        Assert.Null(ranked[0].HeldFrom);
    }

    [Fact]
    public void AMainThemeTooWeakToOfferHoldsNothingBack()
    {
        // Something calling itself the main theme that scores below the review threshold is not a
        // reason to push a good candidate out.
        var weak = new ScoreResult
        {
            Candidate = TestData.Candidate("Interstellar Main Theme"),
            Total = 30,
            Breakdown = new[] { new Signal("KeywordAffinity", 1, 18, "matched", MainTheme: true) },
        };
        var good = new ScoreResult
        {
            Candidate = TestData.Candidate("Hans Zimmer - Cornfield Chase"),
            Total = 85,
            Breakdown = new[] { new Signal("KeywordAffinity", 0, 18, "no theme words") },
        };

        var ordered = ScoringEngine.Order(new[] { weak, good }, TestData.Config());

        Assert.Same(good, ordered[0]);
        Assert.Equal(85, ordered[0].Total);
    }

    [Fact]
    public void OrderingAgainReleasesWhatNoLongerNeedsHolding()
    {
        // A search merges what several queries found. A track held below a main theme that a
        // later look disqualified gets its own score back.
        var mainTheme = new ScoreResult
        {
            Candidate = TestData.Candidate("Interstellar Main Theme", id: "main"),
            Total = 80,
            Breakdown = new[] { new Signal("KeywordAffinity", 1, 18, "matched", MainTheme: true) },
        };
        var track = new ScoreResult
        {
            Candidate = TestData.Candidate("Hans Zimmer - Cornfield Chase", id: "track"),
            Total = 85,
            Breakdown = new[] { new Signal("KeywordAffinity", 0, 18, "no theme words") },
        };

        var held = ScoringEngine.Order(new[] { mainTheme, track }, TestData.Config()).Single(r => r.Candidate.Id == "track");
        Assert.Equal(79, held.Total);

        var released = ScoringEngine.Order(new[] { held }, TestData.Config()).Single();
        Assert.Equal(85, released.Total);
        Assert.Null(released.HeldFrom);
        Assert.DoesNotContain(released.Breakdown, s => s.Rule == "MainTheme");
    }

    [Theory]
    [InlineData("Interstellar Main Theme - Hans Zimmer", "Interstellar", true)]
    [InlineData("Breaking Bad Intro", "Breaking Bad", true)]
    [InlineData("Severance - Official Intro Title Sequence", "Severance", true)]
    [InlineData("Back To The Future Theme by Alan Silvestri", "Back to the Future", true)]
    [InlineData("Gladiator - Theme", "Gladiator", true)]
    [InlineData("Game of Thrones (Theme) by Ramin Djawadi", "Game of Thrones", true)]
    [InlineData("John Williams - Theme from Jurassic Park", "Jurassic Park", true)]
    [InlineData("The X-Files Theme", "The X-Files", true)]
    [InlineData("Drive (2011) title sequence", "Drive", true)]
    [InlineData("Gladiator Soundtrack - Victory Theme", "Gladiator", false)]
    [InlineData("Love Theme from The Godfather", "The Godfather", false)]
    [InlineData("Thomas Newman - Shawshank Prison - Stoic Theme | The Shawshank Redemption", "The Shawshank Redemption", false)]
    [InlineData("Game of Thrones S8 Official Soundtrack | A Song of Ice and Fire", "Game of Thrones", false)]
    [InlineData("Hans Zimmer - Time (Inception)", "Inception", false)]
    [InlineData("The Opening Act - End Credits", "The Opening Act", false)]
    public void KnowsAMainThemeFromAnyOtherMusic(string upload, string work, bool isTheMainTheme)
    {
        var identity = TestData.Movie(work, 2000);
        Assert.Equal(isTheMainTheme, KeywordAffinityRule.ClaimsToBeTheMainTheme(TestData.Candidate(upload), identity));
    }


    [Fact]
    public void TheNamedThemeSongIsNeverMarkedDownForItsWording()
    {
        var identity = TestData.Series("The Sopranos", 1999, theme: new ThemeSong("Woke Up This Morning", "Alabama 3"));
        var verdict = new KeywordAffinityRule().Evaluate(
            TestData.Candidate("Alabama 3 - Woke Up This Morning", channel: "Alabama3akaA3"),
            TestData.Context(identity));

        Assert.Equal(1.0, verdict.Raw);
    }

    [Fact]
    public void ATieGoesToTheMoreWatchedUploadNotTheEarlierLetter()
    {
        var query = Query("Some Film main title theme", 0, "{title} main title theme");
        var identity = TestData.Movie("Some Film", 2010);

        // Both are past the view count at which popularity stops adding anything -- which is how
        // Interstellar's three candidates came to tie exactly.
        var ranked = Rank(
            identity,
            Real("Some Film Main Title Theme (A)", "A Channel", 120, 2_000_000, query),
            Real("Some Film Main Title Theme (B)", "B Channel", 120, 9_000_000, query));

        Assert.Equal(ranked[0].Total, ranked[1].Total, 3);
        Assert.Equal("B Channel", ranked[0].Candidate.Channel);
    }

    [Theory]
    [InlineData("The Office (UK) Opening Theme", "United States of America", "British")]
    [InlineData("The Office UK version intro", "United States of America", "British")]
    [InlineData("The Office (US) - Intro", "United Kingdom", "American")]
    [InlineData("The Office American Version Theme", "United Kingdom", "American")]
    [InlineData("Top Gear USA Intro", "United Kingdom", "American")]
    [InlineData("The Office (US) - Intro", "United States of America", null)]
    [InlineData("The Office Theme Song", "United States of America", null)]
    [InlineData("The Office (UK) Opening Theme", "", null)]
    public void ReadsTheCountryAnUploadSaysItIsFrom(string title, string country, string? contradicts)
    {
        var identity = Series(TitleBefore(title), 2005, country);
        var found = CountryEdition.Contradiction(title, identity);

        if (contradicts is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.NotNull(found);
            Assert.Contains(contradicts, found, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ACoProductionContradictsNeitherCountry()
    {
        var identity = Series("Being Human", 2008, "United Kingdom", "United States of America");

        Assert.Null(CountryEdition.Contradiction("Being Human (US) Opening", identity));
        Assert.Null(CountryEdition.Contradiction("Being Human (UK) Opening", identity));
    }

    [Fact]
    public void ACountryInTheWorksOwnTitleIsNotAMarker()
    {
        Assert.Null(CountryEdition.Contradiction(
            "American Horror Story Opening Theme",
            Series("American Horror Story", 2011, "United Kingdom")));

        // "Us" is a film, and "us" an ordinary word; only the capitals stand for the country.
        Assert.Null(CountryEdition.Contradiction("Us (2019) Main Theme", Movie("Us", 2019, "Canada")));
        Assert.Null(CountryEdition.Contradiction("Theme from Let us Prey", Movie("Let Us Prey", 2014, "United Kingdom")));
    }

    private static string TitleBefore(string uploadTitle) =>
        uploadTitle.StartsWith("Top Gear", StringComparison.Ordinal) ? "Top Gear" : "The Office";

    private static MediaIdentity Series(string title, int year, params string[] countries) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        Year = year,
        Kind = BaseItemKind.Series,
        Countries = countries.Where(c => c.Length > 0).ToArray(),
    };

    private static MediaIdentity Movie(string title, int year, params string[] countries) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        Year = year,
        Kind = BaseItemKind.Movie,
        Countries = countries,
    };
}
