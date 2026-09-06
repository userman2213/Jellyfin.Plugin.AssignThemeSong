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

    /// <summary>Gets or sets why the last run left this item alone, for the diagnostics view.</summary>
    /// <remarks>
    /// The orchestrator computes this sentence anyway and previously discarded it outside the log.
    /// Recording it is what lets a user see why an item was skipped without reading the log, which
    /// is the only way to tell "the rule did not apply" from "the rule applied and said no".
    /// </remarks>
    public string? LastSkipReason { get; set; }

    /// <summary>Gets or sets when <see cref="LastSkipReason"/> was recorded.</summary>
    public DateTime? LastSkipUtc { get; set; }

    /// <summary>
    /// Gets or sets the music-versus-speech measure taken on the downloaded audio.
    /// </summary>
    /// <remarks>
    /// Recorded whatever the verdict was, including when the file was accepted. The two
    /// populations are separated by about 0.6, and that gap will be crossed eventually across a
    /// library of a few hundred titles; having the number next to each assignment is what turns
    /// "is the threshold right?" into a question the data can answer.
    /// </remarks>
    public double? BandDiffStd { get; set; }

    /// <summary>
    /// Gets a value indicating whether an automated run may touch this item, because a person
    /// decided it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately narrow. This must only be true when a human really did act — assigned a theme
    /// by hand, locked the item, or rejected every suggestion. It previously also became true when
    /// the scanner merely <em>noticed</em> a theme file it had not written, latching the item to
    /// <see cref="ThemeItemState.ManualOverride"/> permanently. Since this is tested before the
    /// library's overwrite policy is consulted, that made the policy unreachable: changing a
    /// library to "replace any theme" could never affect an item that had already been latched.
    /// </para>
    /// <para>
    /// "A theme file exists on disk" is an observation about the world right now, not a decision,
    /// and it is re-evaluated on every run against the current policy instead of being stored.
    /// </para>
    /// </remarks>
    public bool IsSettledByHuman =>
        State is ThemeItemState.Locked or ThemeItemState.Rejected
        || (State == ThemeItemState.ManualOverride && ThemePath is not null);
}
