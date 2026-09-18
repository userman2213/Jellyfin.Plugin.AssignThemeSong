using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Guards the bug that made the per-library overwrite rule impossible to apply.
/// </summary>
/// <remarks>
/// An earlier version wrote <see cref="ThemeItemState.ManualOverride"/> whenever the scanner
/// merely noticed a theme file it had not written. That state is tested before the library policy
/// is read, so once latched an item could never be reconsidered however the rule changed. On a
/// real 772-item library it skipped 767 of them.
/// </remarks>
public class OverwritePolicyReachabilityTests : IDisposable
{
    private readonly string _path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "themeforge-latch-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        if (System.IO.File.Exists(_path))
        {
            System.IO.File.Delete(_path);
        }

        GC.SuppressFinalize(this);
    }

    private static ThemeIndexEntry Entry(ThemeItemState state, string? themePath) => new()
    {
        ItemId = Guid.NewGuid(),
        StableKey = "tvdb:" + Guid.NewGuid().ToString("N"),
        Label = "Some Show",
        Kind = "Series",
        State = state,
        ThemePath = themePath,
    };

    [Fact]
    public void AScannerLatchIsNotTreatedAsAHumanDecision()
    {
        // ManualOverride with no recorded path is the scanner's footprint, not a person's.
        var latched = Entry(ThemeItemState.ManualOverride, themePath: null);
        Assert.False(latched.IsSettledByHuman);
    }

    [Fact]
    public void ARealManualAssignmentIsStillRespected()
    {
        // The Assign endpoint always records where it wrote the file.
        var assigned = Entry(ThemeItemState.ManualOverride, themePath: "/media/Show/theme.mp3");
        Assert.True(assigned.IsSettledByHuman);
    }

    [Theory]
    [InlineData(ThemeItemState.Locked)]
    [InlineData(ThemeItemState.Rejected)]
    public void OtherHumanDecisionsAreUnaffected(ThemeItemState state) =>
        Assert.True(Entry(state, themePath: null).IsSettledByHuman);

    [Fact]
    public async Task LoadingReleasesLatchedEntriesButKeepsRealAssignments()
    {
        var index = new JsonThemeIndex(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);

        var latched = Entry(ThemeItemState.ManualOverride, themePath: null);
        var assigned = Entry(ThemeItemState.ManualOverride, themePath: "/media/Other/theme.mp3");
        var locked = Entry(ThemeItemState.Locked, themePath: "/media/Third/theme.mp3");

        index.Put(latched);
        index.Put(assigned);
        index.Put(locked);
        await index.FlushAsync(CancellationToken.None);

        var reloaded = new JsonThemeIndex(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);
        await reloaded.LoadAsync(CancellationToken.None);

        Assert.Equal(ThemeItemState.Unprocessed, reloaded.Get(latched.ItemId)!.State);
        Assert.Equal(ThemeItemState.ManualOverride, reloaded.Get(assigned.ItemId)!.State);
        Assert.Equal(ThemeItemState.Locked, reloaded.Get(locked.ItemId)!.State);
    }

    [Fact]
    public async Task TheRepairIsPersistedRatherThanRedoneEveryLoad()
    {
        var index = new JsonThemeIndex(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);
        index.Put(Entry(ThemeItemState.ManualOverride, themePath: null));
        await index.FlushAsync(CancellationToken.None);

        var first = new JsonThemeIndex(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);
        await first.LoadAsync(CancellationToken.None);
        await first.FlushAsync(CancellationToken.None);

        var second = new JsonThemeIndex(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);
        await second.LoadAsync(CancellationToken.None);

        Assert.All(second.All(), e => Assert.NotEqual(ThemeItemState.ManualOverride, e.State));
    }
}
