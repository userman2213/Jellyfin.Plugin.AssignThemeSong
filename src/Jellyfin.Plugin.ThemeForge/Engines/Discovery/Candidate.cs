using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// A single possible theme song, as reported by a candidate source. Fields mirror what
/// yt-dlp exposes; anything a source cannot supply is left null and the scoring rules
/// that depend on it abstain rather than guess.
/// </summary>
public sealed record Candidate
{
    /// <summary>Gets the source-specific id (for YouTube, the 11-character video id).</summary>
    public required string Id { get; init; }

    /// <summary>Gets the watch URL handed to the acquisition engine.</summary>
    public required string Url { get; init; }

    /// <summary>Gets the candidate's title.</summary>
    public required string Title { get; init; }

    /// <summary>Gets the uploading channel's display name.</summary>
    public string? Channel { get; init; }

    /// <summary>Gets the uploading channel's stable id.</summary>
    public string? ChannelId { get; init; }

    /// <summary>Gets the duration in seconds, when known.</summary>
    public double? DurationSeconds { get; init; }

    /// <summary>Gets the view count, when known.</summary>
    public long? ViewCount { get; init; }

    /// <summary>Gets the upload date, when known.</summary>
    public DateTime? UploadDate { get; init; }

    /// <summary>Gets the description text, used for keyword signals.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the uploader-supplied tags.</summary>
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();

    /// <summary>Gets a value indicating whether this is a live stream, which is never a theme.</summary>
    public bool IsLive { get; init; }

    /// <summary>Gets yt-dlp's availability string ("public", "private", "needs_auth", ...).</summary>
    public string? Availability { get; init; }

    /// <summary>Gets the query that surfaced this candidate.</summary>
    public required Query.SearchQuery FoundBy { get; init; }

    /// <summary>
    /// Gets a value indicating whether the full metadata pass has run. Flat search results carry
    /// only a title and id; rules needing duration or view count abstain until this is true.
    /// </summary>
    public bool IsHydrated { get; init; }

    /// <summary>Gets the title and description as one lower-cased haystack for keyword matching.</summary>
    public string SearchableText =>
        string.Join(' ', Title, Channel ?? string.Empty, Description ?? string.Empty, string.Join(' ', Tags))
            .ToLowerInvariant();
}
