using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class ThemeIndexTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), "themeforge-index-" + Guid.NewGuid().ToString("N") + ".json");

    private JsonThemeIndex NewIndex() => new(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, ".corrupt", ".tmp" })
        {
            if (File.Exists(_path + suffix))
            {
                File.Delete(_path + suffix);
            }
        }

        GC.SuppressFinalize(this);
    }

    private static ThemeIndexEntry Entry(string label, ThemeItemState state = ThemeItemState.AutoAssigned, string? videoId = null) => new()
    {
        ItemId = Guid.NewGuid(),
        StableKey = "tvdb:" + label,
        Label = label,
        Kind = "Series",
        State = state,
        ChosenId = videoId,
        Score = 88,
    };

    [Fact]
    public async Task EntriesSurviveASaveAndReload()
    {
        var index = NewIndex();
        var entry = Entry("Firefly");
        index.Put(entry);
        await index.FlushAsync(CancellationToken.None);

        var reloaded = NewIndex();
        await reloaded.LoadAsync(CancellationToken.None);

        var found = reloaded.Get(entry.ItemId);
        Assert.NotNull(found);
        Assert.Equal("Firefly", found!.Label);
        Assert.Equal(ThemeItemState.AutoAssigned, found.State);
    }

    [Fact]
    public async Task ResolveReKeysAnEntryWhenTheLibraryRegeneratesItemIds()
    {
        // Removing and re-adding a library gives every item a new id. Losing the history would
        // mean re-offering candidates a human already rejected.
        var index = NewIndex();
        var original = Entry("Firefly", ThemeItemState.Rejected);
        index.Put(original);

        // Captured up front: Resolve re-keys the stored entry in place, so reading the id back
        // off the same object afterwards would just return the new one.
        var oldItemId = original.ItemId;
        var newItemId = Guid.NewGuid();

        var resolved = index.Resolve(newItemId, original.StableKey);

        Assert.NotNull(resolved);
        Assert.Equal(ThemeItemState.Rejected, resolved!.State);
        Assert.Equal(newItemId, resolved.ItemId);
        Assert.Null(index.Get(oldItemId));
        await Task.CompletedTask;
    }

    [Fact]
    public void ResolveReturnsNullWhenNothingMatches() =>
        Assert.Null(NewIndex().Resolve(Guid.NewGuid(), "tvdb:unknown"));

    [Fact]
    public void ReviewQueueIsOrderedByScoreAndExcludesEverythingElse()
    {
        var index = NewIndex();

        var low = Entry("Low", ThemeItemState.PendingReview);
        low.Score = 50;
        var high = Entry("High", ThemeItemState.PendingReview);
        high.Score = 70;

        index.Put(low);
        index.Put(high);
        index.Put(Entry("Assigned"));

        var queue = index.ReviewQueue();

        Assert.Equal(2, queue.Count);
        Assert.Equal("High", queue[0].Label);
    }

    [Fact]
    public void FindAssignmentOwnerSpotsAVideoUsedByAnotherItem()
    {
        var index = NewIndex();
        var owner = Entry("Some Show", ThemeItemState.AutoAssigned, videoId: "abc12345678");
        index.Put(owner);

        Assert.Equal("Some Show", index.FindAssignmentOwner("abc12345678", Guid.NewGuid()));

        // An item is never a duplicate of itself.
        Assert.Null(index.FindAssignmentOwner("abc12345678", owner.ItemId));
    }

    [Fact]
    public void FindAssignmentOwnerIgnoresItemsThatNeverGotTheTheme()
    {
        var index = NewIndex();
        index.Put(Entry("Rejected Show", ThemeItemState.Rejected, videoId: "abc12345678"));

        Assert.Null(index.FindAssignmentOwner("abc12345678", Guid.NewGuid()));
    }

    [Fact]
    public async Task ACorruptIndexIsSetAsideRatherThanStoppingThePlugin()
    {
        await File.WriteAllTextAsync(_path, "{ this is not valid json");

        var index = NewIndex();
        await index.LoadAsync(CancellationToken.None);

        Assert.Empty(index.All());
        Assert.True(File.Exists(_path + ".corrupt"), "the unreadable index should be kept for inspection");
    }

    [Fact]
    public async Task LoadingWithNoExistingFileStartsEmpty()
    {
        var index = NewIndex();
        await index.LoadAsync(CancellationToken.None);
        Assert.Empty(index.All());
    }

    [Fact]
    public async Task FlushDoesNothingWhenNothingChanged()
    {
        var index = NewIndex();
        await index.FlushAsync(CancellationToken.None);
        Assert.False(File.Exists(_path));
    }

    [Theory]
    [InlineData(ThemeItemState.Locked, true)]
    [InlineData(ThemeItemState.Rejected, true)]
    [InlineData(ThemeItemState.AutoAssigned, false)]
    [InlineData(ThemeItemState.Failed, false)]
    [InlineData(ThemeItemState.Unprocessed, false)]
    public void HumanDecisionsAreRecognisedAsSettled(ThemeItemState state, bool expected) =>
        Assert.Equal(expected, Entry("X", state).IsSettledByHuman);

    [Fact]
    public void ManualOverrideCountsOnlyWhenAFileWasActuallyAssigned()
    {
        // ManualOverride without a recorded path is the footprint an older version left when the
        // scanner merely noticed an existing theme. Treating that as a human decision is what made
        // the library overwrite rule unreachable, so it must not count.
        var latched = Entry("X", ThemeItemState.ManualOverride);
        Assert.Null(latched.ThemePath);
        Assert.False(latched.IsSettledByHuman);

        latched.ThemePath = "/media/X/theme.mp3";
        Assert.True(latched.IsSettledByHuman);
    }

    [Fact]
    public async Task RemoveDeletesAnEntry()
    {
        var index = NewIndex();
        var entry = Entry("Gone");
        index.Put(entry);

        Assert.True(index.Remove(entry.ItemId));
        Assert.False(index.Remove(entry.ItemId));
        Assert.Null(index.Get(entry.ItemId));
        await Task.CompletedTask;
    }
}


/// <summary>
/// Covers the stamp that says which matcher produced a decision.
/// </summary>
/// <remarks>
/// A decision carries no evidence of the code that made it, so an upgrade that changes how
/// matches are chosen leaves a library full of records that look exactly as trustworthy as new
/// ones. The stamp is what lets the plugin say "these came from the old matcher" instead of
/// keeping them silently.
/// </remarks>
public class MatcherGenerationTests
{
    private static ThemeIndexEntry Entry(ThemeItemState state, int matcher) => new()
    {
        ItemId = System.Guid.NewGuid(),
        Label = "Something",
        State = state,
        DecidedByMatcher = matcher,
    };

    [Theory]
    [InlineData(ThemeItemState.AutoAssigned)]
    [InlineData(ThemeItemState.PendingReview)]
    [InlineData(ThemeItemState.Failed)]
    public void ADecisionFromAnOlderMatcherIsStale(ThemeItemState state) =>
        Assert.True(Entry(state, 0).IsStale);

    [Theory]
    [InlineData(ThemeItemState.AutoAssigned)]
    [InlineData(ThemeItemState.PendingReview)]
    [InlineData(ThemeItemState.Failed)]
    public void ADecisionFromTheCurrentMatcherIsNot(ThemeItemState state) =>
        Assert.False(Entry(state, ThemeIndexEntry.CurrentMatcher).IsStale);

    [Theory]
    [InlineData(ThemeItemState.Locked)]
    [InlineData(ThemeItemState.Rejected)]
    [InlineData(ThemeItemState.Approved)]
    [InlineData(ThemeItemState.ManualOverride)]
    [InlineData(ThemeItemState.Unprocessed)]
    public void AHumanDecisionIsNeverStale(ThemeItemState state)
    {
        // A person rejecting a candidate is still right about that candidate, whatever chose it.
        Assert.False(Entry(state, 0).IsStale);
    }
}
