using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring;

/// <summary>Ranks candidates for an item.</summary>
public interface IScoringEngine
{
    /// <summary>Scores one candidate.</summary>
    /// <param name="candidate">The candidate.</param>
    /// <param name="context">The item and settings to judge against.</param>
    /// <returns>The score and its full breakdown.</returns>
    ScoreResult Score(Candidate candidate, ScoringContext context);

    /// <summary>Scores a set of candidates and returns them best first.</summary>
    /// <param name="candidates">The candidates.</param>
    /// <param name="context">The item and settings to judge against.</param>
    /// <returns>Scored candidates, highest first, with vetoed ones last.</returns>
    IReadOnlyList<ScoreResult> Rank(IEnumerable<Candidate> candidates, ScoringContext context);
}

/// <summary>
/// Applies every rule to a candidate and combines the results into a single 0-100 score.
/// </summary>
/// <remarks>
/// The combination is a plain weighted sum normalised by the total positive weight, chosen over
/// anything cleverer because it stays explainable: every point in the final score is traceable
/// to a named rule, which is what makes the review queue useful and the weights tunable.
/// A veto from any rule fixes the score at zero, so a hard disqualifier such as a ten-hour loop
/// can never be outvoted by a pile of weak positives.
/// </remarks>
public sealed class ScoringEngine : IScoringEngine
{
    private readonly IReadOnlyList<IScoringRule> _rules;

    /// <summary>Initializes a new instance of the <see cref="ScoringEngine"/> class.</summary>
    /// <param name="rules">The rules to apply.</param>
    public ScoringEngine(IEnumerable<IScoringRule> rules) => _rules = rules.ToList();

    /// <inheritdoc />
    public ScoreResult Score(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var weights = context.Configuration.Weights;
        var signals = new List<Signal>(_rules.Count);
        var vetoed = false;
        var earned = 0.0;
        var available = 0.0;

        foreach (var rule in _rules)
        {
            var weight = Math.Max(0, rule.WeightFrom(weights));
            RuleVerdict verdict;

            try
            {
                verdict = rule.Evaluate(candidate, context);
            }
            catch (Exception ex)
            {
                // One misbehaving rule must not cost the candidate its chance, nor abort the run.
                verdict = RuleVerdict.Abstain($"rule failed: {ex.GetType().Name}");
            }

            if (verdict.IsVeto)
            {
                vetoed = true;
                signals.Add(Signal.Veto(rule.Name, verdict.Reason));
                continue;
            }

            var raw = Math.Clamp(verdict.Raw, -1, 1);
            signals.Add(new Signal(rule.Name, raw, weight, verdict.Reason));

            earned += raw * weight;
            if (rule.ContributesPositively)
            {
                available += weight;
            }
        }

        var total = vetoed || available <= 0
            ? 0
            : Math.Clamp(100.0 * earned / available, 0, 100);

        return new ScoreResult
        {
            Candidate = candidate,
            Breakdown = signals,
            Total = total,
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<ScoreResult> Rank(IEnumerable<Candidate> candidates, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        return candidates
            .Select(candidate => Score(candidate, context))
            .OrderByDescending(result => result.Total)
            .ThenBy(result => result.Candidate.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Renders a score breakdown as the lines stored in the index and shown in the UI.</summary>
    /// <param name="result">The score to describe.</param>
    /// <returns>One formatted line per signal, preceded by the total.</returns>
    public static IReadOnlyList<string> Describe(ScoreResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, "Total: {0:0.0}/100", result.Total),
        };

        lines.AddRange(result.Breakdown.Select(signal => signal.ToString()));
        return lines;
    }
}
