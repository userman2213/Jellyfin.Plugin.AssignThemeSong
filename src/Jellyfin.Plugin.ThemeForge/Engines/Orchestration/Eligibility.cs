using System;
using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>What may be done for an item on this run.</summary>
/// <param name="StopReason">
/// When set, nothing runs for the item — not even the catalogue. Only a human's decision or the
/// library's overwrite rule produce this, because those decide whether any theme may be written.
/// </param>
/// <param name="SearchDeferral">
/// When set, the catalogue is still asked but the search is not. Retry backoff and attempt limits
/// live here: they exist to stop a search that keeps failing from running nightly forever, and a
/// free, local, id-keyed lookup has no business being behind them.
/// </param>
/// <param name="RetriedAfterUpgrade">
/// Whether the backoff was ignored because the decision predates the current matcher. The
/// algorithm that gave up on the item no longer exists, so its opinion of when to try again does
/// not either.
/// </param>
public sealed record EligibilityDecision(string? StopReason, string? SearchDeferral, bool RetriedAfterUpgrade)
{
    /// <summary>Gets a value indicating whether the catalogues may be asked.</summary>
    public bool CatalogueAllowed => StopReason is null;

    /// <summary>Gets a value indicating whether the search ladder may run.</summary>
    public bool SearchAllowed => StopReason is null && SearchDeferral is null;
}

/// <summary>
/// Decides, from the index alone, what a run may do for an item.
/// </summary>
/// <remarks>
/// <para>
/// This used to be one list of checks answering one question — "skip it?" — and the retry
/// backoff sat in that list. So an item that had failed once (a yt-dlp HTTP 403, or simply
/// nothing acceptable on YouTube) was skipped outright for twelve hours to thirty days, and
/// ThemerrDB, which answers by the item's own database id and costs nothing for a miss, was
/// never asked. The requirement was always "the catalogue first, then our own search"; the
/// ordering of checks quietly violated it for exactly the items that needed it most.
/// </para>
/// <para>
/// Two questions are answered separately now. Is the item settled — by a person, or by the
/// library's rule about existing themes? That stops everything. Otherwise, is the search
/// deferred? That stops only the search. Pure, so every combination is a one-line test.
/// </para>
/// </remarks>
public static class Eligibility
{
    /// <summary>Decides what a run may do for an item.</summary>
    /// <param name="entry">The item's index entry.</param>
    /// <param name="policy">The library's resolved rule.</param>
    /// <param name="hasForeignTheme">
    /// Whether a theme file ThemeForge did not write already exists for the item. Only consulted
    /// when the policy does not allow replacing such a file, so callers may pass a lazily
    /// computed value.
    /// </param>
    /// <param name="configuration">The active settings.</param>
    /// <param name="nowUtc">The current time.</param>
    /// <returns>The decision.</returns>
    public static EligibilityDecision Decide(
        ThemeIndexEntry entry,
        ResolvedThemePolicy policy,
        bool hasForeignTheme,
        PluginConfiguration configuration,
        DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(configuration);

        // A human's decision always outranks the pipeline's, catalogue included.
        if (entry.IsSettledByHuman)
        {
            return Stop(string.Format(CultureInfo.InvariantCulture, "it is marked {0}", entry.State));
        }

        // A theme ThemeForge chose is only revisited when the library allows replacing one.
        if (entry.State is ThemeItemState.AutoAssigned or ThemeItemState.Approved
            && policy.Overwrite == ThemeOverwritePolicy.Never)
        {
            return Stop("it already has a theme assigned by ThemeForge");
        }

        // A theme file already on disk that ThemeForge did not write belongs to the user, and is
        // only replaced when the library is explicitly set to replace anything. This is a fact
        // about right now, re-read on every run and judged against the policy in force; it is
        // never recorded as a state, because recording it once made it permanent.
        if (policy.Overwrite != ThemeOverwritePolicy.ReplaceAny && hasForeignTheme)
        {
            return Stop("it already has a theme that ThemeForge did not write, and this library is not set to replace those");
        }

        // From here on only the search is at stake. A decision made by a matcher that no longer
        // exists gets one more search regardless of any backoff it left behind.
        if (entry.DecidedByMatcher < ThemeIndexEntry.CurrentMatcher
            && entry.State is ThemeItemState.Failed or ThemeItemState.NoCandidate or ThemeItemState.PendingReview)
        {
            return new EligibilityDecision(null, null, true);
        }

        if (entry.NextRetryUtc is { } retryAt && retryAt > nowUtc)
        {
            return Defer(string.Format(CultureInfo.InvariantCulture, "the retry backoff runs until {0:u}", retryAt));
        }

        if (entry.Attempts >= configuration.MaxAttempts
            && entry.State is ThemeItemState.Failed or ThemeItemState.NoCandidate)
        {
            return Defer(string.Format(CultureInfo.InvariantCulture, "it has been tried {0} times", entry.Attempts));
        }

        return new EligibilityDecision(null, null, false);
    }

    private static EligibilityDecision Stop(string reason) => new(reason, null, false);

    private static EligibilityDecision Defer(string reason) => new(null, reason, false);
}
