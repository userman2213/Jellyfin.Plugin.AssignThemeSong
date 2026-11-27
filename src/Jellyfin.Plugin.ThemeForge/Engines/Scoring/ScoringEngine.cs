using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
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
    /// <summary>How far below the lowest main theme anything else is held.</summary>
    /// <remarks>A whole point, so the two never round to the same number on the page.</remarks>
    private const double MainThemeMargin = 1.0;

    /// <summary>The name the hold is explained under in a candidate's breakdown.</summary>
    private const string MainThemeRule = "MainTheme";

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
            signals.Add(new Signal(rule.Name, raw, weight, verdict.Reason, ReviewOnly: verdict.ReviewOnly, MainTheme: verdict.MainTheme));

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
        ArgumentNullException.ThrowIfNull(context);

        return Order(candidates.Select(candidate => Score(candidate, context)), context.Configuration);
    }

    /// <summary>
    /// Puts scored candidates in order, best first, with every main theme above everything else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A main theme always ranks above music that is only from the soundtrack, and scores above it
    /// too: anything else is held one point below the lowest-scoring main theme. The score alone
    /// could not promise that. An official soundtrack's other tracks can match the title, be the
    /// right length, have millions of views and name the composer, and so outscore the main theme
    /// on everything but being it. Only main themes good enough to be offered at all count, so a
    /// stray upload calling itself the theme of something cannot push a good candidate out.
    /// </para>
    /// <para>
    /// Ties are common: most signals saturate, so several good uploads can land on exactly the
    /// same score. They were broken alphabetically, which is no reason at all -- three of
    /// Interstellar's candidates tied at 90.6 and "Hans Zimmer - Mountains" beat "Interstellar
    /// Main Theme" for starting with an H. Among equals the more watched upload is the better
    /// bet; the title stays as the last step only so the order is stable.
    /// </para>
    /// <para>
    /// Applied to each ranking and again to everything a search found across its queries, since
    /// a main theme found by a later query has to be put above what an earlier one found.
    /// </para>
    /// </remarks>
    /// <param name="results">The scored candidates, in any order.</param>
    /// <param name="configuration">The settings holding the review threshold.</param>
    /// <returns>The candidates, best first.</returns>
    public static IReadOnlyList<ScoreResult> Order(IEnumerable<ScoreResult> results, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(configuration);

        // Whatever an earlier ordering held back is released first and judged again against the
        // main themes in this set, which may not be the ones it was held for.
        var list = results.Select(Released).ToList();
        var offered = Math.Min(configuration.AutoAssignThreshold, configuration.ReviewThreshold);

        var mainThemes = list.Where(result => result.IsMainTheme && result.Total >= offered).ToList();
        if (mainThemes.Count > 0)
        {
            var lowest = mainThemes.Min(result => result.Total);
            var ceiling = Math.Max(0, lowest - MainThemeMargin);

            list = list
                .Select(result => result.IsMainTheme || result.IsVetoed || result.Total <= ceiling
                    ? result
                    : HeldBelow(result, ceiling, lowest))
                .ToList();
        }

        return list
            .OrderByDescending(result => result.Total)
            .ThenByDescending(result => result.IsMainTheme)
            .ThenByDescending(result => result.Candidate.ViewCount ?? 0)
            .ThenBy(result => result.Candidate.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Holds a candidate that is not the main theme below one that is.</summary>
    private static ScoreResult HeldBelow(ScoreResult result, double ceiling, double mainTheme) => new()
    {
        Candidate = result.Candidate,
        Total = ceiling,
        HeldFrom = result.Total,
        Breakdown = result.Breakdown
            .Append(new Signal(
                MainThemeRule,
                0,
                0,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "held at {0:0.0} from {1:0.0}: it is not the main theme, and an upload that is scored {2:0.0}",
                    ceiling,
                    result.Total,
                    mainTheme)))
            .ToList(),
    };

    /// <summary>Undoes <see cref="HeldBelow"/>, giving a candidate back the score it earned.</summary>
    private static ScoreResult Released(ScoreResult result) => result.HeldFrom is not { } earned
        ? result
        : new ScoreResult
        {
            Candidate = result.Candidate,
            Total = earned,
            Breakdown = result.Breakdown.Where(signal => signal.Rule != MainThemeRule).ToList(),
        };

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
