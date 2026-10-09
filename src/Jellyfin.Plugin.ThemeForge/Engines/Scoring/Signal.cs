using System.Globalization;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring;

/// <summary>
/// One rule's contribution to a candidate's score, carrying enough context to explain
/// itself in the review queue. Explainability is the point: a score nobody can argue
/// with is a score nobody can tune.
/// </summary>
/// <param name="Rule">Name of the rule that produced this signal.</param>
/// <param name="Raw">The rule's unweighted opinion, in the range -1 to +1.</param>
/// <param name="Weight">The configured weight applied to <paramref name="Raw"/>.</param>
/// <param name="Reason">Short human-readable justification shown in the UI.</param>
/// <param name="IsVeto">When true the candidate is disqualified outright, whatever else scored.</param>
public sealed record Signal(string Rule, double Raw, double Weight, string Reason, bool IsVeto = false)
{
    /// <summary>Gets this signal's weighted contribution to the total.</summary>
    public double Contribution => Raw * Weight;

    /// <summary>Creates a signal that expresses no opinion, so an unavailable input cannot drag a score down.</summary>
    public static Signal Abstain(string rule, string reason) => new(rule, 0.0, 0.0, reason);

    /// <summary>Creates a disqualifying signal.</summary>
    public static Signal Veto(string rule, string reason) => new(rule, -1.0, 0.0, reason, IsVeto: true);

    /// <inheritdoc />
    public override string ToString() =>
        IsVeto
            ? string.Format(CultureInfo.InvariantCulture, "{0}: VETO ({1})", Rule, Reason)
            : string.Format(CultureInfo.InvariantCulture, "{0}: {1:+0.00;-0.00} x{2:0.##} = {3:+0.00;-0.00} ({4})", Rule, Raw, Weight, Contribution, Reason);
}
