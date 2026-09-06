using System;
using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Judges whether the candidate is the right length to be a theme.
/// </summary>
/// <remarks>
/// Length is the cheapest way to separate a real theme from the two things that dominate search
/// results: a ten-second clip and a one-hour loop. Outside the hard bounds this rule vetoes,
/// because no weighting of other signals should be able to rescue a candidate of the wrong shape.
/// </remarks>
public sealed class DurationPlausibilityRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "DurationPlausibility";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.DurationPlausibility;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        if (candidate.DurationSeconds is not { } duration || duration <= 0)
        {
            // Flat search listings often omit duration; judging on an unknown would be a guess.
            return RuleVerdict.Abstain("duration unknown");
        }

        var configuration = context.Configuration;

        if (duration < configuration.HardMinSeconds)
        {
            return RuleVerdict.Veto(Describe("too short", duration));
        }

        if (duration > configuration.HardMaxSeconds)
        {
            return RuleVerdict.Veto(Describe("too long", duration));
        }

        var (idealMin, idealMax) = context.Identity.IsSeries
            ? (configuration.SeriesIdealMinSeconds, configuration.SeriesIdealMaxSeconds)
            : (configuration.MovieIdealMinSeconds, configuration.MovieIdealMaxSeconds);

        if (duration >= idealMin && duration <= idealMax)
        {
            return new RuleVerdict(1.0, Describe("ideal length", duration));
        }

        // Outside the ideal window the score falls off linearly towards the hard bound, so a
        // slightly long theme is merely less attractive rather than disqualified.
        var raw = duration < idealMin
            ? Falloff(duration, configuration.HardMinSeconds, idealMin)
            : Falloff(duration, configuration.HardMaxSeconds, idealMax);

        return new RuleVerdict(raw, Describe(raw >= 0 ? "acceptable length" : "implausible length", duration));
    }

    /// <summary>
    /// Maps a duration between an ideal edge and a hard bound onto +1 at the ideal edge and
    /// -1 at the bound.
    /// </summary>
    private static double Falloff(double value, double hardBound, double idealEdge)
    {
        var span = Math.Abs(idealEdge - hardBound);
        if (span < 1)
        {
            return 0;
        }

        var distance = Math.Abs(value - idealEdge);
        return Math.Clamp(1 - (2 * distance / span), -1, 1);
    }

    private static string Describe(string verdict, double seconds) =>
        string.Format(CultureInfo.InvariantCulture, "{0} ({1:0}s)", verdict, seconds);
}
