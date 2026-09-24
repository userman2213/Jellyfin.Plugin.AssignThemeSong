using System;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins how IMDb's soundtrack listing is read, and which entry of it is taken as the theme.
/// </summary>
/// <remarks>
/// The fixtures are real listings, trimmed to their soundtrack rows: two films, two series, the
/// older markup, and the bot challenge IMDb serves instead of the page. The films are the reason
/// the choosing rule exists, so they are pinned rather than sampled.
/// </remarks>
public class ImdbSoundtrackTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ReadsEveryEntryOffARealListing()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-decoys.html"));

        Assert.Equal(14, entries.Count);
        Assert.Equal("Coffee Shop Zak", entries[0].Title);
        Assert.Equal("Rolfe Kent", entries[0].Writer);
        Assert.Equal("Where Is My Mind", entries[^1].Title);
    }

    [Fact]
    public void KeepsThePerformerAndWriterApart()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-decoys.html"));
        var svarga = entries.Single(entry => entry.Title == "Svarga");

        Assert.Equal("Vas", svarga.Performer);
        Assert.Contains("Azam Ali", svarga.Writer, StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesNoMarkupOrEntitiesInATitle()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-decoys.html"));

        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain("<", entry.Title, StringComparison.Ordinal);
            Assert.DoesNotContain(">", entry.Title, StringComparison.Ordinal);
            Assert.DoesNotMatch("&(?:#[0-9]+|[a-zA-Z]+);", entry.Title);
        });

        // An ampersand in a title is the title's own, and must survive.
        Assert.Contains(entries, entry => entry.Title == "Splended & 4M15");
    }

    [Fact]
    public void KeepsTheCreditWhenOnePersonBothWroteAndPerformedIt()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-decoys.html"));
        var credited = entries.Single(entry => entry.Title == "Girl From Ypsilanti");

        // Credited "Written and Performed by Daniel May" on one line, which names him for both.
        // Matching "Performed by" first would take the performer and lose the writer.
        Assert.Equal("Daniel May", credited.Performer);
        Assert.Equal("Daniel May", credited.Writer);
    }

    [Fact]
    public void KeepsSeparatePerformerAndWriterCreditsSeparate()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-series-titled.html"));
        var ballad = entries.Single();

        // Firefly's theme is performed by one person and written by another.
        Assert.Equal("The Ballad of Serenity", ballad.Title);
        Assert.Equal("Sonny Rhodes", ballad.Performer);
        Assert.Equal("Joss Whedon", ballad.Writer);
    }

    [Fact]
    public void ReadsTheOlderServerRenderedMarkupToo()
    {
        var entries = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-legacy.html"));

        Assert.Equal(2, entries.Count);

        // The title is in a div of its own, with the credits after it. Splitting on <br> alone
        // glues "Written by ..." onto the title, which is the bug this pins.
        Assert.Equal("Stuck in the Middle with You", entries[0].Title);
        Assert.Equal("Stealers Wheel", entries[0].Performer);
        Assert.Equal("Little Green Bag", entries[1].Title);
    }

    [Fact]
    public void FindsNothingInAPageThatIsNotAListing()
    {
        Assert.Empty(ImdbSoundtrackPage.Parse(Fixture("imdb-challenge.html")));
        Assert.Empty(ImdbSoundtrackPage.Parse("<html><body>nothing here</body></html>"));
        Assert.Empty(ImdbSoundtrackPage.Parse(null));
    }

    [Fact]
    public void TakesASeriesFirstEntryAsItsTheme()
    {
        var sopranos = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-series.html"));
        var theme = ImdbSoundtrackPage.ChooseTheme(sopranos, isSeries: true, "The Sopranos");

        Assert.NotNull(theme);
        Assert.Equal("Woke Up This Morning", theme!.Title);
    }

    [Fact]
    public void TakesNothingFromAFilmWhoseListingNamesNoTheme()
    {
        // The Godfather's listing opens with "Mall Wedding Sequence" and names no theme anywhere.
        // Taking its first entry would assign a wedding cue as the theme.
        var godfather = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-plain.html"));
        Assert.NotEmpty(godfather);

        Assert.Null(ImdbSoundtrackPage.ChooseTheme(godfather, isSeries: false, "The Godfather"));
    }

    [Fact]
    public void IsNotFooledByASongWithThemeInItsName()
    {
        // Fight Club's listing contains "Theme from Valley of the Dolls" and "KDFW News Theme".
        // Both are songs heard in the film. Neither is the film's theme, and a rule that matched
        // the bare word "theme" would take one of them.
        var fightClub = ImdbSoundtrackPage.Parse(Fixture("imdb-soundtrack-film-decoys.html"));

        Assert.Contains(fightClub, entry => entry.Title.Contains("Theme", StringComparison.Ordinal));
        Assert.Null(ImdbSoundtrackPage.ChooseTheme(fightClub, isSeries: false, "Fight Club"));
    }

    [Theory]
    [InlineData("Main Title", true)]
    [InlineData("Main Titles", true)]
    [InlineData("Opening Theme", true)]
    [InlineData("Love Theme from The Godfather", true)]
    [InlineData("End Credits", true)]
    [InlineData("Overture", true)]
    [InlineData("Theme from Valley of the Dolls", false)]
    [InlineData("KDFW News Theme", false)]
    [InlineData("Where Is My Mind", false)]
    [InlineData("", false)]
    public void RecognisesTitleMusicByName(string title, bool expected) =>
        Assert.Equal(expected, ImdbSoundtrackPage.NamesATheme(title));

    [Fact]
    public void TakesAnEntryNamedAfterTheWorkEvenForAFilm()
    {
        var entries = new[]
        {
            new SoundtrackEntry("Something Else", "A Band", null),
            new SoundtrackEntry("The Ballad of Serenity", "Sonny Rhodes", null),
        };

        var theme = ImdbSoundtrackPage.ChooseTheme(entries, isSeries: false, "Serenity");

        Assert.NotNull(theme);
        Assert.Equal("The Ballad of Serenity", theme!.Title);
    }

    [Fact]
    public void WillNotMatchAWorkWhoseTitleIsTooShortToMeanAnything()
    {
        var entries = new[] { new SoundtrackEntry("Put It Up", null, null) };

        // "Up" appears inside half the songs ever written.
        Assert.Null(ImdbSoundtrackPage.ChooseTheme(entries, isSeries: false, "Up"));
    }

    [Fact]
    public void ChoosesNothingFromAnEmptyListing() =>
        Assert.Null(ImdbSoundtrackPage.ChooseTheme(Array.Empty<SoundtrackEntry>(), true, "Whatever"));

    [Theory]
    [InlineData("imdb-challenge.html", true)]
    [InlineData("imdb-soundtrack-series.html", false)]
    public void TellsAChallengeApartFromAPage(string fixture, bool expected) =>
        Assert.Equal(expected, ImdbSoundtrackSource.IsChallenge(Fixture(fixture)));

    [Fact]
    public void DoesNotMistakeARealListingForAChallengeBecauseItMentionsOne()
    {
        // A megabyte-long page that happens to contain the word is the page, not a challenge:
        // the real listings are served alongside the challenge script's own domain.
        var page = "<html>" + new string('x', 25000) + "awswaf</html>";

        Assert.False(ImdbSoundtrackSource.IsChallenge(page));
    }

    [Fact]
    public void RecognisesARenderedListingByItsMarkup()
    {
        Assert.True(ImdbSoundtrackSource.LooksLikeTheListing(Fixture("imdb-soundtrack-series.html")));
        Assert.True(ImdbSoundtrackSource.LooksLikeTheListing(Fixture("imdb-soundtrack-legacy.html")));
        Assert.False(ImdbSoundtrackSource.LooksLikeTheListing(Fixture("imdb-challenge.html")));
    }

    [Fact]
    public void IsOffWhenTheResearchItBelongsToIsOff()
    {
        var source = new ImdbSoundtrackSource(
            new ThrowingHttpClientFactory(),
            new UnavailableBrowser(),
            new NullThemeForgeLogger<ImdbSoundtrackSource>());

        Assert.False(source.IsEnabled(new PluginConfiguration
        {
            ResearchComposers = false,
            UseImdbSoundtrack = true,
        }));

        Assert.False(source.IsEnabled(new PluginConfiguration
        {
            ResearchComposers = true,
            UseImdbSoundtrack = false,
        }));

        Assert.True(source.IsEnabled(new PluginConfiguration
        {
            ResearchComposers = true,
            UseImdbSoundtrack = true,
        }));
    }

    [Fact]
    public void IsAskedLastAndAnswersOnlyAboutTheTheme()
    {
        var source = new ImdbSoundtrackSource(
            new ThrowingHttpClientFactory(),
            new UnavailableBrowser(),
            new NullThemeForgeLogger<ImdbSoundtrackSource>());

        Assert.Equal(CreditsQuestion.Theme, source.Answers);
        Assert.True(source.Order > 20, "IMDb must be asked after Wikidata and Wikipedia.");
    }
}
