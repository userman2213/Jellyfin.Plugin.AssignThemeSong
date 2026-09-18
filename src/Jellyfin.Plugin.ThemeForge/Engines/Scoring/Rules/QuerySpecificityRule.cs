using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Prefers candidates surfaced by an earlier, more specific rung of the search ladder.
/// </summary>
/// <remarks>
/// A hit on "Firefly opening theme" is better evidence than the same video turning up under
/// "Firefly soundtrack", because the narrower query had to match more of what was asked for.
/// </remarks>
public sealed class QuerySpecificityRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "QuerySpecificity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.QuerySpecificity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context) =>
        new(
            candidate.FoundBy.SpecificityBonus,
            string.Format(CultureInfo.InvariantCulture, "found by \"{0}\"", candidate.FoundBy.Text));
}
