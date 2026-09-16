using System;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring;

/// <summary>
/// A rule's opinion of one candidate, before its configured weight is applied.
/// </summary>
/// <param name="Raw">The opinion, from -1 (certainly wrong) to +1 (certainly right).</param>
/// <param name="Reason">A short explanation shown to the user in the review queue.</param>
/// <param name="IsVeto">When true the candidate is disqualified regardless of every other rule.</param>
public readonly record struct RuleVerdict(double Raw, string Reason, bool IsVeto = false)
{
    /// <summary>Expresses no opinion, so a missing input cannot drag a candidate down.</summary>
    /// <param name="reason">Why the rule had nothing to say.</param>
    /// <returns>A neutral verdict.</returns>
    public static RuleVerdict Abstain(string reason) => new(0, reason);

    /// <summary>Disqualifies the candidate outright.</summary>
    /// <param name="reason">Why the candidate cannot be used.</param>
    /// <returns>A disqualifying verdict.</returns>
    public static RuleVerdict Veto(string reason) => new(-1, reason, true);
}

/// <summary>What a rule needs to know beyond the candidate itself.</summary>
public sealed class ScoringContext
{
    /// <summary>Gets the item a theme is being found for.</summary>
    public required MediaIdentity Identity { get; init; }

    /// <summary>Gets the active settings.</summary>
    public required PluginConfiguration Configuration { get; init; }

    /// <summary>
    /// Gets a lookup that reports which other item, if any, already uses a given video id.
    /// Supplied by the orchestrator from the index; null when duplicate detection is unavailable,
    /// in which case the duplicate rule abstains rather than assuming there is no clash.
    /// </summary>
    public Func<string, string?>? FindExistingAssignment { get; init; }
}

/// <summary>
/// One independent, named judgement about a candidate.
/// </summary>
/// <remarks>
/// Rules are deliberately small and know nothing about each other or about their own
/// importance — the engine applies the configured weight. That keeps each one trivially
/// testable in isolation and lets a user re-tune the system without a rebuild.
/// </remarks>
public interface IScoringRule
{
    /// <summary>Gets the rule's name, which appears in the score breakdown.</summary>
    string Name { get; }

    /// <summary>Reads this rule's weight out of the configured set.</summary>
    /// <param name="weights">The configured weights.</param>
    /// <returns>The weight to apply to this rule's verdict.</returns>
    double WeightFrom(ScoringWeights weights);

    /// <summary>
    /// Gets a value indicating whether this rule's weight is part of what a perfect candidate is
    /// expected to earn.
    /// </summary>
    /// <remarks>
    /// Two kinds of rule say no. Rules that only ever subtract, so that a perfect candidate still
    /// scores 100. And rules that reward something most correct candidates do not have -- a
    /// composer credit, say -- because counting such a rule's weight towards the total would lower
    /// every candidate that lacks the rare extra. A rule outside the denominator can only lift a
    /// candidate; the total is capped at 100 regardless.
    /// </remarks>
    bool ContributesPositively => true;

    /// <summary>Judges one candidate.</summary>
    /// <param name="candidate">The candidate to judge.</param>
    /// <param name="context">The item and settings being judged against.</param>
    /// <returns>The rule's verdict.</returns>
    RuleVerdict Evaluate(Candidate candidate, ScoringContext context);
}
