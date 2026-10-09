using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

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
/// <para>
/// A title with nothing to say it is the title music -- no theme words, not the named theme song,
/// not by the work's composer -- is offered for review at most. Every other signal can be at full
/// strength for a song that merely plays in the film: "Drive Soundtrack - Desire - Under Your
/// Spell" scored 73 on its title, its length and its views, and was assigned as Drive's theme.
/// </para>
/// <para>
/// This rule also says whether a candidate is the main theme: the theme song research named, or an
/// upload that calls itself the main theme, the main title, the opening, the intro or the title
/// sequence, or "Back to the Future Theme" -- the work's name and "theme" with nothing between. A
/// "Victory Theme" or a "Love Theme from The Godfather" is one theme among several, and an upload
/// with a disqualifying word -- a cover, an arrangement -- is not the main theme whatever it calls
/// itself. The ranking puts every main theme above everything that is not; see
/// <see cref="ScoringEngine.Order"/>.
/// </para>
/// <para>
/// Among main themes, one that also says it is official earns a step more. Three Game of Thrones
/// uploads of the main title tied, and the more-watched of the other two was an orchestra's concert
/// performance rather than the soundtrack's own. "Official" does nothing for anything else: an
/// official soundtrack's other tracks are still not the theme.
/// </para>
/// </remarks>
public sealed class KeywordAffinityRule : IScoringRule
{
    /// <summary>Words that say only "from the soundtrack", true of every song in the work.</summary>
    private static readonly HashSet<string> SoundtrackOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        "ost", "soundtrack", "original soundtrack", "score", "original score",
    };

    /// <summary>Phrases with which an upload says it is the title music, matched as whole words.</summary>
    private static readonly Regex MainThemePhrase = new(
        @"(?<![\p{L}\p{N}])(main theme|main titles?|opening theme|opening titles?|opening credits|opening sequence|"
        + @"title sequence|title theme|title song|title track|theme song|intro|opening|generique|générique)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Words that may stand between "theme" and the work's name: "Theme from Jurassic Park".</summary>
    private static readonly HashSet<string> ThemeOf = new(StringComparer.Ordinal) { "from", "to", "for", "of", "the" };

    /// <summary>Splits a title into lowercase words, without accents, dropping punctuation and brackets.</summary>
    private static readonly Regex NotAWord = new(@"[^\p{L}\p{N}]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>What separates the parts of an upload's title: "Composer - Theme from Film | Channel".</summary>
    private static readonly Regex Separator = new(@"[\-–—|:•·()\[\]{}""“”/]+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The words with which an upload says it is the theme, which a song's own title never uses.</summary>
    private static readonly string[] ThemeWords =
    {
        "theme", "main title", "opening", "intro", "title sequence", "titles", "end credits",
    };

    /// <summary>"Official", as a word: what a studio's or label's own upload says of itself.</summary>
    private static readonly Regex Official = new(
        @"(?<![\p{L}\p{N}])official(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

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

        // A cover or an arrangement is not the main theme, whatever it calls itself.
        var mainTheme = ClaimsToBeTheMainTheme(candidate, context.Identity)
            && NegativeKeywordRule.Hits(candidate, context.Configuration).Count == 0;

        // The theme the research named outranks any wording: it is the theme, by name and performer.
        // Only when that name is a song's title, though. One that is just the work's name and
        // "theme" -- Wikidata calls Game of Thrones' "Game of Thrones Theme" -- is matched by any
        // upload of it, a concert band's arrangement included, and says nothing the wording below
        // does not.
        if (ComposerRule.NamedThemeSong(context.Identity.Theme, candidate) is { } song
            && IsASongTitle(context.Identity.Theme!.Title, context.Identity.Title))
        {
            return new RuleVerdict(
                1.0,
                string.Format(CultureInfo.InvariantCulture, "is this title's theme song, {0}", song),
                MainTheme: mainTheme);
        }

        var keywords = context.Configuration.PositiveKeywords;
        if (keywords is null || keywords.Length == 0)
        {
            return RuleVerdict.Abstain("no positive keywords are configured") with { MainTheme = mainTheme };
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
        var claimsMain = ClaimsToBeTheMainTheme(candidate, context.Identity);

        if (strong.Count == 0 && !claimsMain)
        {
            // Nothing says this is the title music. A piece by the work's own composer may still be
            // -- "Hans Zimmer - Time" is what Inception is known by -- but anything else is offered
            // for review rather than assigned.
            var reviewOnly = ComposerRule.NamedComposer(context.Identity.Composers, candidate) is null;

            if (LaterTrack(candidate.Title, context.Identity.Title) is { } track)
            {
                return new RuleVerdict(
                    -0.35,
                    string.Format(CultureInfo.InvariantCulture, "track {0} of an album, and it does not call itself the theme", track),
                    ReviewOnly: reviewOnly);
            }

            if (hits.Count > 0)
            {
                // From the soundtrack, which is something, but so is every other song in the work.
                return new RuleVerdict(
                    0.2,
                    string.Format(CultureInfo.InvariantCulture, "only says it is from the soundtrack ({0}), not that it is the theme", Join(hits)),
                    ReviewOnly: reviewOnly);
            }

            // Uploads of real themes almost always say "theme", "opening", "intro" or similar.
            // A title that says none of them is more likely a clip, a scene or a discussion, so
            // this counts mildly against rather than abstaining.
            return new RuleVerdict(-0.35, "no theme-related words in the title", ReviewOnly: reviewOnly);
        }

        // Diminishing returns: the first match is the evidence, further ones add little. Being the
        // main theme is worth one step more, and among main themes so is saying it is the official
        // upload -- but only among them: an official soundtrack's other tracks are not the theme.
        var raw = 0.6 + (0.2 * Math.Max(0, strong.Count - 1));
        var also = new List<string>(2);
        if (claimsMain)
        {
            raw += 0.2;
            also.Add("the main theme");

            if (Official.IsMatch(candidate.Title))
            {
                raw += 0.2;
                also.Add("official");
            }
        }

        raw = Math.Min(1.0, raw);
        var matched = Join(also.Concat(strong).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        return new RuleVerdict(raw, string.Format(CultureInfo.InvariantCulture, "matched {0}", matched), MainTheme: mainTheme);
    }

    /// <summary>
    /// Whether an upload says it is the work's main theme, or is the theme song research named.
    /// </summary>
    /// <remarks>
    /// Wording only: whether it is a cover is the negative keywords' business, and is checked
    /// where this is used.
    /// </remarks>
    /// <param name="candidate">The upload.</param>
    /// <param name="identity">The work.</param>
    /// <returns><see langword="true"/> for "Interstellar Main Theme", "Breaking Bad Intro", "Back to the
    /// Future Theme", "Theme from Jurassic Park" or Dick Dale's "Misirlou" for Pulp Fiction;
    /// <see langword="false"/> for "Victory Theme", "Love Theme from The Godfather" or an official
    /// soundtrack's "A Song of Ice and Fire".</returns>
    internal static bool ClaimsToBeTheMainTheme(Candidate candidate, MediaIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(identity);

        if (ComposerRule.NamedThemeSong(identity.Theme, candidate) is not null)
        {
            return true;
        }

        var titles = new[] { identity.Title, identity.OriginalTitle }
            .Concat(identity.AlternateTitles)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .ToList();

        // The work's own name is taken out first, so a film called "The Opening" is not every
        // upload's claim to be the opening.
        var wording = titles
            .OrderByDescending(title => title.Length)
            .Aggregate(candidate.Title, (text, title) => text.Replace(title, " ", StringComparison.OrdinalIgnoreCase));

        if (MainThemePhrase.IsMatch(wording))
        {
            return true;
        }

        // "Back to the Future Theme", "Gladiator - Theme", "John Williams - Theme from Jurassic
        // Park": "theme" right after the work's name, or opening a part of the title and followed
        // by it. "Victory Theme" and "Love Theme from The Godfather" have a word of their own
        // before "theme", and name one theme among several.
        var words = new List<string>();
        var opensPart = new List<bool>();
        foreach (var part in Separator.Split(candidate.Title))
        {
            var partWords = Words(part);
            for (var i = 0; i < partWords.Count; i++)
            {
                words.Add(partWords[i]);
                opensPart.Add(i == 0);
            }
        }

        foreach (var name in titles.Select(Words).Where(name => name.Count > 0))
        {
            for (var i = 0; i < words.Count; i++)
            {
                if (words[i] == "theme"
                    && (EndsWith(words, i, name) || (opensPart[i] && StartsWith(words, i + 1, name))))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Whether a theme's recorded name is a song's own title, rather than a description of it.
    /// </summary>
    /// <param name="themeTitle">The theme's name, as research recorded it.</param>
    /// <param name="workTitle">The work's title.</param>
    /// <returns><see langword="false"/> for "Game of Thrones Theme", "The X-Files" or "Main Title".</returns>
    internal static bool IsASongTitle(string themeTitle, string? workTitle)
    {
        var name = themeTitle.ToLowerInvariant();

        if (ThemeWords.Any(word => name.Contains(word, StringComparison.Ordinal)))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(workTitle)
            || !name.Contains(workTitle.ToLowerInvariant(), StringComparison.Ordinal);
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

    /// <summary>A title's words, lowercase and without accents or punctuation.</summary>
    private static List<string> Words(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var folded = new string(text.ToLowerInvariant()
            .Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());

        return NotAWord.Split(folded.Replace("&", " and ", StringComparison.Ordinal))
            .Where(word => word.Length > 0)
            .ToList();
    }

    /// <summary>Whether the words just before <paramref name="end"/> are the name, with or without a leading "the".</summary>
    private static bool EndsWith(List<string> words, int end, List<string> name)
    {
        var bare = name.Count > 1 && name[0] == "the" ? name.Skip(1).ToList() : name;
        foreach (var form in new[] { name, bare })
        {
            var start = end - form.Count;
            if (start >= 0 && form.Select((word, i) => words[start + i] == word).All(match => match))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the name follows <paramref name="start"/>, after any of "from", "of" and the like.</summary>
    private static bool StartsWith(List<string> words, int start, List<string> name)
    {
        while (start < words.Count && ThemeOf.Contains(words[start]))
        {
            start++;
        }

        var bare = name.Count > 1 && name[0] == "the" ? name.Skip(1).ToList() : name;
        foreach (var form in new[] { name, bare })
        {
            if (start + form.Count <= words.Count && form.Select((word, i) => words[start + i] == word).All(match => match))
            {
                return true;
            }
        }

        return false;
    }

    private static string Join(IReadOnlyList<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "\"" + v + "\""));
}
