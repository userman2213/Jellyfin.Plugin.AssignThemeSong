using System;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>Pins that a run's summary says why items were skipped, and when it is not finished.</summary>
public class RunReportTests
{
    [Fact]
    public void SkipsAreCountedByReasonWhateverTimeTheyWaitUntil()
    {
        var report = new RunReport();
        report.NoteSkip("the retry backoff runs until 2026-11-28 03:00:00Z");
        report.NoteSkip("the retry backoff runs until 2026-11-29 14:30:12Z");
        report.NoteSkip("it already has a theme assigned by ThemeForge");

        var reasons = report.Reasons();

        Assert.Contains(reasons, pair => pair.Key == "skipped: the retry backoff runs for now" && pair.Value == 2);
        Assert.Contains(reasons, pair => pair.Key == "skipped: it already has a theme assigned by ThemeForge" && pair.Value == 1);
    }

    [Fact]
    public void ARunStillGoingSaysSo()
    {
        var report = new RunReport { Considered = 963 };
        report.Record(ItemOutcome.Assigned);
        report.Record(ItemOutcome.Skipped);

        Assert.StartsWith("still running, 2 of 963 done so far: ", report.ToString(), StringComparison.Ordinal);

        report.FinishedUtc = DateTime.UtcNow;
        Assert.StartsWith("963 considered", report.ToString(), StringComparison.Ordinal);
    }
}
