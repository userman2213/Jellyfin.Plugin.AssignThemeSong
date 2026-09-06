using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>What one item's trip through the pipeline resulted in.</summary>
public enum ItemOutcome
{
    /// <summary>The index or an existing file said there was nothing to do.</summary>
    Skipped = 0,

    /// <summary>A theme was found, downloaded and written.</summary>
    Assigned = 1,

    /// <summary>A plausible candidate was found and is waiting for a human.</summary>
    Queued = 2,

    /// <summary>Nothing scored well enough to use.</summary>
    NoCandidate = 3,

    /// <summary>Something went wrong; the item is eligible for retry.</summary>
    Failed = 4,
}

/// <summary>
/// A summary of one pipeline run, shown on the configuration page and written to the log.
/// </summary>
/// <remarks>
/// A long unattended run over thousands of items produces far too much log output to read.
/// This is what lets a user answer "did that do anything useful?" in one glance, and the
/// per-reason tallies point at what to tune when the answer is no.
/// </remarks>
public sealed class RunReport
{
    /// <summary>Gets or sets when the run started.</summary>
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the run finished.</summary>
    public DateTime? FinishedUtc { get; set; }

    /// <summary>Gets or sets a value indicating whether this was a dry run that wrote nothing.</summary>
    public bool WasDryRun { get; set; }

    /// <summary>Gets or sets a value indicating whether the run was cancelled before finishing.</summary>
    public bool WasCancelled { get; set; }

    /// <summary>Gets or sets how many items were examined.</summary>
    public int Considered { get; set; }

    /// <summary>Gets or sets how many items were left alone.</summary>
    public int Skipped { get; set; }

    /// <summary>Gets or sets how many themes were assigned automatically.</summary>
    public int Assigned { get; set; }

    /// <summary>Gets or sets how many items were added to the review queue.</summary>
    public int Queued { get; set; }

    /// <summary>Gets or sets how many items had no acceptable candidate.</summary>
    public int NoCandidate { get; set; }

    /// <summary>Gets or sets how many items failed outright.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets a fatal error that stopped the whole run, such as yt-dlp being unavailable.</summary>
    public string? FatalError { get; set; }

    /// <summary>Gets the most common reasons items did not get a theme, most frequent first.</summary>
    public Dictionary<string, int> TopReasons { get; } = new(StringComparer.Ordinal);

    /// <summary>Records a reason an item did not get a theme.</summary>
    /// <param name="reason">The reason text.</param>
    public void NoteReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        TopReasons[reason] = TopReasons.TryGetValue(reason, out var count) ? count + 1 : 1;
    }

    /// <summary>Counts an outcome.</summary>
    /// <param name="outcome">What happened to the item.</param>
    public void Record(ItemOutcome outcome)
    {
        switch (outcome)
        {
            case ItemOutcome.Assigned: Assigned++; break;
            case ItemOutcome.Queued: Queued++; break;
            case ItemOutcome.NoCandidate: NoCandidate++; break;
            case ItemOutcome.Failed: Failed++; break;
            default: Skipped++; break;
        }
    }

    /// <summary>Renders the one-line summary written to the log when a run ends.</summary>
    /// <returns>The summary.</returns>
    public override string ToString() => string.Format(
        CultureInfo.InvariantCulture,
        "{0} considered, {1} assigned, {2} queued for review, {3} without a candidate, {4} failed, {5} skipped{6}",
        Considered,
        Assigned,
        Queued,
        NoCandidate,
        Failed,
        Skipped,
        WasDryRun ? " (dry run — nothing was written)" : string.Empty);
}
