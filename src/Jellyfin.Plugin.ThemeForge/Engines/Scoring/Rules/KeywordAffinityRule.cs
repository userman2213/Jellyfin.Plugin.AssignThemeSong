using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Rewards wording that suggests the candidate really is a theme rather than, say, a scene rip.
/// </summary>
/// <remarks>
/// <para>
/// Not every word on the list says the same thing. "Main title", "opening", "intro" and "theme"
/// say this is the title music. "Soundtrack" and "OST" only say it is from the soundtrack -- and so
/// is every song in the film. Counting them alike let any track from a soundtrack album pass for
/// the theme: Drive's eighth soundtrack track, a minor score cue with 823 views, was assigned over
/// "I Drive" and "A Real Hero". So a title whose only theme word is "soundtrack" or "OST" earns a
/// little, not the full credit.
/// </para>
/// <para>
/// A title that names itself the main theme or main title earns more than one that only mentions
/// a theme: "Interstellar Main Theme" and "Hans Zimmer - Mountains (Interstellar Soundtrack)" used to
/// tie, and the tie went to whichever sorted first alphabetically.
/// </para>
/// <para>
/// A candidate that is the theme song the research named -- its title and its performer -- is the
/// theme, whatever its upload is called, and gets the full credit. It used to be marked down for
/// not saying "theme": Dick Dale's own "Misirlou", the Pulp Fiction theme, scored below a generic
/// "Pulp Fiction - Opening Theme" upload, because a song's own title never calls it a theme.
/// </para>
/// <para>
/// A numbered track from an album upload -- "Drive Original Soundtrack - 8. He Had a Good Time" --
/// is a track of the album, and only the first track, or one that calls itself the title music, is
/// likely to be the theme.
/// </para>
/// </remarks>
public sealed class KeywordAffinityRule : IScoringRule
{
    /// <summary>Words that say only "from the soundtrack", true of every song in the work.</summary>
    private static readonly HashSet<string> SoundtrackOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "ost", "soundtrack", "original soundtrack", "score", "original score",
    };

    /// <summary>Phrases with which an upload names itself the title music in so many words.</summary>
    private static readonly string[] NamesItselfTheTitleMusic =
    {
        "main theme", "main title", "opening theme", "theme song", "title theme", "opening credits",
        "title sequence", "intro theme", "opening titles",
    };

    /// <summary>
    /// A track number leading a title segment: "8. He Had a Good Time", "08 - He Had a Good Time".
    /// </summary>
    /// <remarks>
    /// The number must open a segment -- the start, or after a separator or a space -- so the "11"
    /// in "2011" is not a track; and the work's own title is taken out first, so "Saw 3. Hello Zepp"
    /// is the Saw 3 theme rather than track 3.
    /// </remarks>
    private static readonly Regex TrackNumber = new(
        @"(?:^|[\-–—|:]\s*|\s)(?:track\s*)?(\d{1,2})\s*(?:[.)]|\s[\-–—])\s+\p{L}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public string Name => "KeywordAffinity";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => weights.KeywordAffinity;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        // The theme the research named outranks any wording: it is the theme, by name and performer.
        if (ComposerRule.NamedThemeSong(context.Identity.Theme, candidate) is { } song)
        {
            return new RuleVerdict(1.0, string.Format(CultureInfo.InvariantCulture, "is this title's theme song, {0}", song));
        }

        var keywords = context.Configuration.PositiveKeywords;
        if (keywords is null || keywords.Length == 0)
        {
            return RuleVerdict.Abstain("no positive keywords are configured");
        }

        // Only the title is considered. Descriptions routinely mention "theme" in passing,
        // which would make this signal fire for almost everything.
        var haystack = candidate.Title.ToLowerInvariant();

        // Distinct, so a keyword listed twice cannot inflate the score below.
        var hits = keywords
            .Where(keyword => !string.IsNullOrWhiteSpace(keyword)
                              && haystack.Contains(keyword.ToLowerInvariant(), StringComparison.Ordinal))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var strong = hits.Where(hit => !SoundtrackOnly.Contains(hit.Trim())).ToList();
        var namesItself = NamesItselfTheTitleMusic.FirstOrDefault(
            phrase => haystack.Contains(phrase, StringComparison.Ordinal));

        if (strong.Count == 0 && namesItself is null)
        {
            if (LaterTrack(candidate.Title, context.Identity.Title) is { } track)
            {
                return new RuleVerdict(
                    -0.35,
                    string.Format(CultureInfo.InvariantCulture, "track {0} of an album, and it does not call itself the theme", track));
            }

            if (hits.Count > 0)
            {
                // From the soundtrack, which is something, but so is every other song in the work.
                return new RuleVerdict(
                    0.2,
                    string.Format(CultureInfo.InvariantCulture, "only says it is from the soundtrack ({0})", Join(hits)));
            }

            // Uploads of real themes almost always say "theme", "opening", "intro" or similar.
            // A title that says none of them is more likely a clip, a scene or a discussion, so
            // this counts mildly against rather than abstaining.
            return new RuleVerdict(-0.35, "no theme-related words in the title");
        }

        // Diminishing returns: the first match is the evidence, further ones add little. Calling
        // itself the main theme or main title in so many words is worth one step more.
        var raw = 0.6 + (0.2 * Math.Max(0, strong.Count - 1));
        if (namesItself is not null)
        {
            raw += 0.2;
        }

        raw = Math.Min(1.0, raw);
        var matched = namesItself is null ? Join(strong) : Join(new[] { namesItself }.Concat(strong).Distinct().ToList());
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "matched {0}", matched));
    }

    /// <summary>
    /// The track number of a numbered album track other than the first, or null.
    /// </summary>
    /// <param name="title">The candidate's title.</param>
    /// <param name="workTitle">The work's own title, whose numbers are not track numbers.</param>
    /// <returns>The track number, when it is 2 or more.</returns>
    internal static int? LaterTrack(string title, string? workTitle = null)
    {
        if (!string.IsNullOrWhiteSpace(workTitle))
        {
            title = title.Replace(workTitle, " ", StringComparison.OrdinalIgnoreCase);
        }

        var match = TrackNumber.Match(title);
        if (!match.Success
            || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var track))
        {
            return null;
        }

        return track >= 2 ? track : null;
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "\"" + v + "\""));
}
