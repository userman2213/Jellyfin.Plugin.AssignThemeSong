using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins the removal of a theme recorded as ThemerrDB's that ThemerrDB does not give the show.
/// </summary>
/// <remarks>
/// Black Summer, the Netflix zombie series (TMDB 88052), is not in ThemerrDB. Until 3.1.1 it was
/// matched by a name close to its own to the anime Black Summoner (157842), given that anime's
/// opening, and recorded as ThemerrDB's answer -- which nothing ever replaced. The real ThemerrDB
/// source is used here, with a network that fails any request, so the lookup is the fixed one.
/// </remarks>
public sealed class ThemerrDbMismatchTests : IDisposable
{
    private const string OldWording =
        "Provenance: +1.00 x1 = +100.00 (listed in ThemerrDB against this title's own database id, so it was not scored)";

    private const string NewWording =
        "Provenance: +1.00 x1 = +100.00 (listed in ThemerrDB under its own TMDB id 88052, so it was not scored)";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-mismatch-" + Guid.NewGuid().ToString("N"));
    private readonly string _themePath;
    private readonly Series _item;

    public ThemerrDbMismatchTests()
    {
        Directory.CreateDirectory(_root);
        _themePath = Path.Combine(_root, "theme.mp3");
        _item = new Series { Id = Guid.NewGuid(), Name = "Black Summer", Path = _root };
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

    [Fact]
    public async Task AThemeTakenFromAnotherShowsEntryIsRemovedAndTheShowLookedAtAgain()
    {
        var index = await Written(OldWording);

        var outcome = await Process(index, BlackSummonerOnly());

        Assert.False(File.Exists(_themePath));
        var entry = index.Get(_item.Id)!;
        Assert.NotEqual(ThemeItemState.AutoAssigned, entry.State);
        Assert.Null(entry.ThemePath);
        Assert.Null(entry.AssignedBy);
        Assert.DoesNotContain("UQgv8MBxCuU", entry.ChosenUrl ?? string.Empty, StringComparison.Ordinal);

        // Looked at again in the same pass: nobody is credited here, so nothing is searched.
        Assert.Equal(ItemOutcome.NoCandidate, outcome);
        Assert.True(entry.NothingToSearchFor);
        Assert.Equal(0, entry.Attempts);
    }

    [Fact]
    public async Task AShowThemerrDbListsUnderItsOwnIdKeepsItsTheme()
    {
        var index = await Written(OldWording);
        var listed = BlackSummonerOnly();
        listed.TvShows.Add(new CatalogueTitle("88052", "Black Summer"));

        await Process(index, listed);

        Assert.True(File.Exists(_themePath));
        Assert.Equal(ThemeItemState.AutoAssigned, index.Get(_item.Id)!.State);
    }

    [Fact]
    public async Task AThemeChangedByHandIsKept()
    {
        var index = await Written(OldWording);
        await File.WriteAllTextAsync(_themePath, "a theme the owner put here");

        await Process(index, BlackSummonerOnly());

        Assert.True(File.Exists(_themePath));
        Assert.Equal(ThemeItemState.AutoAssigned, index.Get(_item.Id)!.State);
    }

    [Fact]
    public async Task APickRecordedSinceTheFixIsLeftAlone()
    {
        // It says it was listed under the show's own id, so it was; ThemerrDB dropping the show
        // later is not a reason to remove a theme it chose for it.
        var index = await Written(NewWording);

        await Process(index, BlackSummonerOnly());

        Assert.True(File.Exists(_themePath));
        Assert.Equal(ThemeItemState.AutoAssigned, index.Get(_item.Id)!.State);
    }

    [Theory]
    [InlineData(ThemeItemState.ManualOverride, ThemeIndexEntry.ByYou)]
    [InlineData(ThemeItemState.Approved, ThemeIndexEntry.ByYou)]
    public async Task AThemeYouChoseIsNeverTouched(ThemeItemState state, string assignedBy)
    {
        var index = await Written(OldWording, state, assignedBy);

        await Process(index, BlackSummonerOnly());

        Assert.True(File.Exists(_themePath));
        Assert.Equal(state, index.Get(_item.Id)!.State);
    }

    [Fact]
    public async Task ADryRunRemovesNothing()
    {
        var index = await Written(OldWording);

        await Process(index, BlackSummonerOnly(), dryRun: true);

        Assert.True(File.Exists(_themePath));
        Assert.Equal(ThemeItemState.AutoAssigned, index.Get(_item.Id)!.State);
    }

    [Fact]
    public async Task WithNoCopyOfThemerrDbNothingIsRemoved()
    {
        var index = await Written(OldWording);

        await Process(index, new ThemerrDbSnapshot());

        Assert.True(File.Exists(_themePath));
    }

    private static ThemerrDbSnapshot BlackSummonerOnly() => new()
    {
        UpdatedUtc = DateTime.UtcNow,
        TvShows = new List<CatalogueTitle> { new("157842", "Black Summoner") },
    };

    private async Task<MemoryIndex> Written(string provenance, ThemeItemState state = ThemeItemState.AutoAssigned, string assignedBy = "ThemerrDB")
    {
        await File.WriteAllBytesAsync(_themePath, new byte[] { 9, 8, 7, 6 });

        var index = new MemoryIndex();
        index.Put(new ThemeIndexEntry
        {
            ItemId = _item.Id,
            Label = "Black Summer (2019)",
            State = state,
            AssignedBy = assignedBy,
            ChosenId = "UQgv8MBxCuU",
            ChosenUrl = "https://www.youtube.com/watch?v=UQgv8MBxCuU",
            ChosenTitle = "Black Summer (ThemerrDB)",
            ThemePath = _themePath,
            Sha256 = await FileHash.ComputeSha256Async(_themePath, CancellationToken.None),
            ScoreBreakdown = new List<string> { "Total: 100.0/100", provenance },
            DecidedByMatcher = ThemeIndexEntry.CurrentMatcher,
        });
        return index;
    }

    private Task<ItemOutcome> Process(MemoryIndex index, ThemerrDbSnapshot snapshot, bool dryRun = false)
    {
        var configuration = TestData.Config();
        configuration.RequestDelayMs = 0;
        configuration.ResearchComposers = false;
        configuration.DryRun = dryRun;

        var identity = new MediaIdentity
        {
            ItemId = _item.Id,
            Title = "Black Summer",
            NormalizedTitle = TitleNormalizer.Normalize("Black Summer"),
            Year = 2019,
            Kind = Jellyfin.Data.Enums.BaseItemKind.Series,
            TmdbId = "88052",
            ImdbId = "tt8923854",
            TvdbId = "350587",
        };

        var themerrDb = new FixedThemerrDb(snapshot);
        var source = new ThemerrDbSource(themerrDb, new NoNetwork(), new AliveLinks(), new NullThemeForgeLogger<ThemerrDbSource>());

        var orchestrator = new ThemeOrchestrator(
            DispatchProxy.Create<ILibraryManager, RefusingProxy>(),
            new FixedIdentity(identity),
            new QueryPlanner(),
            new NoSearch(),
            new IThemeProvenanceSource[] { source },
            themerrDb,
            new NoComposers(),
            new NothingScores(),
            new DecisionPolicy(),
            DispatchProxy.Create<IAcquisitionEngine, RefusingProxy>(),
            new FolderPlacement(_root),
            index,
            new NeverReplace(),
            new NullThemeForgeLogger<ThemeOrchestrator>());

        return orchestrator.ProcessItemAsync(_item, new RunReport(), configuration, CancellationToken.None);
    }

    public class RefusingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} was not expected to be called");
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Refuse());

        private sealed class Refuse : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new InvalidOperationException($"no request should have been made, but one went to {request.RequestUri}");
        }
    }

    private sealed class AliveLinks : ILinkChecker
    {
        public Task<LinkState> CheckAsync(string url, CancellationToken cancellationToken) => Task.FromResult(LinkState.Alive);
    }

    private sealed class FixedThemerrDb : IThemerrDbCatalogue
    {
        private readonly ThemerrDbSnapshot _snapshot;

        public FixedThemerrDb(ThemerrDbSnapshot snapshot) => _snapshot = snapshot;

        public Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(_snapshot);

        public Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken) => GetAsync(cancellationToken);
    }

    private sealed class FixedIdentity : IMediaIdentityResolver
    {
        private readonly MediaIdentity _identity;

        public FixedIdentity(MediaIdentity identity) => _identity = identity;

        public MediaIdentity? Resolve(BaseItem item) => _identity;
    }

    private sealed class NoSearch : ICandidateSource
    {
        public string Name => "search";

        public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, int maxResults, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());

        public Task<IReadOnlyList<Candidate>> HydrateAsync(IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }

    /// <summary>Says a theme exists exactly when one is in the folder, as the real engine does.</summary>
    private sealed class FolderPlacement : IThemePlacementEngine
    {
        private readonly string _root;

        public FolderPlacement(string root) => _root = root;

        public (string? Directory, string? Reason) ResolveThemeDirectory(BaseItem item, PluginConfiguration configuration) => (_root, null);

        public bool HasExistingTheme(BaseItem item, PluginConfiguration configuration) =>
            ThemePlacementEngine.ExistingThemeFiles(_root).Count > 0;

        public Task<PlacementResult> PlaceAsync(BaseItem item, AcquiredAudio audio, PluginConfiguration configuration, ResolvedThemePolicy policy, CancellationToken cancellationToken) =>
            throw new NotSupportedException("nothing should be placed");
    }

    private sealed class NoComposers : IComposerCatalogue
    {
        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ComposerSnapshot());

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public ResearchedCredits Known(IReadOnlyList<string> keys) => ResearchedCredits.None;
    }

    private sealed class NothingScores : IScoringEngine
    {
        public ScoreResult Score(Candidate candidate, ScoringContext context) => new() { Candidate = candidate, Total = 0, Breakdown = Array.Empty<Signal>() };

        public IReadOnlyList<ScoreResult> Rank(IEnumerable<Candidate> candidates, ScoringContext context) =>
            candidates.Select(candidate => Score(candidate, context)).ToList();
    }

    private sealed class NeverReplace : ILibraryPolicyResolver
    {
        public ResolvedThemePolicy Resolve(BaseItem item, PluginConfiguration configuration) =>
            new(true, ThemeOverwritePolicy.Never, "Shows");

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
}
