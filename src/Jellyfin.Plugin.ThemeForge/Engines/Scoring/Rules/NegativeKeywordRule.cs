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

        var hits = Hits(candidate, context);

        if (hits.Count == 0)
        {
            return RuleVerdict.Abstain("no disqualifying words in the title");
        }

        // One hit is a strong warning; three or more is conclusive.
        var raw = -Math.Min(1.0, 0.55 + (0.25 * (hits.Count - 1)));
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "contains {0}", Join(hits)));
    }

    /// <summary>The disqualifying words in a candidate's title.</summary>
    /// <remarks>
    /// The work's own names and its theme song's are taken out first: every upload for The Piano,
    /// or for the series Episodes, would otherwise be disqualified by its own title.
    /// </remarks>
    /// <param name="candidate">The candidate.</param>
    /// <param name="context">The work, and the settings holding the words.</param>
    /// <returns>Every configured word the rest of the title contains, which may be none.</returns>
    internal static IReadOnlyList<string> Hits(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var identity = context.Identity;
        var haystack = new[] { identity.Title, identity.OriginalTitle, identity.Theme?.Title }
            .Concat(identity.AlternateTitles)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .OrderByDescending(name => name!.Length)
            .Aggregate(candidate.Title, (text, name) => text.Replace(name!, " ", StringComparison.OrdinalIgnoreCase))
            .ToLowerInvariant();

        return (context.Configuration.NegativeKeywords ?? Array.Empty<string>())
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword)
                              && haystack.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
            .ToList();
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "\"" + v + "\""));
}
