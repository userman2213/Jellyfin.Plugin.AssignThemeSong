using System;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Rewards a candidate that names the item's composer.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin already records the composer for most films, and the name is about the most specific
/// thing an upload can say: "Firefly Main Title - Greg Edmonson" cannot be about any other Firefly.
/// It is also what identifies a rights-holder upload titled by track, whose channel is the
/// composer's own "- Topic" channel.
/// </para>
/// <para>
/// Absence is not held against a candidate. Most fan uploads never mention the composer, and a
/// rule that penalised that would push down the ordinary correct answer to reward the rare
/// perfect one.
/// </para>
/// </remarks>
public sealed class ComposerRule : IScoringRule
{
    /// <inheritdoc />
    public string Name => "Composer";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.Composer;

    /// <summary>
    /// Gets a value indicating whether this rule is part of what a perfect candidate is expected
    /// to earn. It is not: it is a bonus.
    /// </summary>
    /// <remarks>
    /// Most correct themes never name the composer. Counting this rule's weight towards the total
    /// a candidate is scored against would lower every candidate that does not name one by about
    /// a tenth -- a measured 79.6 became 71.2 -- and push correct themes out of the auto-assign
    /// band for want of a credit they were never going to carry. Outside the denominator, a
    /// candidate that does name the composer is lifted, and nothing else moves.
    /// </remarks>
    public bool ContributesPositively => false;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        var composers = context.Identity.Composers;
        if (composers is null || composers.Count == 0)
        {
            return RuleVerdict.Abstain("no composer is recorded for this item");
        }

        var named = NamedComposer(composers, candidate);
        if (named is null)
        {
            return RuleVerdict.Abstain("the composer is not named");
        }

        return new RuleVerdict(1.0, string.Format(CultureInfo.InvariantCulture, "names the composer, {0}", named));
    }

    /// <summary>
    /// Finds which of the item's composers, if any, the candidate names.
    /// </summary>
    /// <remarks>
    /// Looked for as whole words in the title, the channel, the uploader and the credited artist.
    /// The description is left out on purpose: it routinely lists every composer whose work an
    /// uploader admires.
    /// </remarks>
    /// <param name="composers">The item's composers.</param>
    /// <param name="candidate">The candidate.</param>
    /// <returns>The composer named, or <see langword="null"/>.</returns>
    public static string? NamedComposer(System.Collections.Generic.IReadOnlyList<string> composers, Candidate candidate)
    {
        ArgumentNullException.ThrowIfNull(composers);
        ArgumentNullException.ThrowIfNull(candidate);

        var places = new[] { candidate.Title, candidate.Channel, candidate.Artist }
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .ToList();

        return composers.FirstOrDefault(composer => places.Any(place => TitleAnchor.Names(composer, place)));
    }
}
