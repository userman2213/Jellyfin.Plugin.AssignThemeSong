using System;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers a dry run leaving no trace.
/// </summary>
/// <remarks>
/// An earlier version returned early from the assignment step when the run was a dry run, and did
/// so by marking the item as awaiting review — after the winning candidate and its score had
/// already been recorded. The queue filled with items the run would have assigned outright,
/// showing the full score they earned, sorted above every genuine suggestion, and they survived
/// dry run being switched off. "Would assign" and "a person needs to decide" are different facts
/// and now have different names.
/// </remarks>
public class DryRunTests
{
    [Fact]
    public void WouldAssignIsCountedSeparatelyFromTheReviewQueue()
    {
        var report = new RunReport { WasDryRun = true, Considered = 3 };
        report.Record(ItemOutcome.WouldAssign);
        report.Record(ItemOutcome.WouldAssign);
        report.Record(ItemOutcome.Queued);

        Assert.Equal(2, report.WouldAssign);
        Assert.Equal(1, report.Queued);
        Assert.Equal(0, report.Assigned);
    }

    [Fact]
    public void ADryRunSummarySaysWhatWouldHaveHappened()
    {
        var report = new RunReport { WasDryRun = true, Considered = 1 };
        report.Record(ItemOutcome.WouldAssign);

        var summary = report.ToString();

        Assert.Contains("would be assigned", summary, StringComparison.Ordinal);
        Assert.Contains("nothing was written", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("1 assigned", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARealRunSummaryDoesNot()
    {
        var report = new RunReport { Considered = 1 };
        report.Record(ItemOutcome.Assigned);

        var summary = report.ToString();

        Assert.Contains("1 assigned", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("dry run", summary, StringComparison.Ordinal);
    }
}

/// <summary>
/// Covers clearing the review-queue entries an earlier version's dry run left behind.
/// </summary>
public sealed class DryRunArtefactTests : IDisposable
{
    private readonly string _path =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "themeforge-dryrun-" + Guid.NewGuid().ToString("N") + ".json");

    private JsonThemeIndex NewIndex() => new(NullThemeForgeLogger<JsonThemeIndex>.Instance, _path);

    public void Dispose()
    {
        foreach (var suffix in new[] { string.Empty, ".corrupt", ".tmp" })
        {
            if (System.IO.File.Exists(_path + suffix))
            {
                System.IO.File.Delete(_path + suffix);
            }
        }
    }

    private static ThemeIndexEntry Queued(string label, double score, int attempts) => new()
    {
        ItemId = Guid.NewGuid(),
        StableKey = "tvdb:" + label,
        Label = label,
        Kind = "Series",
        State = ThemeItemState.PendingReview,
        Score = score,
        Attempts = attempts,
    };

    private async System.Threading.Tasks.Task<JsonThemeIndex> RoundTrip(params ThemeIndexEntry[] entries)
    {
        var index = NewIndex();
        foreach (var entry in entries)
        {
            index.Put(entry);
        }

        await index.FlushAsync(System.Threading.CancellationToken.None);

        var reloaded = NewIndex();
        await reloaded.LoadAsync(System.Threading.CancellationToken.None);
        return reloaded;
    }

    [Fact]
    public async System.Threading.Tasks.Task AQueuedItemThatWouldHaveBeenAssignedIsCleared()
    {
        // Awaiting review, never attempted, and scoring above the auto-assign threshold: the
        // decision policy cannot produce that combination, so it can only be a dry-run artefact.
        var artefact = Queued("Firefly", 100, attempts: 0);

        var reloaded = await RoundTrip(artefact);

        Assert.Equal(ThemeItemState.Unprocessed, reloaded.Get(artefact.ItemId)!.State);
        Assert.Empty(reloaded.ReviewQueue());
    }

    [Fact]
    public async System.Threading.Tasks.Task AGenuineMidBandSuggestionIsKept()
    {
        // 58 is between the review and auto-assign thresholds, which is exactly what the decision
        // policy queues. Clearing this would throw away real work.
        var genuine = Queued("The Expanse", 58, attempts: 0);

        var reloaded = await RoundTrip(genuine);

        Assert.Equal(ThemeItemState.PendingReview, reloaded.Get(genuine.ItemId)!.State);
        Assert.Single(reloaded.ReviewQueue());
    }

    [Fact]
    public async System.Threading.Tasks.Task AnItemQueuedBecauseItsAudioWasAmbiguousIsKept()
    {
        // This one scores above the threshold too, but it was downloaded and measured before being
        // held back, so it has an attempt recorded against it.
        var ambiguous = Queued("Battlestar Galactica", 91, attempts: 1);

        var reloaded = await RoundTrip(ambiguous);

        Assert.Equal(ThemeItemState.PendingReview, reloaded.Get(ambiguous.ItemId)!.State);
    }
}
