using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers finding out what a theme is called, from Wikidata and from Wikipedia.
/// </summary>
/// <remarks>
/// Both fixtures are what the real services returned. The Wikipedia one keeps each article only up
/// to the end of its infobox and a little beyond, so the parser still has to find the infobox in
/// real surrounding text; the articles themselves run to 330 KB.
/// </remarks>
public class ThemeResearchTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static IReadOnlyDictionary<string, ResearchedCredits> Wikidata() =>
        WikidataCreditsSource.Parse(Fixture("wikidata-themes.json"));

    private static string Article(string title)
    {
        using var document = JsonDocument.Parse(Fixture("wikipedia-infoboxes.json"));
        return document.RootElement.GetProperty("query").GetProperty("pages").EnumerateArray()
            .Single(page => page.GetProperty("title").GetString() == title)
            .GetProperty("revisions")[0].GetProperty("slots").GetProperty("main").GetProperty("content").GetString()!;
    }

    // ---- Wikidata ----

    [Fact]
    public void WikidataNamesTheThemeAndWhoPerformsIt()
    {
        var friends = Wikidata()["imdb:tt0108778"].Theme;

        Assert.Equal(new ThemeSong("I'll Be There for You", "The Rembrandts"), friends);
    }

    [Fact]
    public void AThemeIsFiledUnderTheShowsTmdbIdToo()
    {
        Assert.Equal("I'll Be There for You", Wikidata()["tmdbtv:1668"].Theme?.Title);
    }

    [Fact]
    public void APerformerIsDroppedWhenSeveralAreNamed()
    {
        // P175 on a song lists every recording of it. Titanic's theme names Céline Dion and three
        // cover acts, and searching for one of the covers would find the cover.
        var titanic = Wikidata()["imdb:tt0120338"].Theme;

        Assert.Equal("My Heart Will Go On", titanic?.Title);
        Assert.Null(titanic?.Performer);
    }

    [Fact]
    public void AnUnnamedEntityIsNotTakenForAName()
    {
        // The Simpsons' theme has no English label, so the label service returns its bare id,
        // Q115522708. Kept, it would be searched for.
        var simpsons = Wikidata()["imdb:tt0096697"];

        Assert.Null(simpsons.Theme);
        Assert.DoesNotContain(simpsons.Composers, name => name.StartsWith('Q') && name.Skip(1).All(char.IsDigit));
    }

    [Fact]
    public void ASeasonSharingTheShowsIdIsNotTheShow()
    {
        // tt0106179 is recorded on The X-Files and on its tenth season. Only the show is kept, so
        // the article is the show's.
        Assert.Equal("The X-Files", Wikidata()["imdb:tt0106179"].WikipediaTitle);
    }

    [Fact]
    public void TheArticleComesBackAsATitleTheApiAccepts()
    {
        Assert.Equal("Battlestar Galactica (2004 TV series)", Wikidata()["imdb:tt0407362"].WikipediaTitle);
    }

    [Fact]
    public void AShowWikidataKnowsNothingMusicalAboutStillLeadsToItsArticle()
    {
        // The Sopranos has no composer and no theme on Wikidata. Its article is how the theme is
        // found, so the record is kept for the lead alone.
        var sopranos = Wikidata()["imdb:tt0141842"];

        Assert.False(sopranos.Any);
        Assert.Null(sopranos.Theme);
        Assert.Equal("The Sopranos", sopranos.WikipediaTitle);
    }

    // ---- Wikipedia: every form the theme is written in ----

    [Theory]
    [InlineData("The Sopranos", "Woke Up This Morning", "Alabama 3")]                    // "Song (Mix)" by [[Artist]]
    [InlineData("Firefly (TV series)", "The Ballad of Serenity", "Sonny Rhodes")]         // performed by
    [InlineData("House (TV series)", "Teardrop", "Massive Attack")]                     // {{theme song|…|…}} plus a footnote
    [InlineData("Mad Men", "A Beautiful Mine", "RJD2")]                                 // (instrumental)<br />by
    [InlineData("Friends", "I'll Be There for You", "The Rembrandts")]                  // {{Based on|…|…}}
    [InlineData("True Detective", "Far from Any Road", "the Handsome Family")]          // a plainlist, one per season
    [InlineData("Scrubs (2001 TV series)", "Superman", "Lazlo Bane")]                   // a plainlist, the first is the original
    public void TheThemeIsReadInEveryFormEditorsWriteIt(string article, string title, string performer)
    {
        Assert.Equal(new ThemeSong(title, performer), WikipediaInfobox.Theme(Article(article)));
    }

    [Theory]
    [InlineData("The Simpsons", "The Simpsons Theme")]   // italics inside a link
    [InlineData("Game of Thrones", "Main Title")]        // the text shown, not the article linked
    public void ATitleWithNoPerformerIsKeptWithoutOne(string article, string title)
    {
        Assert.Equal(new ThemeSong(title, null), WikipediaInfobox.Theme(Article(article)));
    }

    [Theory]
    [InlineData("Dexter (TV series)", new[] { "Rolfe Kent" })]
    [InlineData("Firefly (TV series)", new[] { "Joss Whedon" })]
    [InlineData("Friends", new[] { "Michael Skloff" })]                                  // a one-item plainlist
    [InlineData("Scrubs (2001 TV series)", new[] { "Chad Fischer", "Chris Link", "Tim Bright" })]
    [InlineData("Star Trek: The Next Generation", new[] { "Alexander Courage", "Jerry Goldsmith" })] // an unbulleted list with a styling argument
    public void WhoWroteTheThemeIsRead(string article, string[] names)
    {
        Assert.Equal(names, WikipediaInfobox.ThemeComposers(Article(article)));
    }

    [Fact]
    public void ThemesComposerIsNotTheShowsComposer()
    {
        // The reason the theme's composer is worth reading at all: Dexter was scored by Daniel
        // Licht, and its theme is Rolfe Kent's.
        Assert.Equal(new[] { "Daniel Licht" }, Wikidata()["imdb:tt0773262"].Composers);
        Assert.Equal(new[] { "Rolfe Kent" }, WikipediaInfobox.ThemeComposers(Article("Dexter (TV series)")));
    }

    [Fact]
    public void AnEmptyFieldAndAFieldThatIsOnlyACommentYieldNothing()
    {
        // Breaking Bad's open_theme is empty and its theme_music_composer holds only an editor's
        // note. A line-by-line reading took the next field's text as the empty one's value.
        var source = Article("Breaking Bad");

        Assert.Null(WikipediaInfobox.Theme(source));
        Assert.Empty(WikipediaInfobox.ThemeComposers(source));
    }

    [Fact]
    public void AnArticleWithoutATelevisionInfoboxYieldsNothing()
    {
        Assert.Empty(WikipediaInfobox.Read("{{Infobox film\n| name = Alien\n| music = [[Jerry Goldsmith]]\n}}\n'''Alien''' is a film."));
        Assert.Null(WikipediaInfobox.Theme("Just prose."));
    }

    [Theory]
    [InlineData("Instrumental")]
    [InlineData("Main theme by the composer")]
    [InlineData("")]
    public void AnUnquotedDescriptionIsNotATitle(string value)
    {
        // Searching for "Instrumental" would find every instrumental ever uploaded.
        Assert.Null(WikipediaInfobox.ReadTheme(value));
    }

    [Theory]
    [InlineData("\"Dog Days Are Over\" by [[Florence and the Machine]]", "Florence and the Machine")]
    [InlineData("\"Maneater\" by [[Hall & Oates]]", "Hall & Oates")]
    [InlineData("\"Song\" by Someone featuring Someone Else", "Someone")]
    public void ABandNameIsNotCutInHalf(string value, string performer)
    {
        Assert.Equal(performer, WikipediaInfobox.ReadTheme(value)?.Performer);
    }

    // ---- Wikipedia: the response around the articles ----

    [Fact]
    public void EachArticleIsFiledUnderTheTitleItWasAskedFor()
    {
        var articles = WikipediaThemeSource.ParseArticles(
            Fixture("wikipedia-infoboxes.json"),
            new[] { "The Sopranos", "Mad Men", "Not An Article" });

        Assert.Equal(new[] { "Mad Men", "The Sopranos" }, articles.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ARedirectedOrNormalisedTitleIsTracedBack()
    {
        // The API answers under the title it ended up at; the answer belongs to the one asked for.
        const string Json = """
            {"batchcomplete":true,"query":{
              "normalized":[{"fromencoded":false,"from":"mad men","to":"Mad men"}],
              "redirects":[{"from":"Mad men","to":"Mad Men"}],
              "pages":[{"pageid":1,"ns":0,"title":"Mad Men","revisions":[{"slots":{"main":{"content":"source"}}}]}]}}
            """;

        Assert.Equal("source", WikipediaThemeSource.ParseArticles(Json, new[] { "mad men" })["mad men"]);
    }

    [Fact]
    public void AMissingArticleIsAbsent()
    {
        const string Json = """{"batchcomplete":true,"query":{"pages":[{"ns":0,"title":"Nothing Here","missing":true}]}}""";

        Assert.Empty(WikipediaThemeSource.ParseArticles(Json, new[] { "Nothing Here" }));
    }
}
