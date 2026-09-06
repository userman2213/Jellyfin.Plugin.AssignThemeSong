using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>A runner-up shown alongside a review-queue entry.</summary>
/// <param name="Id">Source video id.</param>
/// <param name="Title">Candidate title.</param>
/// <param name="Url">Watch URL.</param>
/// <param name="Score">Score achieved.</param>
/// <param name="Reason">Why it was not chosen.</param>
public sealed record AlternateDto(string Id, string Title, string Url, double Score, string Reason);

/// <summary>One item awaiting a decision, with everything needed to make it.</summary>
public sealed class ReviewItemDto
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item's display label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is a Movie or a Series.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the proposed candidate's title.</summary>
    public string? CandidateTitle { get; set; }

    /// <summary>Gets or sets the proposed candidate's URL.</summary>
    public string? CandidateUrl { get; set; }

    /// <summary>Gets or sets the proposed candidate's channel.</summary>
    public string? CandidateChannel { get; set; }

    /// <summary>Gets or sets the score it achieved.</summary>
    public double? Score { get; set; }

    /// <summary>Gets or sets the explainable breakdown of that score.</summary>
    public IReadOnlyList<string> ScoreBreakdown { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the runners-up.</summary>
    public IReadOnlyList<AlternateDto> Alternates { get; set; } = Array.Empty<AlternateDto>();
}

/// <summary>A row in the library overview.</summary>
public sealed class LibraryRowDto
{
    /// <summary>Gets or sets the Jellyfin item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the item's display label.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this is a Movie or a Series.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets the item's state in the index.</summary>
    public string State { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether a theme file exists on disk.</summary>
    public bool HasThemeFile { get; set; }

    /// <summary>Gets or sets the assigned theme's title.</summary>
    public string? ThemeTitle { get; set; }

    /// <summary>Gets or sets the assigned theme's source URL.</summary>
    public string? ThemeUrl { get; set; }

    /// <summary>Gets or sets the score of the assigned theme.</summary>
    public double? Score { get; set; }

    /// <summary>Gets or sets the measured loudness of the assigned theme.</summary>
    public double? LoudnessLufs { get; set; }

    /// <summary>Gets or sets the last error recorded for this item.</summary>
    public string? LastError { get; set; }
}

/// <summary>The plugin's current state, for the status panel.</summary>
public sealed class StatusDto
{
    /// <summary>Gets or sets a value indicating whether a run is in progress.</summary>
    public bool IsRunning { get; set; }

    /// <summary>Gets or sets the yt-dlp version in use, or the reason it is unavailable.</summary>
    public string YtDlpVersion { get; set; } = "not checked";

    /// <summary>Gets or sets the ffmpeg path in use.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a description of any tooling problem.</summary>
    public string? ToolProblem { get; set; }

    /// <summary>
    /// Gets or sets a warning when yt-dlp is old enough to be the cause of failing downloads.
    /// </summary>
    /// <remarks>
    /// Worth its own field because the symptom points somewhere else entirely: search and
    /// metadata keep working and only the download fails, with HTTP 403.
    /// </remarks>
    public string? ToolWarning { get; set; }

    /// <summary>Gets or sets how many items are awaiting review.</summary>
    public int PendingReview { get; set; }

    /// <summary>Gets or sets how many themes ThemeForge has assigned.</summary>
    public int Assigned { get; set; }

    /// <summary>Gets or sets how many items are in a failed state.</summary>
    public int Failed { get; set; }

    /// <summary>Gets or sets how many index entries exist in total.</summary>
    public int Indexed { get; set; }

    /// <summary>Gets or sets the summary of the last run.</summary>
    public string? LastRunSummary { get; set; }

    /// <summary>Gets or sets when the last run finished.</summary>
    public DateTime? LastRunFinishedUtc { get; set; }

    /// <summary>Gets or sets the most common reasons items did not get a theme in the last run.</summary>
    public IReadOnlyList<string> LastRunReasons { get; set; } = Array.Empty<string>();
}

/// <summary>A request to set an item's theme from a specific URL.</summary>
public sealed class AssignRequest
{
    /// <summary>Gets or sets the URL to download.</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>The result of an operation the UI reports back to the user.</summary>
/// <param name="Success">Whether it worked.</param>
/// <param name="Message">What to tell the user.</param>
public sealed record OperationResult(bool Success, string Message);

/// <summary>The outcome of removing every theme ThemeForge wrote.</summary>
public sealed class ThemeRemovalResult
{
    /// <summary>Gets or sets how many theme files were deleted.</summary>
    public int Deleted { get; set; }

    /// <summary>
    /// Gets or sets how many were left alone because their contents no longer match what
    /// ThemeForge wrote, meaning the user replaced them by hand.
    /// </summary>
    public int SkippedModified { get; set; }

    /// <summary>Gets or sets how many were recorded but already gone from disk.</summary>
    public int AlreadyMissing { get; set; }

    /// <summary>Gets or sets how many could not be deleted, with the reason.</summary>
    public List<string> Failures { get; set; } = new();

    /// <summary>Gets a sentence describing the outcome, for the confirmation dialog.</summary>
    public string Summary =>
        $"Deleted {Deleted}; kept {SkippedModified} that had been edited; {AlreadyMissing} were already gone.";
}
