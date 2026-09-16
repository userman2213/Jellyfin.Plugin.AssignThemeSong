using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers each mechanism the matcher is built from, one at a time.
/// </summary>
/// <remarks>
/// The end-to-end behaviour is in <c>TitleAnchorBenchmarkTests</c>. These pin the individual
/// rules, so that a benchmark failure points at which one moved.
/// </remarks>
public class TitleAnchorTests
{
    [Fact]
    public void AWholeWordIsRequired()
    {
        // The defect this replaced: a bare substring scored a perfect match.
        Assert.True(TitleAnchor.Match("Fire", "Firefly Theme").Rejected);
        Assert.False(TitleAnchor.Match("Fire", "Fire - Main Title").Rejected);
    }

    [Fact]
    public void AOneWordTitleMustMatchExactly()
    {
        // Alien and Aliens are different films; approximate matching is switched off entirely
        // when the whole title is one word, because there is nothing else to corroborate it.
        Assert.True(TitleAnchor.Match("Alien", "Aliens Main Theme").Rejected);
        Assert.False(TitleAnchor.Match("Alien", "Alien Main Theme").Rejected);
    }

    [Fact]
    public void ALongerWordMayDifferSlightly()
    {
        Assert.False(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactika Theme").Rejected);
    }

    [Fact]
    public void AShorterWordMayNot()
    {
        // castle/castlevania and dark/darkside both score above 0.90 on similarity alone.
        Assert.True(TitleAnchor.Match("Castle Rock", "Castlevania Rock Theme").Rejected);
    }

    [Fact]
    public void AnythingLeftInTheAnchorsOwnSegmentCountsAgainstIt()
    {
        var clean = TitleAnchor.Match("Girls", "Girls - Main Title");
        var sibling = TitleAnchor.Match("Girls", "Golden Girls - Main Title");

        Assert.Equal(1.0, clean.Residual);
        Assert.True(sibling.Rejected);
        Assert.True(sibling.Anchored, "the word is there; it is the leftover that disqualifies it");
    }

    [Fact]
    public void AColonDoesNotHideASibling()
    {
        // A colon joins a work to its own subtitle, so it must not start a new part -- otherwise
        // "Star Trek: Deep Space Nine" would look like "Star Trek" with nothing left over.
        Assert.True(TitleAnchor.Match("Star Trek", "Star Trek: Deep Space Nine - Theme").Rejected);
        Assert.False(TitleAnchor.Match("Star Trek: Deep Space Nine", "Star Trek: Deep Space Nine - Theme").Rejected);
    }

    [Fact]
    public void WordsAfterAThemeClaimDescribeTheRecording()
    {
        // "Kyle Dixon & Michael Stein" are the composers, not a different show.
        var result = TitleAnchor.Match("Stranger Things", "Stranger Things Theme - Kyle Dixon & Michael Stein");
        Assert.Equal(1.0, result.Residual);
    }

    [Fact]
    public void AClaimBeforeTheAnchorIsNotResidualEither()
    {
        Assert.False(TitleAnchor.Match("Firefly", "Main Theme from Firefly").Rejected);
    }

    [Fact]
    public void ANumberBesideTheNameUsuallyMeansADifferentEntry()
    {
        Assert.True(TitleAnchor.Match("Titanic", "Titanic II Theme Song").Rejected);
        Assert.True(TitleAnchor.Match("Halo", "Halo 3 Main Theme").Rejected);
    }

    [Fact]
    public void ALongTitleStillToleratesAStrayYear()
    {
        Assert.False(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica 2004 Theme").Rejected);
    }

    [Fact]
    public void TheArticleIsPartOfTheTitle()
    {
        var withArticle = TitleAnchor.Match("The Office", "The Office Theme Song");
        var without = TitleAnchor.Match("The Office", "Office Theme Song");

        Assert.Equal(1.0, withArticle.Score);
        Assert.True(without.Score < withArticle.Score, "dropping the article is weaker evidence");
        Assert.False(without.Rejected);
    }

    [Fact]
    public void APublishersBrandingCanBeAbsent()
    {
        Assert.False(TitleAnchor.Match("Marvel's Agents of S.H.I.E.L.D.", "Agents of SHIELD Main Theme").Rejected);
    }

    [Fact]
    public void AnInteriorArticleMayBeMissingFromEitherSide()
    {
        Assert.False(TitleAnchor.Match("Star Trek: The Next Generation", "Star Trek Next Generation Theme").Rejected);
        Assert.False(TitleAnchor.Match("Game of Thrones", "Game Of Thrones Main Title").Rejected);
    }

    [Theory]
    [InlineData("Lost")]
    [InlineData("House")]
    [InlineData("Friends")]
    [InlineData("The Office")]
    public void AnOrdinaryTitleAsksForCorroboration(string title) =>
        Assert.True(TitleAnchor.Informativeness(title) <= TitleAnchor.LowInformationCeiling);

    [Theory]
    [InlineData("Battlestar Galactica")]
    [InlineData("Peaky Blinders")]
    [InlineData("Chernobyl")]
    [InlineData("Firefly")]
    public void ADistinctiveTitleDoesNot(string title) =>
        Assert.True(TitleAnchor.Informativeness(title) > TitleAnchor.LowInformationCeiling);

    [Fact]
    public void AThemeClaimIsRecognised()
    {
        Assert.True(TitleAnchor.ClaimsToBeATheme("Lost - Main Title"));
        Assert.True(TitleAnchor.ClaimsToBeATheme("Cowboy Bebop OP"));
        Assert.False(TitleAnchor.ClaimsToBeATheme("Marshmello - FRIENDS"));
    }

    [Theory]
    [InlineData("Lost", "Lost Season 1 Intro")]
    [InlineData("The Office", "The Office Season 3 Intro")]
    [InlineData("Friends", "Friends S01E01 Opening")]
    [InlineData("Lost", "Lost Staffel 2 Vorspann")]
    [InlineData("Lost", "Lost 1x03 Intro")]
    public void ASeasonOrEpisodeNumberIsAQualifier(string wanted, string candidate)
    {
        // A first version doubled every number as a sibling marker and rejected all of these.
        var result = TitleAnchor.Match(wanted, candidate);
        Assert.False(result.Rejected, result.Reason);
        Assert.Equal(1.0, result.Residual);
    }

    [Fact]
    public void ABareNumberBesideAShortTitleIsStillASibling()
    {
        Assert.True(TitleAnchor.Match("Titanic", "Titanic 2 Theme Song").Rejected);
        Assert.True(TitleAnchor.Match("Halo", "Halo 3 Main Theme").Rejected);
    }

    [Fact]
    public void TheItemsOwnYearCorroboratesTheMatch()
    {
        var result = TitleAnchor.Match("Lost", "Lost 2004 Intro", 2004);

        Assert.False(result.Rejected, result.Reason);
        Assert.True(result.YearConfirmed);
        Assert.Equal(1.0, result.Residual);
    }

    [Fact]
    public void ADifferentYearIsADifferentProductionEvenInBrackets()
    {
        // Brackets are otherwise somebody else's business, but "(1978)" beside a title is exactly
        // how uploads tell two productions of the same name apart.
        Assert.True(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica (1978) Theme", 2004).Rejected);
        Assert.True(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica 1978 Intro (4K)", 2004).Rejected);
        Assert.False(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica (1978) Theme", 1978).Rejected);
    }

    [Fact]
    public void AYearBesideARecordingWordIsWhenItWasRecorded() =>
        Assert.False(TitleAnchor.Match("Firefly", "Firefly Theme (2019 Remaster)", 2002).Rejected);

    [Fact]
    public void ARangeStartingAtTheRightYearIsTheRightWork()
    {
        var result = TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica 2004-2009 Opening", 2004);
        Assert.False(result.Rejected, result.Reason);
        Assert.True(result.YearConfirmed);
    }

    [Fact]
    public void AYearThatIsTheTitleIsNotADate()
    {
        Assert.False(TitleAnchor.Match("1883", "1883 Main Theme", 2021).Rejected);
        Assert.True(TitleAnchor.Match("2012", "2012 (2009) Main Theme", 2009).YearConfirmed);
    }

    [Fact]
    public void WithNoYearKnownAStatedYearIsNeutral()
    {
        var result = TitleAnchor.Match("Lost", "Lost 1998 Intro");
        Assert.False(result.Rejected, result.Reason);
        Assert.False(result.YearConfirmed);
    }

    [Fact]
    public void TwoWorksSetAgainstEachOtherAreNeither() =>
        Assert.True(TitleAnchor.Match("Battlestar Galactica", "Battlestar Galactica 1978 vs 2004 comparison", 2004).Rejected);

    [Fact]
    public void ASubtitleOfTwoWordsStandsForTheWhole()
    {
        var result = TitleAnchor.Match("Star Wars: The Clone Wars", "The Clone Wars Main Theme");

        Assert.False(result.Rejected, result.Reason);
        Assert.True(result.Score < 1.0, "a match on the subtitle alone is weaker evidence");
        Assert.True(result.RequiresCorroboration, "and needs something else to agree");
    }

    [Fact]
    public void AOneWordSubtitleDoesNot() =>
        Assert.True(TitleAnchor.Match("Star Trek: Voyager", "Voyager Theme").Rejected);

    [Fact]
    public void AClaimInAnotherLanguageEndsTheRegionToo() =>
        Assert.Equal(1.0, TitleAnchor.Match("Dark", "Dark Vorspann Staffel 1").Residual);

    [Theory]
    [InlineData("Bear McCreary", "Battlestar Galactica Main Title - Bear McCreary", true)]
    [InlineData("Bear McCreary", "Bear McCreary - Topic", true)]
    [InlineData("Vangelis", "Blade Runner (Vangelis)", true)]
    [InlineData("Hans Zimmer", "Hans Zimmermann Piano Tutorial", false)]
    [InlineData("Hans Zimmer", "Interstellar Main Theme", false)]
    [InlineData("", "Anything", false)]
    public void APersonIsNamedAsWholeWordsAnywhere(string name, string text, bool expected) =>
        Assert.Equal(expected, TitleAnchor.Names(name, text));

    [Fact]
    public void AnEmptyTitleIsRejectedRatherThanMatchingEverything()
    {
        Assert.True(TitleAnchor.Match(null, "Anything At All").Rejected);
        Assert.True(TitleAnchor.Match("Firefly", null).Rejected);
        Assert.True(TitleAnchor.Match("   ", "Firefly Theme").Rejected);
    }
}
