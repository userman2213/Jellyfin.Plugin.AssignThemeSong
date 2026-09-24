using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers reading composers and themes out of Wikidata.
/// </summary>
/// <remarks>
/// Both fixtures are answers those services really gave, captured before the parsers were written.
/// That is the point of them: the first guess at a response's shape was wrong — the key is
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

        // The query uses P31 itself, to leave out seasons, so what must be absent is the injected
        // fragment rather than the property.
        Assert.NotNull(query);
        Assert.DoesNotContain("?item wdt:P31 ?x", query, StringComparison.Ordinal);
        Assert.DoesNotContain("tt00\"", query, StringComparison.Ordinal);
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

}

/// <summary>
/// Covers the cache itself: what it remembers, for how long, and under which ids.
/// </summary>
public class ComposerSnapshotTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ComposerSnapshot WithBattlestar()
    {
        var snapshot = new ComposerSnapshot();
        snapshot.RecordComposers(
            new[] { "imdb:tt0407362", "tmdbtv:1972" },
            new ResearchedCredits(new[] { "Bear McCreary" }, null, null),
            "Wikidata",
            Now);
        return snapshot;
    }

    [Fact]
    public void AnAnswerIsFoundUnderEveryIdTheWorkHad()
    {
        // The point of storing it twice: an item that gains a TMDB id next week, or loses its
        // IMDb id to a metadata refresh, still finds the answer that was already paid for.
        var snapshot = WithBattlestar();

        Assert.NotNull(snapshot.Find(new[] { "imdb:tt0407362" }));
        Assert.NotNull(snapshot.Find(new[] { "tmdbtv:1972" }));
        Assert.Null(snapshot.Find(new[] { "tvdb:73545" }));
    }

    [Fact]
    public void TheFirstKeyThatMatchesWins()
    {
        var snapshot = WithBattlestar();

        Assert.Equal(new[] { "Bear McCreary" }, snapshot.Find(new[] { "tvdb:73545", "tmdbtv:1972" })!.Composers);
    }

    [Fact]
    public void AKnownAnswerIsNotAskedAgain()
    {
        var snapshot = WithBattlestar();

        Assert.False(snapshot.NeedsComposers(new[] { "imdb:tt0407362" }, Now.AddDays(59)));
        Assert.True(snapshot.NeedsComposers(new[] { "imdb:tt0407362" }, Now.AddDays(61)));
    }

    [Fact]
    public void ANobodyKnowsIsRememberedTooButForLess()
    {
        // Without remembering a miss, a quarter of any library hits the network on every scan.
        // Remembering it for as long as a hit would be worse: the databases do grow.
        var snapshot = new ComposerSnapshot();
        snapshot.RecordComposers(new[] { "imdb:tt0000000" }, ResearchedCredits.None, "nobody", Now);

        Assert.False(snapshot.NeedsComposers(new[] { "imdb:tt0000000" }, Now.AddDays(13)));
        Assert.True(snapshot.NeedsComposers(new[] { "imdb:tt0000000" }, Now.AddDays(15)));
    }

    [Fact]
    public void AWorkNeverAskedAboutNeedsAsking()
    {
        Assert.True(new ComposerSnapshot().NeedsLookUp(new[] { "imdb:tt0407362" }, Now));
    }

    [Fact]
    public void RecordingAgainReplacesRatherThanAccumulates()
    {
        var snapshot = WithBattlestar();
        snapshot.RecordComposers(
            new[] { "imdb:tt0407362" },
            new ResearchedCredits(new[] { "Richard Gibbs" }, null, null),
            "Wikipedia",
            Now.AddDays(70));

        Assert.Equal(new[] { "Richard Gibbs" }, snapshot.Find(new[] { "imdb:tt0407362" })!.Composers);
        Assert.Equal(2, snapshot.Entries.Count);
    }

    [Fact]
    public void ItSurvivesBeingWrittenAndReadBack()
    {
        var snapshot = WithBattlestar();
        snapshot.UpdatedUtc = Now;

        var read = JsonSerializer.Deserialize<ComposerSnapshot>(JsonSerializer.Serialize(snapshot))!;

        Assert.Equal(new[] { "Bear McCreary" }, read.Find(new[] { "tmdbtv:1972" })!.Composers);
        Assert.Equal(1, read.Known / 2);
        Assert.Equal(Now, read.UpdatedUtc);
    }

    [Fact]
    public void OnlyAnAnswerThatNamesSomebodyCountsAsKnown()
    {
        var snapshot = new ComposerSnapshot();
        snapshot.RecordComposers(new[] { "imdb:tt0000000" }, ResearchedCredits.None, "nobody", Now);

        Assert.Single(snapshot.Entries);
        Assert.Equal(0, snapshot.Known);
    }

    // ---- The keys ----

    [Fact]
    public void AFilmAndAShowWithTheSameTmdbNumberAreDifferentWorks()
    {
        Assert.Equal(new[] { "tmdb:1972" }, CreditsKeys.For(null, "1972", null, isSeries: false));
        Assert.Equal(new[] { "tmdbtv:1972" }, CreditsKeys.For(null, "1972", null, isSeries: true));
    }

    [Fact]
    public void ImdbComesFirstBecauseBothSourcesAreKeyedOnIt()
    {
        Assert.Equal(
            new[] { "imdb:tt0407362", "tmdbtv:1972", "tvdb:73545" },
            CreditsKeys.For("tt0407362", "1972", "73545", isSeries: true));
    }

    [Fact]
    public void AnItemWithNoIdsHasNoKeys()
    {
        Assert.Empty(CreditsKeys.For(null, "  ", string.Empty, isSeries: false));
    }
}
