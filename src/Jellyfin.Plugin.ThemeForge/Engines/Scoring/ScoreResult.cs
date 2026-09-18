using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring;

/// <summary>
/// The scoring engine's verdict on one candidate, with the full breakdown retained so a
/// human can see why.
/// </summary>
public sealed class ScoreResult
{
    /// <summary>Gets the candidate this verdict is about.</summary>
    public required Discovery.Candidate Candidate { get; init; }

    /// <summary>Gets the individual rule signals, in evaluation order.</summary>
    public required IReadOnlyList<Signal> Breakdown { get; init; }

    /// <summary>Gets the final score on a 0-100 scale, or 0 when vetoed.</summary>
    public required double Total { get; init; }

    /// <summary>Gets a value indicating whether a rule disqualified this candidate outright.</summary>
    public bool IsVetoed => Breakdown.Any(s => s.IsVeto);

    /// <summary>Gets the reason for disqualification, when vetoed.</summary>
    public string? VetoReason => Breakdown.FirstOrDefault(s => s.IsVeto)?.Reason;

    /// <summary>
    /// Gets the signals that moved the needle most, strongest first. Used for the one-line
    /// summary in the review queue where the full breakdown would not fit.
    /// </summary>
    public IReadOnlyList<Signal> TopSignals(int count) =>
        Breakdown.Where(s => Math.Abs(s.Contribution) > 0.001)
                 .OrderByDescending(s => Math.Abs(s.Contribution))
                 .Take(count)
                 .ToList();
}
