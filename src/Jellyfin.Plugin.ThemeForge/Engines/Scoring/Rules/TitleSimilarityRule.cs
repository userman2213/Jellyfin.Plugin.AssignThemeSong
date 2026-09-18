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
/// The judgement itself lives in <see cref="TitleAnchor"/>. This rule decides what to point it
/// at and what to make of the answer. It is pointed at the title first and, failing that, at the
/// album the upload belongs to — a rights-holder upload is titled by track, "Main Title" or "The
/// Ballad of Serenity", and names the show only on the album. The album is known only after
/// hydration, so before it a music-release upload whose title does not name the show is held
/// rather than vetoed: it scores below every candidate that does name the show, so it reaches
/// the shortlist only when those are scarce, which is exactly when it is needed. A veto before
/// hydration is permanent, and this is the one class of candidate it used to silence unheard.
/// </para>
/// <para>
/// An ordinary title such as <c>Friends</c> or <c>Lost</c> can be named perfectly by a candidate
/// that has nothing to do with the show — "Marshmello - FRIENDS" is a song — so when the anchor
/// says a title is too ordinary to stand alone, something else has to agree before the candidate
/// can score highly enough to be assigned unattended: a claim to be a theme, the item's own year,
/// its composer's name, or the album it belongs to. It is capped rather than disqualified: it may
/// well be right, and a human glancing at the review queue can tell in a second.
/// </para>
/// </remarks>
public sealed class TitleSimilarityRule : IScoringRule
{
    /// <summary>
    /// The most an ordinary title can score when nothing in the candidate corroborates it.
    /// </summary>
    /// <remarks>
    /// Chosen to land such a candidate in the review band rather than the auto-assign band with
    /// the shipped weights, without dropping it below the review threshold entirely.
    /// </remarks>
    private const double UncorroboratedCeiling = 0.55;

    /// <summary>
    /// What a music-release upload scores before hydration when its title does not name the show.
    /// </summary>
    /// <remarks>
    /// Above the disqualifying floor and well below any anchored candidate, so it is hydrated only
    /// when nothing better is on offer. After hydration it either anchors on its album or is vetoed.
    /// </remarks>
    private const double HeldScore = 0.35;

    /// <summary>What a match on the album alone is worth, relative to one on the title.</summary>
    private const double AlbumFormPenalty = 0.9;

    /// <inheritdoc />
    public string Name => "TitleSimilarity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.TitleSimilarity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var titles = TitlesToTry(context).ToList();
        if (titles.Count == 0)
        {
            return RuleVerdict.Veto("this item has no title to match against");
        }

        var (best, matchedOn) = Best(titles, candidate.Title, context.Identity.Year);
        var viaAlbum = false;

        if (best.Rejected && !string.IsNullOrWhiteSpace(candidate.Album))
        {
            // The album carries the show's name for a track that does not. No year check here: a
            // soundtrack's release or reissue year is not the production's year.
            var (onAlbum, albumMatchedOn) = Best(titles, candidate.Album, null);
            if (!onAlbum.Rejected)
            {
                best = onAlbum;
                matchedOn = albumMatchedOn;
                viaAlbum = true;
            }
        }

        if (best.Rejected)
        {
            if (candidate.IsTopicChannel && !candidate.IsHydrated && string.IsNullOrWhiteSpace(candidate.Album))
            {
                return new RuleVerdict(
                    HeldScore,
                    "a music-release upload whose title does not name the show; held until its album is known");
            }

            return RuleVerdict.Veto(best.Reason);
        }

        var raw = viaAlbum ? best.Score * AlbumFormPenalty : best.Score;
        var reason = viaAlbum
            ? string.Format(CultureInfo.InvariantCulture, "its album \"{0}\" {1}", candidate.Album, best.Reason)
            : best.Reason;

        if (best.RequiresCorroboration && !IsCorroborated(candidate, context, best, viaAlbum))
        {
            raw = Math.Min(raw, UncorroboratedCeiling);
            reason += string.Format(
                CultureInfo.InvariantCulture,
                ", but \"{0}\" is an ordinary title and nothing here backs it up",
                matchedOn);
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
    /// What, besides the title, says this candidate is about the right work.
    /// </summary>
    private static bool IsCorroborated(Candidate candidate, ScoringContext context, AnchorResult anchor, bool viaAlbum) =>
        viaAlbum
        || anchor.YearConfirmed
        || TitleAnchor.ClaimsToBeATheme(candidate.Title)
        || ComposerRule.NamedComposer(context.Identity.Composers, candidate) is not null;

    private static (AnchorResult Result, string MatchedOn) Best(IReadOnlyList<string> titles, string? text, int? year)
    {
        AnchorResult? best = null;
        var matchedOn = titles[0];

        foreach (var title in titles)
        {
            var result = TitleAnchor.Match(title, text, year);

            var better = best is null
                || (best.Rejected && !result.Rejected)
                || (best.Rejected == result.Rejected && result.Score > best.Score);

            if (better)
            {
                best = result;
                matchedOn = title;
            }
        }

        return (best!, matchedOn);
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
