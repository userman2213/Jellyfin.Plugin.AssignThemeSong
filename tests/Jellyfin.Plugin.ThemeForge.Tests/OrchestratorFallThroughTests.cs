using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins what happens to one item when the catalogue's answer cannot be used: the search runs, in
/// the same pass, and the title's music has been looked up before it does.
/// </summary>
/// <remarks>
/// Everything around the orchestrator is a fake, so these exercise its own decisions and nothing
/// else. The library manager is a proxy that fails on any call, which is also the proof that
/// processing one item never consults it.
/// </remarks>
public sealed class OrchestratorFallThroughTests : IDisposable
{
    private const string CatalogueUrl = "https://www.youtube.com/watch?v=catalogue01";
    private const string SearchUrl = "https://www.youtube.com/watch?v=searchhit01";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-orch-" + Guid.NewGuid().ToString("N"));
    private readonly Movie _item;
    private readonly MediaIdentity _identity;

    public OrchestratorFallThroughTests()
    {
        Directory.CreateDirectory(_root);
        _item = new Movie { Id = Guid.NewGuid(), Name = "Fight Club", Path = Path.Combine(_root, "Fight Club.mkv") };
        _identity = new MediaIdentity
        {
            ItemId = _item.Id,
            Title = "Fight Club",
            NormalizedTitle = TitleNormalizer.Normalize("Fight Club"),
            Year = 1999,
            Kind = Jellyfin.Data.Enums.BaseItemKind.Movie,
            TmdbId = "550",
            ImdbId = "tt0137523",
        };
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ---- the catalogue's link will not download -------------------------------------

    [Fact]
    public async Task ACatalogueLinkThatWillNotDownloadFallsThroughToTheSearch()
    {
        // Before, the item was marked failed and the search never ran; the next run met the same
        // link first and failed again, so the item never got a theme.
        var world = new World(_root)
        {
            Provenance = { Answer = Catalogue() },
            Search = { Results = { Searched() } },
            Acquisition = { Broken = { CatalogueUrl } },
        };

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.Assigned, outcome);
        Assert.Equal(new[] { CatalogueUrl, SearchUrl }, world.Acquisition.Tried);
        Assert.True(world.Search.Asked > 0, "the search should have run after the catalogue's link failed");
    }

    [Fact]
    public async Task TheFallThroughCountsAsOneAttemptNotTwo()
    {
        // Each processed item is one attempt against the limit. Counting the catalogue's try and
        // the search's try separately would exhaust an item's attempts at twice the speed.
        var world = new World(_root)
        {
            Provenance = { Answer = Catalogue() },
            Search = { Results = { Searched() } },
            Acquisition = { Broken = { CatalogueUrl } },
        };

        await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(1, world.Index.Get(_item.Id)!.Attempts);
    }

    [Fact]
    public async Task AWorkingCatalogueLinkIsUsedAndNothingIsSearched()
    {
        var world = new World(_root)
        {
            Provenance = { Answer = Catalogue() },
            Search = { Results = { Searched() } },
        };

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.Assigned, outcome);
        Assert.Equal(new[] { CatalogueUrl }, world.Acquisition.Tried);
        Assert.Equal(0, world.Search.Asked);
    }

    [Fact]
    public async Task WhenTheSearchItselfFindsNothingTheItemSaysSo()
    {
        var world = new World(_root)
        {
            Provenance = { Answer = Catalogue() },
            Acquisition = { Broken = { CatalogueUrl } },
        };

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.NoCandidate, outcome);
    }

    // ---- the music is looked up before the search ------------------------------------

    [Fact]
    public async Task TheTitlesMusicIsLookedUpBeforeItIsSearchedFor()
    {
        // The daily research leaves out what ThemerrDB covers. When ThemerrDB's link turns out to
        // be unusable the title is searched after all, and was being searched with no composer and
        // no theme name.
        var world = new World(_root) { Search = { Results = { Searched() } } };

        await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        var asked = Assert.Single(world.Composers.Synced);
        Assert.Equal("tt0137523", asked.ImdbId);
        Assert.True(world.Composers.SyncedBeforeFirstSearch, "the research has to happen before the search");
    }

    [Fact]
    public async Task WhatTheResearchFoundReachesTheSearch()
    {
        // Only a query naming the composer finds anything. The composer rung is in the plan only
        // when the identity names a composer, which it does only after the research.
        var world = new World(_root) { Search = { Results = { Searched() } } };
        world.Search.OnlyWhen = query => query.Contains("Dust Brothers", StringComparison.Ordinal);
        world.Identities.AfterResearch = new MediaIdentity
        {
            ItemId = _identity.ItemId,
            Title = _identity.Title,
            NormalizedTitle = _identity.NormalizedTitle,
            Year = _identity.Year,
            Kind = _identity.Kind,
            TmdbId = _identity.TmdbId,
            ImdbId = _identity.ImdbId,
            Composers = new[] { "The Dust Brothers" },
        };

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.Assigned, outcome);
        Assert.Contains(world.Search.Queries, query => query.Contains("Dust Brothers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingIsLookedUpWhenTheResearchIsSwitchedOff()
    {
        var world = new World(_root) { Search = { Results = { Searched() } } };
        var configuration = Config();
        configuration.ResearchComposers = false;

        await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), configuration, CancellationToken.None);

        Assert.Empty(world.Composers.Synced);
    }

    [Fact]
    public async Task ATitleAlreadyResearchedIsNotAskedAboutAgain()
    {
        // The cache already answers every question about it, so the search goes ahead without
        // waiting on the catalogue -- which may be busy with a nightly pass.
        var world = new World(_root) { Search = { Results = { Searched() } } };
        var keys = CreditsKeys.For(_identity.ImdbId, _identity.TmdbId, _identity.TvdbId, _identity.IsSeries);
        world.Composers.Cache.RecordComposers(keys, new ResearchedCredits(new[] { "The Dust Brothers" }, null, null), "IMDb", DateTime.UtcNow);
        world.Composers.Cache.RecordTheme(keys, ResearchedCredits.None, "nobody", DateTime.UtcNow);
        world.Composers.Cache.RecordOrigin(keys, ResearchedCredits.None with { Countries = new[] { "US" } }, DateTime.UtcNow);

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.Assigned, outcome);
        Assert.Empty(world.Composers.Synced);
    }

    [Fact]
    public async Task AResearchFailureDoesNotStopTheSearch()
    {
        var world = new World(_root) { Search = { Results = { Searched() } } };
        world.Composers.Fail = true;

        var outcome = await world.Orchestrator(_identity).ProcessItemAsync(_item, new RunReport(), Config(), CancellationToken.None);

        Assert.Equal(ItemOutcome.Assigned, outcome);
    }

    // ---- the pieces --------------------------------------------------------------------

    private static PluginConfiguration Config()
    {
        var configuration = TestData.Config();
        configuration.ResearchComposers = true;
        configuration.StopLadderOnConfidentHit = true;
        configuration.RequestDelayMs = 0;
        return configuration;
    }

    private static Candidate Catalogue() => new()
    {
        Id = "catalogue01",
        Url = CatalogueUrl,
        Title = "Fight Club (ThemerrDB)",
        Channel = "ThemerrDB",
        FoundBy = new SearchQuery("themerrdb:x", 0, "catalogue"),
        IsHydrated = true,
        Provenance = "ThemerrDB",
    };

    private static Candidate Searched() => new()
    {
        Id = "searchhit01",
        Url = SearchUrl,
        Title = "Fight Club Main Theme",
        Channel = "Soundtracks",
        DurationSeconds = 180,
        FoundBy = new SearchQuery("Fight Club main title theme", 0, "{title} main title theme"),
        IsHydrated = true,
    };

    /// <summary>Everything the orchestrator talks to, as fakes the tests can set up and inspect.</summary>
    private sealed class World
    {
        private readonly string _root;

        public World(string root)
        {
            _root = root;
            Search = new FakeSearch(this);
            Composers = new FakeComposers(this);
            Acquisition = new FakeAcquisition(root);
        }

        public FakeProvenance Provenance { get; } = new();

        public FakeSearch Search { get; }

        public FakeComposers Composers { get; }

        public FakeAcquisition Acquisition { get; }

        public FakeIdentities Identities { get; } = new();

        public MemoryIndex Index { get; } = new();

        public ThemeOrchestrator Orchestrator(MediaIdentity identity)
        {
            Identities.Initial = identity;
            return new ThemeOrchestrator(
                Refuse<ILibraryManager>(),
                Identities,
                new QueryPlanner(),
                Search,
                new[] { Provenance },
                new EmptyCatalogue(),
                Composers,
                new FixedScores(),
                new DecisionPolicy(),
                Acquisition,
                new FakePlacement(_root),
                Index,
                new AlwaysEnabled(),
                new NullThemeForgeLogger<ThemeOrchestrator>());
        }
    }

    private sealed class FakeIdentities : IMediaIdentityResolver
    {
        public MediaIdentity? Initial { get; set; }

        public MediaIdentity? AfterResearch { get; set; }

        public int Resolved { get; private set; }

        public MediaIdentity? Resolve(BaseItem item)
        {
            Resolved++;
            return Resolved > 1 && AfterResearch is not null ? AfterResearch : Initial;
        }
    }

    private sealed class FakeProvenance : IThemeProvenanceSource
    {
        public Candidate? Answer { get; set; }

        public string Name => "ThemerrDB";

        public int Order => 0;

        public bool IsEnabled(PluginConfiguration configuration) => true;

        public Task<Candidate?> FindAsync(MediaIdentity identity, PluginConfiguration configuration, CancellationToken cancellationToken) =>
            Task.FromResult(Answer);
    }

    private sealed class FakeSearch : ICandidateSource
    {
        private readonly World _world;

        public FakeSearch(World world) => _world = world;

        public string Name => "search";

        public List<Candidate> Results { get; } = new();

        public List<string> Queries { get; } = new();

        public int Asked { get; private set; }

        /// <summary>Gets or sets which queries find anything; all of them when unset.</summary>
        public Func<string, bool>? OnlyWhen { get; set; }

        public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, int maxResults, CancellationToken cancellationToken)
        {
            if (Asked == 0)
            {
                _world.Composers.SyncedBeforeFirstSearch = _world.Composers.Synced.Count > 0;
            }

            Asked++;
            Queries.Add(query.Text);
            var hits = OnlyWhen is null || OnlyWhen(query.Text) ? Results.ToList() : new List<Candidate>();
            return Task.FromResult<IReadOnlyList<Candidate>>(hits);
        }

        public Task<IReadOnlyList<Candidate>> HydrateAsync(IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }

    private sealed class FakeComposers : IComposerCatalogue
    {
        private readonly World _world;

        public FakeComposers(World world) => _world = world;

        public List<CreditsRequest> Synced { get; } = new();

        public bool SyncedBeforeFirstSearch { get; set; }

        public bool Fail { get; set; }

        /// <summary>Gets what the cache already holds.</summary>
        public ComposerSnapshot Cache { get; } = new();

        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(Cache);

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new System.Net.Http.HttpRequestException("research is down");
            }

            Synced.AddRange(works);
            return Task.FromResult(Cache);
        }

        public ResearchedCredits Known(IReadOnlyList<string> keys) => ResearchedCredits.None;
    }

    /// <summary>Downloads anything except the links it is told are broken.</summary>
    private sealed class FakeAcquisition : IAcquisitionEngine
    {
        private readonly string _root;

        public FakeAcquisition(string root) => _root = root;

        public HashSet<string> Broken { get; } = new(StringComparer.Ordinal);

        public List<string> Tried { get; } = new();

        public Task<AcquiredAudio> AcquireAsync(Candidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken)
        {
            Tried.Add(candidate.Url);
            if (Broken.Contains(candidate.Url))
            {
                throw new InvalidOperationException("yt-dlp downloaded nothing: This video is unavailable");
            }

            // Its own directory: the orchestrator deletes the staging directory when it is done.
            var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var file = Path.Combine(staging, "theme.mp3");
            File.WriteAllBytes(file, new byte[] { 1, 2, 3 });

            return Task.FromResult(new AcquiredAudio
            {
                StagingPath = file,
                DurationSeconds = 120,
                Sha256 = "x",
                SizeBytes = 3,
                SourceUrl = candidate.Url,
            });
        }
    }

    private sealed class FakePlacement : IThemePlacementEngine
    {
        private readonly string _root;

        public FakePlacement(string root) => _root = root;

        public (string? Directory, string? Reason) ResolveThemeDirectory(BaseItem item, PluginConfiguration configuration) => (_root, null);

        public bool HasExistingTheme(BaseItem item, PluginConfiguration configuration) => false;

        public Task<PlacementResult> PlaceAsync(BaseItem item, AcquiredAudio audio, PluginConfiguration configuration, ResolvedThemePolicy policy, CancellationToken cancellationToken) =>
            Task.FromResult(PlacementResult.Placed(Path.Combine(_root, "theme.mp3")));
    }

    /// <summary>Gives every candidate a score its title earns: assignable when it names a theme.</summary>
    private sealed class FixedScores : IScoringEngine
    {
        public ScoreResult Score(Candidate candidate, ScoringContext context) => candidate.Title.Contains("Theme", StringComparison.Ordinal)
            ? new() { Candidate = candidate, Total = 90, Breakdown = new[] { new Signal("Fixed", 1, 90, "a theme", MainTheme: true) } }
            : new() { Candidate = candidate, Total = 10, Breakdown = Array.Empty<Signal>() };

        public IReadOnlyList<ScoreResult> Rank(IEnumerable<Candidate> candidates, ScoringContext context) =>
            candidates.Select(candidate => Score(candidate, context)).OrderByDescending(result => result.Total).ToList();
    }

    private sealed class EmptyCatalogue : IThemerrDbCatalogue
    {
        public Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ThemerrDbSnapshot());

        public Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken) =>
            Task.FromResult(new ThemerrDbSnapshot());
    }

    private sealed class AlwaysEnabled : ILibraryPolicyResolver
    {
        public ResolvedThemePolicy Resolve(BaseItem item, PluginConfiguration configuration) =>
            new(true, ThemeOverwritePolicy.ReplaceOwn, "Films");

        public IReadOnlyList<LibrarySummary> ListLibraries(PluginConfiguration configuration) => Array.Empty<LibrarySummary>();

        public LibraryPolicyAudit Audit(PluginConfiguration configuration) => throw new NotSupportedException();

        public void LogEffectiveRules(PluginConfiguration configuration)
        {
        }
    }

    private sealed class MemoryIndex : IThemeIndex
    {
        private readonly Dictionary<Guid, ThemeIndexEntry> _entries = new();

        public ThemeIndexEntry? Get(Guid itemId) => _entries.GetValueOrDefault(itemId);

        public ThemeIndexEntry? Resolve(Guid itemId, string stableKey) => Get(itemId);

        public IReadOnlyCollection<ThemeIndexEntry> All() => _entries.Values;

        public IReadOnlyList<ThemeIndexEntry> ReviewQueue() => Array.Empty<ThemeIndexEntry>();

        public void Put(ThemeIndexEntry entry) => _entries[entry.ItemId] = entry;

        public bool Remove(Guid itemId) => _entries.Remove(itemId);

        public string? FindAssignmentOwner(string videoId, Guid excludingItem) => null;

        public Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task LoadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>An implementation of any interface that fails on every call.</summary>
    private static T Refuse<T>()
        where T : class => DispatchProxy.Create<T, RefusingProxy>();

    public class RefusingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} was not expected to be called");
    }
}
