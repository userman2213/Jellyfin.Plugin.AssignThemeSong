using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers reading composers out of Wikidata and MusicBrainz.
/// </summary>
/// <remarks>
/// Both fixtures are answers those services really gave, captured before the parsers were written.
/// That is the point of them: the first guess at the MusicBrainz shape was wrong — the key is
/// spelled <c>release_group</c>, not <c>release-group</c> — and no invented fixture would have
/// caught it, because it would have been invented wrong in the same way.
/// </remarks>
public class CreditsResearchTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static IReadOnlyDictionary<string, ResearchedCredits> Wikidata() =>
        WikidataCreditsSource.Parse(Fixture("wikidata-composers.json"));

    // ---- Wikidata ----

    [Fact]
    public void OneRequestAnswersForEveryTitleInIt()
    {
        var found = Wikidata();

        Assert.Equal(new[] { "Jerry Goldsmith" }, found["imdb:tt0078748"].Composers);
        Assert.Equal(new[] { "Greg Edmonson" }, found["imdb:tt0303461"].Composers);
        Assert.Equal(new[] { "Bear McCreary" }, found["imdb:tt0407362"].Composers);
    }

    [Fact]
    public void AnAnswerIsFiledUnderEveryIdTheRowCarries()
    {
        // The same work comes back once per id that matched it, and a row may carry two. Filing it
        // only under the first would leave an item that has a TMDB id but no IMDb id unanswered
        // although the answer is right there.
        var found = Wikidata();

        Assert.Equal(new[] { "Jerry Goldsmith" }, found["tmdb:348"].Composers);
        Assert.Equal(new[] { "Bear McCreary" }, found["tmdbtv:1972"].Composers);
    }

    [Fact]
    public void FilmsAndShowsAreKeptApart()
    {
        // TMDB numbers films and shows in separate sequences, so film 1972 and show 1972 are
        // different works. Blade Runner is film 348's neighbour in one of them and nothing in the
        // other, and a key that did not say which would confuse them.
        var found = Wikidata();

        Assert.True(found.ContainsKey("tmdbtv:1972"));
        Assert.False(found.ContainsKey("tmdb:1972"));
    }

    [Fact]
    public void BothComposersOfAFilmAreKept()
    {
        // Blade Runner 2049 arrives as four rows: two composers crossed with two performers.
        var found = Wikidata()["imdb:tt1856101"];

        Assert.Equal(2, found.Composers.Count);
        Assert.Contains("Hans Zimmer", found.Composers, StringComparer.Ordinal);
        Assert.Contains("Benjamin Wallfisch", found.Composers, StringComparer.Ordinal);
    }

    [Fact]
    public void ARepeatedRowDoesNotRepeatAName()
    {
        // Jerry Goldsmith arrives on four rows for Alien and must be named once.
        Assert.Single(Wikidata()["imdb:tt0078748"].Composers);
    }

    [Fact]
    public void ThePerformerIsOnlyKeptWhenNobodyIsCreditedWithWritingIt()
    {
        // Every work in the fixture has a composer, and on a score the performer is usually the
        // same person. Naming them twice would say nothing and cost a query rung.
        Assert.All(Wikidata().Values, credits => Assert.Null(credits.Artist));
    }

    [Fact]
    public void TheReleaseGroupIsKeptSoTheNextSourceCanSkipAStep()
    {
        Assert.Equal("e15b15c1-97e6-3f22-a21f-ec7c2498f604", Wikidata()["imdb:tt0078748"].ReleaseGroupId);
    }

    [Fact]
    public void ATitleNobodyHasHeardOfIsAbsentRatherThanEmpty()
    {
        // tt0000000 was asked about and is not in the answer at all. Absent is what tells the
        // catalogue to try the next source; an empty record would end the search.
        Assert.False(Wikidata().ContainsKey("imdb:tt0000000"));
    }

    [Fact]
    public void NothingComesOfAnEmptyAnswer()
    {
        Assert.Empty(WikidataCreditsSource.Parse("""{"head":{"vars":[]},"results":{"bindings":[]}}"""));
    }

    // ---- The query ----

    [Fact]
    public void TheQueryAsksAboutEveryKindOfIdThatIsPresent()
    {
        var query = WikidataCreditsSource.BuildQuery(new[]
        {
            new CreditsRequest("imdb:tt0078748", new[] { "imdb:tt0078748" }, "tt0078748", "348", false, "Alien"),
            new CreditsRequest("tmdbtv:1972", new[] { "tmdbtv:1972" }, null, "1972", true, "Battlestar Galactica"),
        });

        Assert.NotNull(query);
        Assert.Contains("wdt:P345", query, StringComparison.Ordinal);
        Assert.Contains("\"tt0078748\"", query, StringComparison.Ordinal);
        Assert.Contains("wdt:P4947", query, StringComparison.Ordinal);
        Assert.Contains("wdt:P4983", query, StringComparison.Ordinal);

        // A film's id and a show's id go to different properties, so they must not be pooled.
        Assert.Contains("?tmdbFilm. VALUES ?tmdbFilm { \"348\" }", query, StringComparison.Ordinal);
        Assert.Contains("?tmdbTv. VALUES ?tmdbTv { \"1972\" }", query, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdThatIsNotAnIdNeverReachesTheQuery()
    {
        // Ids are pasted into a query. One carrying a quote would end the VALUES clause and start
        // something else, so anything that is not plainly an id is dropped rather than escaped.
        var query = WikidataCreditsSource.BuildQuery(new[]
        {
            new CreditsRequest("imdb:x", new[] { "imdb:x" }, "tt00\" } UNION { ?item wdt:P31 ?x", null, false, "Bad"),
            new CreditsRequest("imdb:tt0303461", new[] { "imdb:tt0303461" }, "tt0303461", null, true, "Firefly"),
        });

        Assert.NotNull(query);
        Assert.DoesNotContain("wdt:P31", query, StringComparison.Ordinal);
        Assert.Contains("\"tt0303461\"", query, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsAskedAboutTitlesWithNoIds()
    {
        Assert.Null(WikidataCreditsSource.BuildQuery(new[]
        {
            new CreditsRequest("name:x", new[] { "name:x" }, null, null, false, "Home video"),
        }));
    }

    // ---- MusicBrainz ----

    [Fact]
    public void TheImdbUrlLookupNamesTheComposer()
    {
        // This is the lookup that rescues the titles Wikidata has no composer for.
        var found = MusicBrainzCreditsSource.Parse(Fixture("musicbrainz-imdb-url.json"));

        Assert.Equal(new[] { "Bear McCreary" }, found.Composers);
    }

    [Fact]
    public void AGuestOnOneReleaseIsNotTakenForTheComposer()
    {
        // Battlestar Galactica's IMDb page links four releases: three season soundtracks credited
        // to Bear McCreary and a solo piano album credited to him and its pianist. Taking every
        // credit would search for a theme under the pianist's name.
        var found = MusicBrainzCreditsSource.Parse(Fixture("musicbrainz-imdb-url.json"));

        Assert.DoesNotContain("Joohyun Park", found.Composers, StringComparer.Ordinal);
    }

    [Fact]
    public void TheFirstLinkedReleaseGroupIsRemembered()
    {
        var found = MusicBrainzCreditsSource.Parse(Fixture("musicbrainz-imdb-url.json"));

        Assert.Equal("8e6e4480-c9a3-39e0-847f-649478165753", found.ReleaseGroupId);
    }

    [Fact]
    public void ASingleReleaseGroupReadsTheSameWayUnwrapped()
    {
        // Asking about a release group directly returns one without the relation wrapper.
        var found = MusicBrainzCreditsSource.Parse("""
            {"id":"8e6e4480-c9a3-39e0-847f-649478165753","title":"Season 2",
             "artist-credit":[{"name":"Bear McCreary","joinphrase":"",
               "artist":{"name":"Bear McCreary","id":"78192f3c-607b-4190-a02b-8e036887b9a7"}}]}
            """);

        Assert.Equal(new[] { "Bear McCreary" }, found.Composers);
        Assert.Equal("8e6e4480-c9a3-39e0-847f-649478165753", found.ReleaseGroupId);
    }

    [Fact]
    public void CoComposersOnEveryReleaseAreBothKept()
    {
        var found = MusicBrainzCreditsSource.Parse("""
            {"resource":"https://www.imdb.com/title/tt1856101/","id":"x","relations":[
              {"target-type":"release_group","release_group":{"id":"a","artist-credit":[
                {"name":"Hans Zimmer"},{"name":"Benjamin Wallfisch"}]}},
              {"target-type":"release_group","release_group":{"id":"b","artist-credit":[
                {"name":"Hans Zimmer"},{"name":"Benjamin Wallfisch"}]}}]}
            """);

        Assert.Equal(new[] { "Hans Zimmer", "Benjamin Wallfisch" }, found.Composers);
    }

    [Fact]
    public void ACompilationNamesNobody()
    {
        // "Various Artists" is not a person, and searching for it would find every compilation
        // ever made rather than this title's theme.
        var found = MusicBrainzCreditsSource.Parse("""
            {"id":"c","artist-credit":[{"name":"Various Artists",
              "artist":{"name":"Various Artists","id":"89ad4ac3-39f7-470e-963a-56509c546377"}}]}
            """);

        Assert.Empty(found.Composers);
        Assert.False(found.Any);
    }

    [Fact]
    public void AnUnlinkedTitleYieldsNothing()
    {
        var found = MusicBrainzCreditsSource.Parse("""{"resource":"https://www.imdb.com/title/tt0000000/","id":"d","relations":[]}""");

        Assert.False(found.Any);
    }

    [Fact]
    public void TheArtistNameIsReadWhenTheCreditHasNoneOfItsOwn()
    {
        var found = MusicBrainzCreditsSource.Parse("""
            {"id":"e","artist-credit":[{"joinphrase":"","artist":{"name":"Ramin Djawadi","id":"f"}}]}
            """);

        Assert.Equal(new[] { "Ramin Djawadi" }, found.Composers);
    }
}
