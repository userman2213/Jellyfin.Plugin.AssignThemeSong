using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Penalises a video that is already the theme of a different item.
/// </summary>
/// <remarks>
/// The same video winning twice usually means a generic upload — a "best TV themes" compilation,
/// or a franchise track that matches several entries equally well. It is not always wrong, so
/// this is a heavy penalty rather than a veto: a genuinely shared theme can still be chosen if
/// everything else about it is strong.
/// </remarks>
public sealed class DuplicateRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "Duplicate";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.Duplicate;

    /// <inheritdoc />
    public bool ContributesPositively => false;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        if (context.FindExistingAssignment is null)
        {
            return RuleVerdict.Abstain("no index available to check against");
        }

        var owner = context.FindExistingAssignment(candidate.Id);
        if (string.IsNullOrEmpty(owner))
        {
            return RuleVerdict.Abstain("not used elsewhere");
        }

        return new RuleVerdict(
            -1,
            string.Format(CultureInfo.InvariantCulture, "already assigned to \"{0}\"", owner));
    }
}
