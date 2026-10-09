using System;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>Covers the run saying where its themes came from.</summary>
public class CoverageReportTests
{
    [Fact]
    public void TheSummaryNamesEachSource()
    {
        var report = new RunReport { Considered = 3 };
        report.Record(ItemOutcome.Assigned);
        report.NoteAssignedBy("ThemerrDB");
        report.Record(ItemOutcome.Assigned);
        report.NoteAssignedBy("ThemerrDB");
        report.Record(ItemOutcome.Assigned);
        report.NoteAssignedBy("search");

        var summary = report.ToString();

        Assert.Contains("3 assigned (2 via ThemerrDB, 1 via search)", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ARunThatAssignedNothingSaysNothingAboutSources()
    {
        var report = new RunReport { Considered = 1 };
        report.Record(ItemOutcome.NoCandidate);

        Assert.DoesNotContain("via", report.ToString(), StringComparison.Ordinal);
    }
}
