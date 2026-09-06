using System.Globalization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Measures whether the candidate is actually about the right show or film.
/// </summary>
/// <remarks>
/// The single most important signal, and the only one that vetoes on weakness: a beautifully
/// produced main title from the wrong series is worse than finding nothing, because it gets
/// written to the library and nobody notices.
/// </remarks>
public sealed class TitleSimilarityRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "TitleSimilarity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.TitleSimilarity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        var candidateTitle = TitleNormalizer.NormalizeCandidate(candidate.Title);
        var best = TextSimilarity.TitleMatch(context.Identity.NormalizedTitle, candidateTitle);
        var matchedOn = context.Identity.Title;

        foreach (var alternate in context.Identity.AlternateTitles)
        {
            var score = TextSimilarity.TitleMatch(alternate, candidateTitle);
            if (score > best)
            {
                best = score;
                matchedOn = alternate;
            }
        }

        var floor = context.Configuration.MinimumTitleSimilarity;
        if (best < floor)
        {
            return RuleVerdict.Veto(string.Format(
                CultureInfo.InvariantCulture,
                "title match {0:P0} is below the {1:P0} minimum — this does not look like the right title",
                best,
                floor));
        }

        return new RuleVerdict(
            best,
            string.Format(CultureInfo.InvariantCulture, "title match {0:P0} against \"{1}\"", best, matchedOn));
    }
}
