using System;
using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;

namespace Jellyfin.Plugin.ThemeForge.Engines.Decision;

/// <summary>What the pipeline should do with the best candidate it found.</summary>
public enum DecisionOutcome
{
    /// <summary>Nothing scored well enough to be worth offering.</summary>
    NoAcceptableCandidate = 0,

    /// <summary>Good enough to offer a human, not good enough to apply unattended.</summary>
    Review = 1,

    /// <summary>Confident enough to download and assign without asking.</summary>
    AutoAssign = 2,
}

/// <summary>The decision, with the reasoning that produced it.</summary>
/// <param name="Outcome">What to do.</param>
/// <param name="Best">The best-scoring candidate, or null when there was nothing to judge.</param>
/// <param name="Reason">A sentence explaining the decision, recorded in the index.</param>
public sealed record ThemeDecision(DecisionOutcome Outcome, ScoreResult? Best, string Reason);

/// <summary>Turns a ranked candidate list into an action.</summary>
public interface IDecisionPolicy
{
    /// <summary>Decides what to do with a ranked candidate list.</summary>
    /// <param name="ranked">Candidates, best first.</param>
    /// <param name="configuration">Settings supplying the thresholds.</param>
    /// <returns>The decision.</returns>
    ThemeDecision Decide(System.Collections.Generic.IReadOnlyList<ScoreResult> ranked, PluginConfiguration configuration);
}

/// <summary>
/// Applies the two configured thresholds.
/// </summary>
/// <remarks>
/// Kept as its own engine, separate from scoring, because the two answer different questions.
/// Scoring asks "how good is this candidate?"; the policy asks "is that good enough to act on
/// without a human?". Splitting them means the risk appetite can be changed without touching
/// the ranking, and the boundary is a single testable function.
/// </remarks>
public sealed class DecisionPolicy : IDecisionPolicy
{
    /// <inheritdoc />
    public ThemeDecision Decide(
        System.Collections.Generic.IReadOnlyList<ScoreResult> ranked,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(configuration);

        if (ranked.Count == 0)
        {
            return new ThemeDecision(DecisionOutcome.NoAcceptableCandidate, null, "the search returned no candidates");
        }

        var best = ranked[0];

        if (best.IsVetoed)
        {
            return new ThemeDecision(
                DecisionOutcome.NoAcceptableCandidate,
                best,
                $"every candidate was disqualified; the best was rejected because {best.VetoReason}");
        }

        // A threshold pair the user has inverted would otherwise send confident matches to review.
        var autoAssign = Math.Max(configuration.AutoAssignThreshold, configuration.ReviewThreshold);
        var review = Math.Min(configuration.AutoAssignThreshold, configuration.ReviewThreshold);

        if (best.Total >= autoAssign)
        {
            return new ThemeDecision(
                DecisionOutcome.AutoAssign,
                best,
                string.Format(CultureInfo.InvariantCulture, "scored {0:0.0}, at or above the auto-assign threshold of {1:0.0}", best.Total, autoAssign));
        }

        if (best.Total >= review)
        {
            return new ThemeDecision(
                DecisionOutcome.Review,
                best,
                string.Format(CultureInfo.InvariantCulture, "scored {0:0.0}, between the review threshold of {1:0.0} and the auto-assign threshold of {2:0.0}", best.Total, review, autoAssign));
        }

        return new ThemeDecision(
            DecisionOutcome.NoAcceptableCandidate,
            best,
            string.Format(CultureInfo.InvariantCulture, "the best candidate scored {0:0.0}, below the review threshold of {1:0.0}", best.Total, review));
    }
}
