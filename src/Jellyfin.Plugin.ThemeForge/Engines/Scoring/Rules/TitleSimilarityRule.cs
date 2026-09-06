using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Decides whether the candidate is about the right show or film at all.
/// </summary>
/// <remarks>
/// <para>
/// The single most important signal, and the only one that disqualifies on weakness: a
/// beautifully produced main title from the wrong series is worse than finding nothing, because
/// it gets written into the library and nobody notices.
/// </para>
/// <para>
/// The judgement itself lives in <see cref="TitleAnchor"/>. This rule turns it into a score, and
/// adds one thing the anchor cannot decide on its own: an ordinary title such as <c>Friends</c>
/// or <c>Lost</c> can be named perfectly by a candidate that has nothing to do with the show —
/// "Marshmello - FRIENDS" is a song — so when the anchor says a title is too ordinary to stand
/// alone, the candidate has to claim to be a theme before it can score highly enough to be
/// assigned unattended. It is capped rather than disqualified: it may well be right, and a human
/// glancing at the review queue can tell in a second.
/// </para>
/// </remarks>
public sealed class TitleSimilarityRule : IScoringRule
{
    /// <summary>
    /// The most an ordinary title can score when nothing in the candidate claims to be a theme.
    /// </summary>
    /// <remarks>
    /// Chosen to land such a candidate in the review band rather than the auto-assign band with
    /// the shipped weights, without dropping it below the review threshold entirely.
    /// </remarks>
    private const double UncorroboratedCeiling = 0.55;

    /// <inheritdoc />
    public string Name => "TitleSimilarity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.TitleSimilarity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        AnchorResult? best = null;
        var matchedOn = context.Identity.Title;

        foreach (var title in TitlesToTry(context))
        {
            var result = TitleAnchor.Match(title, candidate.Title);

            var better = best is null
                || (best.Rejected && !result.Rejected)
                || (best.Rejected == result.Rejected && result.Score > best.Score);

            if (better)
            {
                best = result;
                matchedOn = title;
            }
        }

        if (best is null)
        {
            return RuleVerdict.Veto("this item has no title to match against");
        }

        if (best.Rejected)
        {
            return RuleVerdict.Veto(best.Reason);
        }

        var raw = best.Score;
        var reason = best.Reason;

        if (best.RequiresCorroboration && !TitleAnchor.ClaimsToBeATheme(candidate.Title))
        {
            raw = Math.Min(raw, UncorroboratedCeiling);
            reason += $", but \"{matchedOn}\" is an ordinary title and nothing here says this is a theme";
        }

        var floor = context.Configuration.MinimumTitleSimilarity;
        if (raw < floor)
        {
            return RuleVerdict.Veto(string.Format(
                CultureInfo.InvariantCulture,
                "title match {0:P0} is below the {1:P0} minimum — {2}",
                raw,
                floor,
                best.Reason));
        }

        return new RuleVerdict(raw, reason);
    }

    /// <summary>
    /// Every name the item is known by, best first.
    /// </summary>
    /// <remarks>
    /// The display title is tried first because it is the name an uploader is most likely to have
    /// used. The rest cover a work released under a different name in another market, and a series
    /// whose Jellyfin title carries a publisher's branding the uploads do not.
    /// </remarks>
    private static IEnumerable<string> TitlesToTry(ScoringContext context)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var title in new[] { context.Identity.Title, context.Identity.OriginalTitle }
                     .Concat(context.Identity.AlternateTitles))
        {
            if (!string.IsNullOrWhiteSpace(title) && seen.Add(title))
            {
                yield return title;
            }
        }
    }
}
