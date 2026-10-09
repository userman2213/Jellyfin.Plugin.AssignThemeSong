using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>
/// What a search asked for by hand found, and what the item already has.
/// </summary>
/// <remarks>
/// Carries <see cref="ScoreResult"/> rather than a presentation type because the scores and their
/// reasons are the point: the question behind a manual search is usually "why did it not pick the
/// obvious one?", and the answer is in the breakdown.
/// </remarks>
/// <param name="Success">Whether the search could be run at all.</param>
/// <param name="Message">What to tell the user, when it could not, or a note about the results.</param>
/// <param name="Query">The words that were searched for, so the box can show them.</param>
/// <param name="CurrentId">The source id of the theme this item has now, if any.</param>
/// <param name="CurrentTitle">The title of the theme this item has now, if any.</param>
/// <param name="CurrentUrl">Where the theme this item has now came from, if any.</param>
/// <param name="Results">Every candidate the search found, best first, vetoed ones included.</param>
public sealed record ManualSearchResult(
    bool Success,
    string Message,
    string Query,
    string? CurrentId,
    string? CurrentTitle,
    string? CurrentUrl,
    IReadOnlyList<ScoreResult> Results)
{
    /// <summary>Creates a result for a search that could not be run.</summary>
    /// <param name="message">Why not.</param>
    /// <returns>The result.</returns>
    public static ManualSearchResult Failed(string message) =>
        new(false, message, string.Empty, null, null, null, Array.Empty<ScoreResult>());
}
