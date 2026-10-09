using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the one seam that puts a researched composer in front of the rest of the pipeline.
/// </summary>
/// <remarks>
/// Identity resolution asks a single synchronous question — who wrote this? — and three things
/// downstream go quiet when the answer is empty: the search ladder drops its composer rung,
/// <see cref="ComposerRule"/> abstains, and an ordinary title loses its only item-side
/// corroborator. The decorator answers that question from the cache, so all three come back
/// without any of them knowing where the name came from. These tests pin the two rules that make
/// it safe: Jellyfin's own credits always win, and a cache miss changes nothing.
/// </remarks>
public class ResearchedComposerTests
{
    private sealed class Knows : IComposerCatalogue
    {
        private readonly ResearchedCredits _credits;

        public Knows(params string[] composers) =>
            _credits = new ResearchedCredits(composers, null, null);

        public Knows(string artist, bool asArtist) =>
            _credits = new ResearchedCredits(Array.Empty<string>(), artist, null);

        public IReadOnlyList<string>? Asked { get; private set; }

        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ComposerSnapshot());

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public ResearchedCredits Known(IReadOnlyList<string> keys)
        {
            Asked = keys;
            return _credits;
        }
    }

    private sealed class KnowsNobody : IComposerCatalogue
    {
        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ComposerSnapshot());

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public ResearchedCredits Known(IReadOnlyList<string> keys) => ResearchedCredits.None;
    }

    private sealed class Credits : IPeopleLookup
    {
        private readonly string[] _composers;
        private readonly string[] _crew;

        public Credits(string[]? composers = null, string[]? crew = null)
        {
            _composers = composers ?? Array.Empty<string>();
            _crew = crew ?? Array.Empty<string>();
        }

        public IReadOnlyList<string> Composers(BaseItem item) => _composers;

        public IReadOnlyList<string> MusicCredits(BaseItem item) => _crew;

        public ThemeSong? Theme(BaseItem item) => null;
    }

    private static Series Battlestar()
    {
        var series = new Series { Name = "Battlestar Galactica" };
        series.SetProviderId(MetadataProvider.Imdb, "tt0407362");
        series.SetProviderId(MetadataProvider.Tmdb, "1972");
        return series;
    }

    [Fact]
    public void TheCacheAnswersWhenJellyfinHasNobody()
    {
        var lookup = new ResearchedPeopleLookup(new Credits(), new Knows("Bear McCreary"));

        Assert.Equal(new[] { "Bear McCreary" }, lookup.Composers(Battlestar()));
    }

    [Fact]
    public void JellyfinsOwnCreditsWin()
    {
        // The library's own metadata was fetched or entered for this library and its owner can
        // correct it. The cache is a guess about a work that may have been identified imperfectly,
        // so it is only ever consulted when there is nothing to lose.
        var lookup = new ResearchedPeopleLookup(new Credits(new[] { "Richard Gibbs" }), new Knows("Bear McCreary"));

        Assert.Equal(new[] { "Richard Gibbs" }, lookup.Composers(Battlestar()));
    }

    [Fact]
    public void AMissLeavesTheItemExactlyAsItWas()
    {
        var lookup = new ResearchedPeopleLookup(new Credits(), new KnowsNobody());

        Assert.Empty(lookup.Composers(Battlestar()));
    }

    [Fact]
    public void ASoundtrackArtistStandsInWhenNobodyIsCreditedWithWritingIt()
    {
        var lookup = new ResearchedPeopleLookup(new Credits(), new Knows("Vangelis", asArtist: true));

        Assert.Equal(new[] { "Vangelis" }, lookup.Composers(Battlestar()));
    }

    [Fact]
    public void TheCacheIsAskedUnderEveryIdTheItemCarries()
    {
        var catalogue = new Knows("Bear McCreary");
        new ResearchedPeopleLookup(new Credits(), catalogue).Composers(Battlestar());

        Assert.Equal(new[] { "imdb:tt0407362", "tmdbtv:1972" }, catalogue.Asked);
    }

    [Fact]
    public void AFilmIsAskedAboutUnderTheFilmKey()
    {
        // TMDB numbers films and shows separately, so asking about film 1972 under the show key
        // would return whatever show happens to hold that number.
        var film = new Movie { Name = "Blade Runner 2049" };
        film.SetProviderId(MetadataProvider.Tmdb, "335984");

        var catalogue = new Knows("Hans Zimmer");
        new ResearchedPeopleLookup(new Credits(), catalogue).Composers(film);

        Assert.Equal(new[] { "tmdb:335984" }, catalogue.Asked);
    }

    [Fact]
    public void TheWiderMusicCrewIsNotResearched()
    {
        // Nothing in the databases consulted here records a conductor or arranger for a film, and
        // a name invented for the slot would be searched for and believed.
        var lookup = new ResearchedPeopleLookup(new Credits(crew: new[] { "Ron Jones" }), new Knows("Bear McCreary"));

        Assert.Equal(new[] { "Ron Jones" }, lookup.MusicCredits(Battlestar()));
    }

    // ---- What the rule does with the wider crew ----

    [Fact]
    public void NamingSomebodyElseCreditedOnTheMusicIsWorthSomething()
    {
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Star Trek: The Next Generation Main Title - Ron Jones"),
            TestData.Context(TestData.Series("Star Trek: The Next Generation", musicCredits: new[] { "Ron Jones" })));

        Assert.Equal(0.5, verdict.Raw);
        Assert.Contains("Ron Jones", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheComposerIsStillWorthMore()
    {
        // A conductor records dozens of scores; a composer wrote this one.
        var candidate = TestData.Candidate("Main Title - Greg Edmonson, conducted by Ron Jones");
        var identity = TestData.Series("Firefly", composers: new[] { "Greg Edmonson" }, musicCredits: new[] { "Ron Jones" });

        var verdict = new ComposerRule().Evaluate(candidate, TestData.Context(identity));

        Assert.Equal(1.0, verdict.Raw);
        Assert.Contains("the composer", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void KnowingNobodyAtAllStillAbstains()
    {
        var verdict = new ComposerRule().Evaluate(
            TestData.Candidate("Lost Main Title"),
            TestData.Context(TestData.Series("Lost")));

        Assert.Equal(0, verdict.Raw);
        Assert.False(verdict.IsVeto);
    }
}
