using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Rewards wording that suggests the candidate really is a theme rather than, say, a scene rip.
/// </summary>
public sealed class KeywordAffinityRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "KeywordAffinity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.KeywordAffinity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        var keywords = context.Configuration.PositiveKeywords;
        if (keywords is null || keywords.Count == 0)
        {
            return RuleVerdict.Abstain("no positive keywords are configured");
        }

        // Only the title is considered. Descriptions routinely mention "theme" in passing,
        // which would make this signal fire for almost everything.
        var haystack = candidate.Title.ToLowerInvariant();
        var hits = keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword)
                              && haystack.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
            .ToList();

        if (hits.Count == 0)
        {
            // Uploads of real themes almost always say "theme", "opening", "intro" or similar.
            // A title that says none of them is more likely a clip, a scene or a discussion, so
            // this counts mildly against rather than abstaining.
            return new RuleVerdict(-0.35, "no theme-related words in the title");
        }

        // Diminishing returns: the first match is the evidence, further ones add little.
        var raw = Math.Min(1.0, 0.6 + (0.2 * (hits.Count - 1)));
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "matched {0}", Join(hits)));
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "\"" + v + "\""));
}
