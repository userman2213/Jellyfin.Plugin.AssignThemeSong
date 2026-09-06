using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Penalises the things a YouTube search for a theme actually returns: reactions, covers,
/// ten-hour loops, tutorials and fan edits.
/// </summary>
/// <remarks>
/// Weighted heavily and never a positive contributor, so it can only ever push a candidate down.
/// In practice this rule does more to keep the library clean than any of the positive signals.
/// </remarks>
public sealed class NegativeKeywordRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "NegativeKeywords";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.NegativeKeywords;

    /// <inheritdoc />
    public bool ContributesPositively => false;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        var keywords = context.Configuration.NegativeKeywords;
        if (keywords is null || keywords.Length == 0)
        {
            return RuleVerdict.Abstain("no negative keywords are configured");
        }

        var haystack = candidate.Title.ToLowerInvariant();
        var hits = keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword)
                              && haystack.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
            .ToList();

        if (hits.Count == 0)
        {
            return RuleVerdict.Abstain("no disqualifying words in the title");
        }

        // One hit is a strong warning; three or more is conclusive.
        var raw = -Math.Min(1.0, 0.55 + (0.25 * (hits.Count - 1)));
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "contains {0}", Join(hits)));
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "\"" + v + "\""));
}
