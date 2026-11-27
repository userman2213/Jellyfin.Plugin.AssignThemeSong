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
/// Pins that a theme a search guessed gives way to ThemerrDB, and that nothing else is put at risk.
/// </summary>
/// <remarks>
/// Until 3.1 a search's pick was permanent in a library set never to replace a theme, which is the
/// default: the eligibility gate stopped the item before ThemerrDB was asked, and placement would
/// not write over a theme even where ThemeForge had written it. So ThemerrDB's daily updates never
/// reached anything a search had already filled.
/// </remarks>
public sealed class CatalogueReplacesSearchPickTests : IDisposable
{
    private const string GuessId = "searchguess";
    private const string CatalogueId = "themerrdb01";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-replace-" + Guid.NewGuid().ToString("N"));
    private readonly Movie _item;
    private readonly MediaIdentity _identity;
    private readonly string _themePath;

    public CatalogueReplacesSearchPickTests()
    {
        Directory.CreateDirectory(_root);
        _themePath = Path.Combine(_root, "theme.mp3");
        _item = new Movie { Id = Guid.NewGuid(), Name = "Lucky Strike", Path = Path.Combine(_root, "Lucky Strike.mkv") };
        _identity = new MediaIdentity
        {
            ItemId = _item.Id,
            Title = "Lucky Strike",
            NormalizedTitle = TitleNormalizer.Normalize("Lucky Strike"),
            Year = 2026,
            Kind = Jellyfin.Data.Enums.BaseItemKind.Movie,
            ImdbId = "tt19035928",
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

    [Fact]
    public async Task ASearchsGuessIsReplacedWhenThemerrDbListsTheTitle()
    {
        var world = await World.WithSearchPickAsync(this);
        world.Catalogue.Answer = Listed();

        var outcome = await world.ProcessAsync();

        var entry = world.Index.Get(_item.Id)!;
        Assert.Equal(ItemOutcome.Assigned, outcome);
        Assert.Equal("ThemerrDB", entry.AssignedBy);
        Assert.Equal(CatalogueId, entry.ChosenId);
        Assert.Equal(ThemeItemState.AutoAssigned, entry.State);
    }

    [Fact]
    public async Task ItIsReplacedEvenInALibrarySetNeverToReplaceATheme()
    {
        // The library rule protects themes a person or a catalogue chose. A search's guess is
        // ThemeForge's own file, and placement is told it may write over that one.
        var world = await World.WithSearchPickAsync(this);
        world.Catalogue.Answer = Listed();

        await world.ProcessAsync();

        Assert.Equal(ThemeOverwritePolicy.ReplaceOwn, Assert.Single(world.Placement.Policies).Overwrite);
    }

    [Fact]
    public async Task WithNoCatalogueEntryTheGuessStaysAndIsNotSearchedAgain()
    {
        // No churn: without a catalogue entry nothing is better than what it has, so the search
        // does not run again every night.
        var world = await World.WithSearchPickAsync(this);

        var outcome = await world.ProcessAsync();

        var entry = world.Index.Get(_item.Id)!;
        Assert.Equal(ItemOutcome.Skipped, outcome);
        Assert.Equal(0, world.Search.Asked);
        Assert.Equal(GuessId, entry.ChosenId);
        Assert.Contains("only a ThemerrDB or Plex entry", entry.LastSkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThemeChangedByHandIsLeftAlone()
    {
        var world = await World.WithSearchPickAsync(this);
        world.Catalogue.Answer = Listed();
        await File.WriteAllBytesAsync(_themePath, new byte[] { 9, 9, 9, 9 });

        var outcome = await world.ProcessAsync();

        Assert.Equal(ItemOutcome.Skipped, outcome);
        Assert.Empty(world.Acquisition.Tried);
        Assert.Contains("changed", world.Index.Get(_item.Id)!.LastSkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AThemeSomeoneAddedBesideItIsNotDeleted()
    {
        // Placement clears every theme file in the folder before moving the new one in. One that
        // ThemeForge did not write must stop the replacement, not be swept away by it.
        var world = await World.WithSearchPickAsync(this);
        world.Catalogue.Answer = Listed();
        await File.WriteAllBytesAsync(Path.Combine(_root, "theme.ogg"), new byte[] { 5 });

        var outcome = await world.ProcessAsync();

        Assert.Equal(ItemOutcome.Skipped, outcome);
        Assert.Empty(world.Placement.Policies);
        Assert.True(File.Exists(Path.Combine(_root, "theme.ogg")));
    }

    [Fact]
    public async Task ACatalogueThemeThatWillNotDownloadLeavesTheCurrentOneExactlyAsItWas()
    {
        var world = await World.WithSearchPickAsync(this);
        world.Catalogue.Answer = Listed();
        world.Acquisition.Broken = true;

        var outcome = await world.ProcessAsync();

        var entry = world.Index.Get(_item.Id)!;
        Assert.Equal(ItemOutcome.Skipped, outcome);
        Assert.Equal(ThemeItemState.AutoAssigned, entry.State);
        Assert.Equal(ThemeIndexEntry.BySearch, entry.AssignedBy);
        Assert.Equal(GuessId, entry.ChosenId);
        Assert.Equal(_themePath, entry.ThemePath);
        Assert.Equal(0, entry.Attempts);
        Assert.Contains("current theme stays", entry.LastSkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThemeThatIsAlreadyTheCataloguesIsNotFetchedAgain()
    {
        // A search that found ThemerrDB's video before ThemerrDB listed it: nothing to download,
        // and the theme stops being treated as a guess.
        var world = await World.WithSearchPickAsync(this, chosenId: CatalogueId);
        world.Catalogue.Answer = Listed();

        await world.ProcessAsync();

        Assert.Empty(world.Acquisition.Tried);
        Assert.Equal("ThemerrDB", world.Index.Get(_item.Id)!.AssignedBy);
    }

    [Theory]
    [InlineData(ThemeItemState.Approved, ThemeIndexEntry.ByYou)]
    [InlineData(ThemeItemState.ManualOverride, ThemeIndexEntry.ByYou)]
    [InlineData(ThemeItemState.AutoAssigned, "ThemerrDB")]
    public async Task AThemeAPersonOrACatalogueChoseIsNeverTouched(ThemeItemState state, string assignedBy)
    {
        var world = await World.WithSearchPickAsync(this, state: state, assignedBy: assignedBy);
        world.Catalogue.Answer = Listed();

        await world.ProcessAsync();

        Assert.Equal(0, world.Catalogue.Asked);
        Assert.Empty(world.Acquisition.Tried);
    }

    // ---- who chose the themes already recorded --------------------------------------------

    [Fact]
    public void ACataloguesChoiceIsRecognisedFromItsBreakdown()
    {
        var entry = new ThemeIndexEntry
        {
            State = ThemeItemState.AutoAssigned,
            ThemePath = "/x/theme.mp3",
            ScoreBreakdown =
            {
                "Total: 100.0/100",
                "Provenance: +1.00 x1 = +100.00 (listed in ThemerrDB against this title's own database id, so it was not scored)",
            },
        };

        Assert.Equal("ThemerrDB", ThemeIndexEntry.InferAssignedBy(entry));
    }

    [Fact]
    public void ThePlexArchivesChoiceIsRecognisedByItsFullName()
    {
        var entry = new ThemeIndexEntry
        {
            State = ThemeItemState.AutoAssigned,
            ThemePath = "/x/theme.mp3",
            ScoreBreakdown = { "Provenance: +1.00 x1 = +100.00 (listed in Plex television theme archive against this title's own database id, so it was not scored)" },
        };

        Assert.Equal("Plex television theme archive", ThemeIndexEntry.InferAssignedBy(entry));
    }

    [Fact]
    public void AnAutoAssignedThemeWithoutACatalogueLineWasASearchsGuess()
    {
        var entry = new ThemeIndexEntry
        {
            State = ThemeItemState.AutoAssigned,
            ThemePath = "/x/theme.mp3",
            ScoreBreakdown = { "Total: 91.0/100", "TitleSimilarity: +1.00 x40 = +40.00 (exact)" },
        };

        Assert.Equal(ThemeIndexEntry.BySearch, ThemeIndexEntry.InferAssignedBy(entry));
    }

    [Theory]
    [InlineData(ThemeItemState.Approved)]
    [InlineData(ThemeItemState.ManualOverride)]
    public void APersonsChoiceIsTheirs(ThemeItemState state) =>
        Assert.Equal(ThemeIndexEntry.ByYou, ThemeIndexEntry.InferAssignedBy(new ThemeIndexEntry { State = state, ThemePath = "/x/theme.mp3" }));

    [Fact]
    public void NoThemeNobodyChose() =>
        Assert.Null(ThemeIndexEntry.InferAssignedBy(new ThemeIndexEntry { State = ThemeItemState.NoCandidate }));

    // ---- the pieces ------------------------------------------------------------------------

    private static Candidate Listed() => new()
    {
        Id = CatalogueId,
        Url = "https://www.youtube.com/watch?v=" + CatalogueId,
        Title = "Lucky Strike (ThemerrDB)",
        Channel = "ThemerrDB",
        FoundBy = new SearchQuery("themerrdb:x", 0, "catalogue"),
        IsHydrated = true,
        Provenance = "ThemerrDB",
    };

    private sealed class World
    {
        private readonly CatalogueReplacesSearchPickTests _test;

        private World(CatalogueReplacesSearchPickTests test)
        {
            _test = test;
            Acquisition = new FakeAcquisition(test._root);
            Placement = new FakePlacement(test._root);
        }

        public FakeCatalogue Catalogue { get; } = new();

        public FakeSearch Search { get; } = new();

        public FakeAcquisition Acquisition { get; }

        public FakePlacement Placement { get; }

        public MemoryIndex Index { get; } = new();

        /// <summary>A library set never to replace a theme, holding a theme ThemeForge wrote.</summary>
        public static async Task<World> WithSearchPickAsync(
            CatalogueReplacesSearchPickTests test,
            string chosenId = GuessId,
            ThemeItemState state = ThemeItemState.AutoAssigned,
            string assignedBy = ThemeIndexEntry.BySearch)
        {
            await File.WriteAllBytesAsync(test._themePath, new byte[] { 1, 2, 3, 4 });
            var world = new World(test);
            world.Index.Put(new ThemeIndexEntry
            {
                ItemId = test._item.Id,
                Label = "Lucky Strike (2026)",
                State = state,
                AssignedBy = assignedBy,
                ChosenId = chosenId,
                ChosenTitle = "Lucky Strike - Western Ballad",
                ThemePath = test._themePath,
                Sha256 = await FileHash.ComputeSha256Async(test._themePath, CancellationToken.None),
                DecidedByMatcher = ThemeIndexEntry.CurrentMatcher,
            });
            return world;
        }

        public Task<ItemOutcome> ProcessAsync()
        {
            var configuration = TestData.Config();
            configuration.RequestDelayMs = 0;
            configuration.ResearchComposers = false;

            var orchestrator = new ThemeOrchestrator(
                DispatchProxy.Create<ILibraryManager, RefusingProxy>(),
                new FixedIdentity(_test._identity),
                new QueryPlanner(),
                Search,
                new[] { Catalogue },
                new EmptyThemerrDb(),
                new NoComposers(),
                new NothingScores(),
                new DecisionPolicy(),
                Acquisition,
                Placement,
                Index,
                new NeverReplace(),
                new NullThemeForgeLogger<ThemeOrchestrator>());

            return orchestrator.ProcessItemAsync(_test._item, new RunReport(), configuration, CancellationToken.None);
        }
    }

    private sealed class FixedIdentity : IMediaIdentityResolver
    {
        private readonly MediaIdentity _identity;

        public FixedIdentity(MediaIdentity identity) => _identity = identity;

        public MediaIdentity? Resolve(BaseItem item) => _identity;
    }

    private sealed class FakeCatalogue : IThemeProvenanceSource
    {
        public Candidate? Answer { get; set; }

        public int Asked { get; private set; }

        public string Name => "ThemerrDB";

        public int Order => 0;

        public bool IsEnabled(PluginConfiguration configuration) => true;

        public Task<Candidate?> FindAsync(MediaIdentity identity, PluginConfiguration configuration, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeSearch : ICandidateSource
    {
        public int Asked { get; private set; }

        public string Name => "search";

        public Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, int maxResults, CancellationToken cancellationToken)
        {
            Asked++;
            return Task.FromResult<IReadOnlyList<Candidate>>(Array.Empty<Candidate>());
        }

        public Task<IReadOnlyList<Candidate>> HydrateAsync(IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken) =>
            Task.FromResult(candidates);
    }

    private sealed class FakeAcquisition : IAcquisitionEngine
    {
        private readonly string _root;

        public FakeAcquisition(string root) => _root = root;

        public bool Broken { get; set; }

        public List<string> Tried { get; } = new();

        public Task<AcquiredAudio> AcquireAsync(Candidate candidate, PluginConfiguration configuration, CancellationToken cancellationToken)
        {
            Tried.Add(candidate.Url);
            if (Broken)
            {
                throw new InvalidOperationException("yt-dlp downloaded nothing: This video is unavailable");
            }

            var staging = Path.Combine(_root, "staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var file = Path.Combine(staging, "theme.mp3");
            File.WriteAllBytes(file, new byte[] { 7, 7, 7 });

            return Task.FromResult(new AcquiredAudio
            {
                StagingPath = file,
                DurationSeconds = 90,
                Sha256 = "replacement",
                SizeBytes = 3,
                SourceUrl = candidate.Url,
            });
        }
    }

    private sealed class FakePlacement : IThemePlacementEngine
    {
        private readonly string _root;

        public FakePlacement(string root) => _root = root;

        public List<ResolvedThemePolicy> Policies { get; } = new();

        public (string? Directory, string? Reason) ResolveThemeDirectory(BaseItem item, PluginConfiguration configuration) => (_root, null);

        public bool HasExistingTheme(BaseItem item, PluginConfiguration configuration) => true;

        public Task<PlacementResult> PlaceAsync(BaseItem item, AcquiredAudio audio, PluginConfiguration configuration, ResolvedThemePolicy policy, CancellationToken cancellationToken)
        {
            Policies.Add(policy);
            return Task.FromResult(PlacementResult.Placed(Path.Combine(_root, "theme.mp3")));
        }
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

    private sealed class EmptyThemerrDb : IThemerrDbCatalogue
    {
        public Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ThemerrDbSnapshot());

        public Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken) => GetAsync(cancellationToken);
    }

    private sealed class NeverReplace : ILibraryPolicyResolver
    {
        public ResolvedThemePolicy Resolve(BaseItem item, PluginConfiguration configuration) =>
            new(true, ThemeOverwritePolicy.Never, "Films");

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

    public class RefusingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} was not expected to be called");
    }
}
