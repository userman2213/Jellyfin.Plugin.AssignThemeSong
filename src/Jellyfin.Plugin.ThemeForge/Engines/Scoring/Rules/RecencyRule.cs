using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Checks the upload date against the release year.
/// </summary>
/// <remarks>
/// A video uploaded years before a show first aired cannot be its theme. The signal is weak in
/// the other direction — themes are re-uploaded constantly — so this only ever nudges.
/// </remarks>
public sealed class RecencyRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "Recency";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.Recency;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        if (candidate.UploadDate is not { } uploaded)
        {
            return RuleVerdict.Abstain("upload date unknown");
        }

        if (context.Identity.Year is not { } year)
        {
            return RuleVerdict.Abstain("release year unknown");
        }

        var difference = uploaded.Year - year;

        // Allow a year of slack: trailers and promotional uploads legitimately precede release.
        if (difference < -1)
        {
            return new RuleVerdict(
                -1,
                string.Format(CultureInfo.InvariantCulture, "uploaded {0} years before release", -difference));
        }

        return new RuleVerdict(
            1,
            string.Format(CultureInfo.InvariantCulture, "uploaded {0}, consistent with a {1} release", uploaded.Year, year));
    }
}
