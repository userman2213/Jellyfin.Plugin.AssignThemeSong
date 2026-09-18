using System;
using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Treats view count as weak corroboration.
/// </summary>
/// <remarks>
/// Deliberately given a small weight and a logarithmic curve. Popularity correlates with a
/// recording being the one people look for, but a viral reaction video is more popular than any
/// genuine theme, so this must never be able to outweigh the title or keyword signals.
/// </remarks>
public sealed class PopularityRule : IScoringRule
{
    /// <summary>Views at which this rule is considered fully satisfied.</summary>
    private const double SaturationViews = 1_000_000;

    /// <inheritdoc />
    public string Name => "Popularity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.Popularity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        if (candidate.ViewCount is not { } views || views <= 0)
        {
            return RuleVerdict.Abstain("view count unknown");
        }

        var raw = Math.Clamp(Math.Log10(views) / Math.Log10(SaturationViews), 0, 1);
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "{0:N0} views", views));
    }
}
