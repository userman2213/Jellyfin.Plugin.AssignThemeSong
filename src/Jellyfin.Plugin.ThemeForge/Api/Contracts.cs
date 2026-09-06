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

    /// <summary>
    /// Gets or sets why the last run passed this item over, in the pipeline's own words.
    /// </summary>
    /// <remarks>
    /// A run summary can say 767 items were skipped without saying why any one of them was, which
    /// is no help at all when the question is whether a setting was even consulted.
    /// </remarks>
    public string? SkipReason { get; set; }

    /// <summary>
    /// Gets or sets the music-versus-speech measure taken on the downloaded audio.
    /// </summary>
    /// <remarks>
    /// Shown for every item, accepted or not. The two populations are about 0.6 apart, so seeing
    /// where a library's own assignments actually fall is the only way to tell whether the
    /// threshold is set sensibly for it.
    /// </remarks>
    public double? BandDiffStd { get; set; }
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

    /// <summary>
    /// Gets or sets anything wrong with the settings themselves, such as a library rule that
    /// matches no library.
    /// </summary>
    /// <remarks>
    /// Repeated from the Diagnostics tab because this is the panel people look at when something
    /// has not happened, and a rule the engine never sees is indistinguishable from a rule that
    /// did nothing.
    /// </remarks>
    public IReadOnlyList<string> ConfigurationProblems { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Gets or sets how many recorded decisions were made by a matcher no longer in use.
    /// </summary>
    /// <remarks>
    /// Surfaced rather than cleared automatically. Throwing away someone's whole history on an
    /// upgrade is not a decision a plugin should make for them, but leaving it invisible means a
    /// library carries assignments from a matcher that is gone and nothing ever says so.
    /// </remarks>
    public int StaleDecisions { get; set; }

    /// <summary>Gets or sets a value indicating whether the last run wrote nothing.</summary>
    public bool LastRunWasDryRun { get; set; }

    /// <summary>Gets or sets what the local ThemerrDB copy holds, and how old it is.</summary>
    public CatalogueStatus Themerr { get; set; } = new();
}

/// <summary>What the local copy of a catalogue holds.</summary>
/// <remarks>
/// Reported because the difference between "the database has nothing for this title" and "we have
/// never read the database" is invisible otherwise, and it is the difference between a scan that
/// costs no requests for a miss and one that costs hundreds.
/// </remarks>
public sealed class CatalogueStatus
{
    /// <summary>Gets or sets how many films are listed.</summary>
    public int Movies { get; set; }

    /// <summary>Gets or sets how many shows are listed.</summary>
    public int Shows { get; set; }

    /// <summary>Gets or sets how many film collections are listed.</summary>
    public int Collections { get; set; }

    /// <summary>Gets or sets when it was last read, or null if it never has been.</summary>
    public DateTime? UpdatedUtc { get; set; }

    /// <summary>Gets or sets how many hours old it is.</summary>
    public double? AgeHours { get; set; }
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

/// <summary>What a fresh start actually did.</summary>
public sealed class ResetResult
{
    /// <summary>Gets or sets how many theme files were deleted.</summary>
    public int ThemesDeleted { get; set; }

    /// <summary>Gets or sets how many theme files were left alone because they had been changed by hand.</summary>
    public int ThemesKept { get; set; }

    /// <summary>Gets or sets how many recorded decisions were discarded.</summary>
    public int DecisionsCleared { get; set; }

    /// <summary>Gets or sets a sentence describing the outcome.</summary>
    public string Summary { get; set; } = string.Empty;
}

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
