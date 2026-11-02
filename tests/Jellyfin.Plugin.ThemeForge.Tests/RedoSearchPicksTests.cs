using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Api;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins the clean-up for themes the title searches of earlier versions chose: only a search's
/// theme goes, and only while the file is still ThemeForge's.
/// </summary>
public sealed class RedoSearchPicksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-redo-" + Guid.NewGuid().ToString("N"));
    private readonly MemoryIndex _index = new();

    public RedoSearchPicksTests() => Directory.CreateDirectory(_root);

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
    public async Task DeletesASearchsThemeAndLooksAtTheTitleAgain()
    {
        var entry = await Written("Lucky Strike", ThemeItemState.AutoAssigned, ThemeIndexEntry.BySearch);

        var result = await Redo();

        Assert.Equal(1, result.Deleted);
        Assert.False(File.Exists(Path.Combine(_root, "Lucky Strike", "theme.mp3")));
        Assert.Equal(ThemeItemState.Unprocessed, entry.State);
        Assert.Null(entry.ThemePath);
        Assert.Null(entry.Sha256);
        Assert.Null(entry.AssignedBy);
        Assert.Null(entry.ChosenUrl);
        Assert.Empty(entry.ScoreBreakdown);
        Assert.Equal(0, entry.Attempts);
    }

    [Fact]
    public async Task KeepsAThemeChangedByHand()
    {
        var entry = await Written("Alien", ThemeItemState.AutoAssigned, ThemeIndexEntry.BySearch);
        await File.WriteAllTextAsync(entry.ThemePath!, "something the owner put there");

        var result = await Redo();

        Assert.Equal(0, result.Deleted);
        Assert.Equal(1, result.SkippedModified);
        Assert.True(File.Exists(entry.ThemePath));
        Assert.Equal(ThemeItemState.AutoAssigned, entry.State);
    }

    [Theory]
    [InlineData(ThemeItemState.AutoAssigned, "ThemerrDB")]
    [InlineData(ThemeItemState.AutoAssigned, "Plex television theme archive")]
    [InlineData(ThemeItemState.Approved, ThemeIndexEntry.ByYou)]
    [InlineData(ThemeItemState.ManualOverride, ThemeIndexEntry.ByYou)]
    [InlineData(ThemeItemState.Locked, ThemeIndexEntry.BySearch)]
    public async Task LeavesEveryOtherThemeAlone(ThemeItemState state, string assignedBy)
    {
        var entry = await Written("Firefly", state, assignedBy);

        var result = await Redo();

        Assert.Equal(0, result.Deleted);
        Assert.True(File.Exists(entry.ThemePath));
        Assert.Equal(state, entry.State);
        Assert.Equal(assignedBy, entry.AssignedBy);
    }

    [Fact]
    public async Task ForgetsAThemeWhoseFileIsAlreadyGone()
    {
        var entry = await Written("Predator", ThemeItemState.AutoAssigned, ThemeIndexEntry.BySearch);
        File.Delete(entry.ThemePath!);

        var result = await Redo();

        Assert.Equal(1, result.AlreadyMissing);
        Assert.Equal(ThemeItemState.Unprocessed, entry.State);
    }

    [Fact]
    public async Task DoesNothingWithoutConfirmation()
    {
        var entry = await Written("Titanic", ThemeItemState.AutoAssigned, ThemeIndexEntry.BySearch);

        var response = await Controller(running: false).RedoSearchPicks(confirm: false, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(response.Result);
        Assert.True(File.Exists(entry.ThemePath));
    }

    [Fact]
    public async Task WaitsForARunToFinish()
    {
        var entry = await Written("Skyfall", ThemeItemState.AutoAssigned, ThemeIndexEntry.BySearch);

        var response = await Controller(running: true).RedoSearchPicks(confirm: true, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(response.Result);
        Assert.True(File.Exists(entry.ThemePath));
    }

    private async Task<ThemeRemovalResult> Redo()
    {
        var response = await Controller(running: false).RedoSearchPicks(confirm: true, CancellationToken.None);
        return Assert.IsType<ThemeRemovalResult>(response.Value);
    }

    private async Task<ThemeIndexEntry> Written(string title, ThemeItemState state, string assignedBy)
    {
        var folder = Path.Combine(_root, title);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "theme.mp3");
        await File.WriteAllBytesAsync(path, new byte[] { 1, 2, 3, (byte)title.Length });

        var entry = new ThemeIndexEntry
        {
            ItemId = Guid.NewGuid(),
            Label = title,
            State = state,
            AssignedBy = assignedBy,
            ThemePath = path,
            Sha256 = await FileHash.ComputeSha256Async(path, CancellationToken.None),
            ChosenUrl = "https://www.youtube.com/watch?v=chosen00001",
            ChosenTitle = title + " Theme Song",
            ScoreBreakdown = new List<string> { "Something: +1" },
            Attempts = 1,
        };

        _index.Put(entry);
        return entry;
    }

    private ThemeForgeController Controller(bool running)
    {
        var orchestrator = DispatchProxy.Create<IThemeOrchestrator, RunningProxy>();
        ((RunningProxy)(object)orchestrator).Running = running;

        return new ThemeForgeController(
            orchestrator,
            _index,
            Refuse<ILibraryManager>(),
            Refuse<IThemePlacementEngine>(),
            Refuse<IMediaIdentityResolver>(),
            Refuse<IToolProvisioner>(),
            Refuse<ILibraryPolicyResolver>(),
            Refuse<IThemerrDbCatalogue>(),
            new NullThemeForgeLogger<ThemeForgeController>());
    }

    private static T Refuse<T>()
        where T : class => DispatchProxy.Create<T, RefusingProxy>();

    public class RefusingProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            throw new NotSupportedException($"{targetMethod?.Name} was not expected to be called");
    }

    /// <summary>Answers whether a run is in progress, and nothing else.</summary>
    public class RunningProxy : DispatchProxy
    {
        public bool Running { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "get_IsRunning"
                ? Running
                : throw new NotSupportedException($"{targetMethod?.Name} was not expected to be called");
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
