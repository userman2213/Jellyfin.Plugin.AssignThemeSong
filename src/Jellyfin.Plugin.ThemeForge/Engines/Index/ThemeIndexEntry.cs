using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThemeForge.Engines.Index;

/// <summary>
/// A candidate that was considered but not chosen, kept so the review queue can show the
/// runners-up and so a re-run does not silently repeat a rejected suggestion.
/// </summary>
public sealed class RejectedCandidateRecord
{
    /// <summary>Gets or sets the source video id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the candidate title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the watch URL.</summary>
    public string Url { get; set; } = string.Empty;

    /// <summary>Gets or sets the score it achieved.</summary>
    public double Score { get; set; }

    /// <summary>Gets or sets why it lost, in one line.</summary>
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// The complete record of what the pipeline decided about one library item, and why.
/// This is the plugin's memory: without it every run would start from nothing and
/// re-litigate decisions a human already settled.
/// </summary>
public sealed class ThemeIndexEntry
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the provider-anchored key that survives a library rebuild.</summary>
    public string StableKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's display label at the time of processing.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the item kind, as a string so the file stays readable.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets where this item stands.</summary>
    public ThemeItemState State { get; set; } = ThemeItemState.Unprocessed;

    /// <summary>Gets or sets the chosen candidate's video id.</summary>
    public string? ChosenId { get; set; }

    /// <summary>Gets or sets the chosen candidate's title.</summary>
    public string? ChosenTitle { get; set; }

    /// <summary>Gets or sets the chosen candidate's URL.</summary>
    public string? ChosenUrl { get; set; }

    /// <summary>Gets or sets the chosen candidate's uploading channel.</summary>
    public string? ChosenChannel { get; set; }

    /// <summary>Gets or sets the score the chosen candidate achieved.</summary>
    public double? Score { get; set; }

    /// <summary>Gets or sets the full explainable breakdown, one formatted signal per line.</summary>
    public List<string> ScoreBreakdown { get; set; } = new();

    /// <summary>Gets or sets the runners-up.</summary>
    public List<RejectedCandidateRecord> Rejected { get; set; } = new();

    /// <summary>Gets or sets where the theme was written.</summary>
    public string? ThemePath { get; set; }

    /// <summary>Gets or sets the SHA-256 of the written file, to detect later external edits.</summary>
    public string? Sha256 { get; set; }

    /// <summary>Gets or sets the integrated loudness measured after normalization.</summary>
    public double? LoudnessLufs { get; set; }

    /// <summary>Gets or sets the theme's duration in seconds.</summary>
    public double? DurationSeconds { get; set; }

    /// <summary>Gets or sets when this entry was first created.</summary>
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when this entry last changed.</summary>
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets how many times acquisition has been attempted.</summary>
    public int Attempts { get; set; }

    /// <summary>Gets or sets the last error, when the state is <see cref="ThemeItemState.Failed"/>.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the earliest time a retry should be attempted.</summary>
    public DateTime? NextRetryUtc { get; set; }

    /// <summary>
    /// Gets a value indicating whether an automated run may touch this item. A human decision
    /// always outranks the pipeline.
    /// </summary>
    public bool IsSettledByHuman =>
        State is ThemeItemState.Locked or ThemeItemState.ManualOverride or ThemeItemState.Rejected;
}
