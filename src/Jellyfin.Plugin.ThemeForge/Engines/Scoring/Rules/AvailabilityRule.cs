using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Rejects candidates that cannot actually be downloaded.
/// </summary>
/// <remarks>
/// Pure gatekeeping: it never adds points, it only removes candidates that would waste a
/// download attempt and end up as a failed item needing retries.
/// </remarks>
public sealed class AvailabilityRule : IScoringRule
{
    private static readonly string[] UnusableAvailability =
    {
        "private", "premium_only", "subscriber_only", "needs_auth", "unlisted_but_unavailable",
    };

    /// <inheritdoc />
    public string Name => "Availability";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => 0;

    /// <inheritdoc />
    public bool ContributesPositively => false;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        if (candidate.IsLive)
        {
            return RuleVerdict.Veto("live stream");
        }

        if (context.Configuration.BlockedVideoIds?
                .Any(id => string.Equals(id, candidate.Id, StringComparison.OrdinalIgnoreCase)) == true)
        {
            return RuleVerdict.Veto("this video has been blocked");
        }

        var availability = candidate.Availability;
        if (!string.IsNullOrEmpty(availability)
            && UnusableAvailability.Contains(availability, StringComparer.OrdinalIgnoreCase))
        {
            return RuleVerdict.Veto(string.Format(CultureInfo.InvariantCulture, "not downloadable ({0})", availability));
        }

        return RuleVerdict.Abstain("available");
    }
}
