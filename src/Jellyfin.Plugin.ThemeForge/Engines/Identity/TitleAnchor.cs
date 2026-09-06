using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>What the anchor found when it looked for one title inside another.</summary>
/// <param name="Score">How strongly the candidate names this work, from 0 to 1.</param>
/// <param name="Anchored">Whether the wanted title was found as whole words at all.</param>
/// <param name="Residual">
/// How much of the anchor's own region is the anchor. 1 means the region names this work and
/// nothing else; a low value means the anchor is a fragment of a longer name.
/// </param>
/// <param name="RequiresCorroboration">
/// Whether the wanted title is too ordinary to stand on its own, so something else in the
/// candidate has to agree before it can be trusted unattended.
/// </param>
/// <param name="Rejected">Whether this is positively the wrong work, rather than merely weak.</param>
/// <param name="Reason">A sentence for the log and the review queue.</param>
public sealed record AnchorResult(
    double Score,
    bool Anchored,
    double Residual,
    bool RequiresCorroboration,
    bool Rejected,
    string Reason);

/// <summary>
/// Decides whether a candidate's title names a particular work.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a substring test that returned a perfect score whenever the wanted title
/// appeared anywhere in the candidate's. That made <c>Lost</c> match "Getting lost in the woods
/// ASMR", <c>House</c> match "Building a house timelapse" and <c>Girls</c> match "Spice Girls
/// Wannabe", each at full marks — and since the title carries more weight than any other signal,
/// a saturated title score carried the wrong answer most of the way to being assigned unattended.
/// </para>
/// <para>
/// Two mechanisms replace it. The first is whole-word anchoring: the wanted title has to appear
/// as a run of complete words, and a one-word title has to match exactly, because Alien and
/// Aliens are different films. The second, and the one that does the work, is the residual: any
/// content word left over in the anchor's own part of the title means the anchor is a fragment of
/// a longer name. "Golden <b>Girls</b>", "Gilmore <b>Girls</b>" and "2 Broke <b>Girls</b>" all
/// anchor <c>Girls</c> perfectly well and all leave a word behind; "Girls - Main Title" leaves
/// nothing. That distinction is invisible to any measure of string similarity, which is why the
/// wrong answers used to be indistinguishable from the right ones — they are real theme songs of
/// real shows, of exactly the right length, from channels of exactly the right kind.
/// </para>
/// <para>
/// Ordinary one-word titles — Lost, House, Girls, Friends, Bones — are flagged as needing
/// corroboration rather than rejected. "Marshmello - FRIENDS" anchors cleanly and leaves no
/// residual, and only the absence of any claim to be a theme separates it from the real thing.
/// </para>
/// </remarks>
public static class TitleAnchor
{
    /// <summary>
    /// The residual below which the candidate is treated as naming a different work.
    /// </summary>
    /// <remarks>
    /// Correct matches leave nothing behind and score 1. The wrong ones that motivated this —
    /// "Golden Girls" for <c>Girls</c>, "office chair review" for <c>The Office</c>, "Lost in
    /// Space" for <c>Lost</c> — land between 0.18 and 0.45. The floor sits above that band with
    /// room for a legitimate qualifier the noise list does not know about.
    /// </remarks>
    public const double IdentityFloor = 0.62;

    /// <summary>Titles at or below this are too ordinary to be trusted without corroboration.</summary>
    public const double LowInformationCeiling = 0.30;

    /// <summary>What a title loses when it only matches with its leading article removed.</summary>
    private const double ArticleFormPenalty = 0.85;

    /// <summary>Two words this long or longer may match approximately rather than exactly.</summary>
    private const int FuzzyMinimumLength = 5;

    /// <summary>How close in length two words must be before approximate matching applies.</summary>
    private const double FuzzyLengthRatio = 0.75;

    /// <summary>How similar two words must be to count as the same word.</summary>
    private const double FuzzySimilarity = 0.92;

    /// <summary>
    /// Words that carry no identity, so their presence or absence never decides a match.
    /// </summary>
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "and", "in", "on", "at", "to", "for", "from", "with", "by",
        "le", "la", "les", "el", "los", "las", "der", "die", "das", "il", "un", "une", "de", "du", "des",
    };

    /// <summary>
    /// Words that say "this recording is a theme". They end the region the residual is measured
    /// over, because everything after them describes the recording rather than the work.
    /// </summary>
    private static readonly HashSet<string> ThemeClaims = new(StringComparer.Ordinal)
    {
        "theme", "themes", "opening", "opener", "intro", "introduction", "main", "title", "titles",
        "credits", "ending", "outro", "generique", "sigla", "soundtrack", "ost", "score", "bgm",
        "op", "ed", "instrumental", "suite", "overture", "tema",
    };

    /// <summary>
    /// Words that qualify a recording without naming a different work, so they do not count as
    /// residual. Deliberately short: every word added here weakens the test that does the work.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "hd", "hq", "4k", "1080p", "720p", "official", "full", "video", "audio", "quality",
        "remastered", "remaster", "version", "original", "extended", "complete", "stereo", "mono",
        "tv", "series", "show", "season", "episode", "size", "clean", "long", "short", "high",
        "bbc", "hbo", "netflix", "amc", "nbc", "cbs", "itv", "showtime", "syfy", "hulu", "disney",
        "song", "music", "audiotrack", "track",
    };

    /// <summary>
    /// Everyday words. A title made only of these could be about anything, so it is held to a
    /// higher standard of evidence rather than trusted on its own.
    /// </summary>
    /// <remarks>
    /// A frequency table would be more principled, but it would also have to be shipped, kept
    /// current and tuned. This list is the shape of the problem as it actually appears in a
    /// library: short, common, single-word series titles.
    /// </remarks>
    private static readonly HashSet<string> Everyday = new(StringComparer.Ordinal)
    {
        "lost", "house", "girls", "boys", "friends", "bones", "office", "family", "life", "love",
        "home", "school", "work", "money", "war", "peace", "time", "day", "night", "man", "woman",
        "men", "women", "kids", "baby", "dog", "cat", "car", "city", "town", "world", "people",
        "mom", "dad", "brother", "sister", "doctor", "nurse", "cop", "law", "order", "shield",
        "heroes", "friend", "new", "old", "good", "bad", "big", "small", "young", "happy",
        "party", "game", "games", "band", "song", "music", "dance", "sport", "news", "weather",
    };

    /// <summary>
    /// Decides how strongly a candidate's title names the wanted work.
    /// </summary>
    /// <param name="wantedTitle">The library item's title, as Jellyfin has it.</param>
    /// <param name="candidateTitle">The candidate's title, as the source reported it.</param>
    /// <returns>What was found.</returns>
    public static AnchorResult Match(string? wantedTitle, string? candidateTitle)
    {
        var candidate = Segment(candidateTitle);
        if (candidate.Count == 0)
        {
            return new AnchorResult(0, false, 0, false, true, "the candidate has no usable title");
        }

        var forms = WantedForms(wantedTitle);
        if (forms.Count == 0)
        {
            return new AnchorResult(0, false, 0, false, true, "this item has no usable title to match against");
        }

        AnchorResult? best = null;

        foreach (var (tokens, penalty, label) in forms)
        {
            var found = Evaluate(tokens, penalty, label, candidate);
            if (best is null || found.Score > best.Score || (best.Rejected && !found.Rejected))
            {
                best = found;
            }

            // The full form matching is the best answer available; the shortened one can only
            // score lower, so there is nothing to gain by trying it.
            if (!found.Rejected && penalty >= 1.0)
            {
                break;
            }
        }

        return best!;
    }

    /// <summary>Reports whether a candidate's title claims to be a theme at all.</summary>
    /// <param name="candidateTitle">The candidate's title.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool ClaimsToBeATheme(string? candidateTitle) =>
        Segment(candidateTitle).Any(token => ThemeClaims.Contains(token.Text));

    /// <summary>
    /// Scores how ordinary a title is, from 0 (could be about anything) to 1 (unmistakable).
    /// </summary>
    /// <param name="title">The title to weigh.</param>
    /// <returns>The score.</returns>
    public static double Informativeness(string? title)
    {
        var tokens = Segment(title).Select(token => token.Text).Where(text => !Filler.Contains(text)).ToList();
        if (tokens.Count == 0)
        {
            return 0;
        }

        var total = tokens.Sum(token =>
            Everyday.Contains(token) ? 0.15
            : token.Length <= 3 ? 0.35
            : token.Length <= 5 ? 0.65
            : 1.0);

        return Math.Clamp(total / 2.0, 0, 1);
    }

    private static AnchorResult Evaluate(
        IReadOnlyList<string> wanted,
        double penalty,
        string label,
        IReadOnlyList<Token> candidate)
    {
        var anchor = FindAnchor(wanted, candidate);
        if (anchor is null)
        {
            return new AnchorResult(
                0,
                false,
                0,
                false,
                true,
                $"the candidate's title does not contain \"{label}\" as whole words");
        }

        var (start, end) = anchor.Value;
        var (regionStart, regionEnd) = Region(candidate, start, end);

        var anchorWeight = 0;
        for (var i = start; i < end; i++)
        {
            if (!Filler.Contains(candidate[i].Text))
            {
                anchorWeight += candidate[i].Text.Length;
            }
        }

        var residualWords = new List<string>();
        var residualWeight = 0;
        for (var i = regionStart; i < regionEnd; i++)
        {
            if (i >= start && i < end)
            {
                continue;
            }

            var text = candidate[i].Text;
            if (Filler.Contains(text) || Noise.Contains(text) || ThemeClaims.Contains(text) || IsQualifier(text))
            {
                continue;
            }

            residualWords.Add(text);
            residualWeight += ResidualWeight(text);
        }

        var residual = anchorWeight + residualWeight == 0
            ? 1.0
            : (double)anchorWeight / (anchorWeight + residualWeight);

        if (residual < IdentityFloor)
        {
            return new AnchorResult(
                residual,
                true,
                residual,
                false,
                true,
                string.Format(
                    CultureInfo.InvariantCulture,
                    "\"{0}\" appears, but the title also says \"{1}\", so it names something else",
                    label,
                    string.Join(" ", residualWords)));
        }

        var needsMore = Informativeness(label) <= LowInformationCeiling;
        var score = Math.Clamp(residual * penalty, 0, 1);

        var reason = residual >= 1
            ? string.Format(CultureInfo.InvariantCulture, "names \"{0}\" and nothing else", label)
            : string.Format(
                CultureInfo.InvariantCulture,
                "names \"{0}\", alongside \"{1}\"",
                label,
                string.Join(" ", residualWords));

        if (penalty < 1)
        {
            reason += ", matching only without its leading article";
        }

        return new AnchorResult(score, true, residual, needsMore, false, reason);
    }

    /// <summary>
    /// The stretch of the candidate's title the residual is measured over: the anchor's own
    /// segment, cut short at the first claim to be a theme.
    /// </summary>
    /// <remarks>
    /// Cutting at the claim is what stops "Star Trek: Deep Space Nine - Theme" passing as
    /// <c>Star Trek</c>: a colon does not start a new segment, so the sibling's name is still in
    /// the region and counts against it. Everything after "Theme" describes the recording and is
    /// none of the region's business.
    /// </remarks>
    private static (int Start, int End) Region(IReadOnlyList<Token> candidate, int anchorStart, int anchorEnd)
    {
        var segment = candidate[anchorStart].Segment;

        var start = anchorStart;
        while (start > 0 && candidate[start - 1].Segment == segment)
        {
            start--;
        }

        var end = anchorEnd;
        while (end < candidate.Count && candidate[end].Segment == candidate[anchorEnd - 1].Segment)
        {
            if (ThemeClaims.Contains(candidate[end].Text))
            {
                break;
            }

            end++;
        }

        // A claim before the anchor ends the region too: in "Theme Song from Lost" the words
        // before the anchor are about the recording, not about a different work.
        for (var i = anchorStart - 1; i >= start; i--)
        {
            if (ThemeClaims.Contains(candidate[i].Text))
            {
                start = i + 1;
                break;
            }
        }

        return (start, end);
    }

    private static (int Start, int End)? FindAnchor(IReadOnlyList<string> wanted, IReadOnlyList<Token> candidate)
    {
        for (var start = 0; start < candidate.Count; start++)
        {
            if (TryAnchorAt(wanted, candidate, start, out var end))
            {
                return (start, end);
            }
        }

        return null;
    }

    private static bool TryAnchorAt(IReadOnlyList<string> wanted, IReadOnlyList<Token> candidate, int start, out int end)
    {
        var i = 0;
        var j = start;
        var solid = 0;
        var single = wanted.Count(token => !Filler.Contains(token)) == 1;

        while (i < wanted.Count && j < candidate.Count)
        {
            if (Same(wanted[i], candidate[j].Text, single))
            {
                if (!Filler.Contains(wanted[i]))
                {
                    solid++;
                }

                i++;
                j++;
                continue;
            }

            // An interior "the" or "of" may be missing from either side without changing which
            // work is named. A leading article may not: that is a separate form, tried on its own.
            if (i > 0 && Filler.Contains(wanted[i]))
            {
                i++;
                continue;
            }

            if (i > 0 && Filler.Contains(candidate[j].Text))
            {
                j++;
                continue;
            }

            end = 0;
            return false;
        }

        while (i < wanted.Count && i > 0 && Filler.Contains(wanted[i]))
        {
            i++;
        }

        end = j;
        return i == wanted.Count && solid > 0;
    }

    /// <summary>
    /// Whether two words are the same word.
    /// </summary>
    /// <remarks>
    /// Approximate matching is deliberately hard to satisfy. A plain similarity threshold scores
    /// <c>castle</c> against <c>castlevania</c> at 0.909, <c>dark</c> against <c>darkside</c> at
    /// 0.900 and <c>you</c> against <c>young</c> at 0.907 — all of them different works. Requiring
    /// comparable length as well as similarity separates a spelling difference from a shorter word
    /// that happens to start the same way, and a one-word title has to match exactly: Alien and
    /// Aliens are not the same film.
    /// </remarks>
    private static bool Same(string wanted, string actual, bool exactOnly)
    {
        if (string.Equals(wanted, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (exactOnly || wanted.Length < FuzzyMinimumLength || actual.Length < FuzzyMinimumLength)
        {
            return false;
        }

        var ratio = (double)Math.Min(wanted.Length, actual.Length) / Math.Max(wanted.Length, actual.Length);
        return ratio >= FuzzyLengthRatio && TextSimilarity.JaroWinkler(wanted, actual) >= FuzzySimilarity;
    }

    /// <summary>
    /// An initialism or an abbreviation, which qualifies a title rather than naming a different
    /// work: the "M.D." in "House M.D.", the "UK" in "The Office UK".
    /// </summary>
    private static bool IsQualifier(string token) =>
        token.Length <= 2 && token.All(char.IsLetter);

    /// <summary>
    /// How heavily one leftover word counts against the match.
    /// </summary>
    /// <remarks>
    /// Longer words carry more of a name, so length is the base. Two adjustments matter. Short
    /// words get a floor, because "Bad Boys for Life" is a different film from "Bad Boys" and the
    /// four letters of "Life" should say so. Numbers count double: a number beside a title almost
    /// always separates one entry in a series from another — "Titanic II", "Halo 3", "Blade Runner
    /// 2049" — while a long distinctive title can still carry a stray year without being rejected.
    /// </remarks>
    private static int ResidualWeight(string token) =>
        token.All(char.IsDigit)
            ? Math.Max(token.Length * 2, 6)
            : Math.Max(token.Length, 5);

    /// <summary>
    /// The forms of the wanted title worth trying, best first.
    /// </summary>
    /// <remarks>
    /// The article is part of the title, not noise: "The Boys" and "Boys" are different claims,
    /// and a candidate that says only the second is weaker evidence. So the article is kept in the
    /// first form and dropped in a second one that scores lower, rather than being stripped
    /// unconditionally as it was before.
    /// </remarks>
    private static IReadOnlyList<(IReadOnlyList<string> Tokens, double Penalty, string Label)> WantedForms(string? title)
    {
        var tokens = Segment(title).Select(token => token.Text).ToList();
        if (tokens.Count == 0)
        {
            return Array.Empty<(IReadOnlyList<string>, double, string)>();
        }

        var forms = new List<(IReadOnlyList<string>, double, string)>
        {
            (tokens, 1.0, string.Join(' ', tokens)),
        };

        var lead = 0;
        if (tokens.Count > lead + 1 && Filler.Contains(tokens[lead]))
        {
            lead++;
        }

        // A possessive on the first word is a publisher's branding rather than part of the name
        // people use: "Marvel's Agents of S.H.I.E.L.D." is uploaded as "Agents of SHIELD".
        if (HasPossessivePrefix(title) && tokens.Count > lead + 1)
        {
            lead++;
        }

        if (lead > 0)
        {
            var shortened = tokens.Skip(lead).ToList();
            forms.Add((shortened, ArticleFormPenalty, string.Join(' ', shortened)));
        }

        return forms;
    }

    private static bool HasPossessivePrefix(string? title)
    {
        var trimmed = title?.TrimStart();
        if (string.IsNullOrEmpty(trimmed))
        {
            return false;
        }

        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        if (space <= 0)
        {
            return false;
        }

        var first = trimmed[..space];
        return first.EndsWith("'s", StringComparison.OrdinalIgnoreCase)
            || first.EndsWith("\u2019s", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One word of a title, and which part of the title it belongs to.</summary>
    private readonly record struct Token(string Text, int Segment);

    /// <summary>
    /// Splits a title into words, tracking which part of the title each belongs to.
    /// </summary>
    /// <remarks>
    /// A dash between spaces, a pipe, a slash or a bracket separates the name of a work from
    /// everything else an uploader put in the title, so those start a new part. A colon does not:
    /// it usually joins a work to its own subtitle, and treating it as a boundary would let
    /// "Star Trek: Deep Space Nine" pass as <c>Star Trek</c> with nothing left over.
    /// </remarks>
    private static IReadOnlyList<Token> Segment(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Array.Empty<Token>();
        }

        var text = Fold(title);
        var tokens = new List<Token>();
        var word = new StringBuilder();
        var segment = 0;

        void Flush()
        {
            if (word.Length > 0)
            {
                tokens.Add(new Token(word.ToString(), segment));
                word.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (char.IsLetterOrDigit(c))
            {
                word.Append(c);
                continue;
            }

            Flush();

            if (IsHardSeparator(text, i))
            {
                segment++;
            }
        }

        Flush();

        for (var i = 0; i < tokens.Count; i++)
        {
            if (RomanNumeral(tokens[i].Text) is { } arabic)
            {
                tokens[i] = tokens[i] with { Text = arabic };
            }
        }

        return tokens;
    }

    private static bool IsHardSeparator(string text, int index)
    {
        var c = text[index];

        if (c is '|' or '/' or '(' or ')' or '[' or ']' or '{' or '}' or '"' or ',' or '–' or '—'
            or '«' or '»' or '•' or '~' or ';' or '「' or '」')
        {
            return true;
        }

        // A hyphen inside a word ("Spider-Man") joins it; one standing alone separates.
        return c == '-'
            && (index == 0 || !char.IsLetterOrDigit(text[index - 1])
                || index == text.Length - 1 || !char.IsLetterOrDigit(text[index + 1]));
    }

    private static string? RomanNumeral(string token) => token switch
    {
        "ii" => "2", "iii" => "3", "iv" => "4", "vi" => "6", "vii" => "7", "viii" => "8",
        "ix" => "9", "xi" => "11", "xii" => "12", "xiii" => "13", "xiv" => "14", "xv" => "15",
        _ => null,
    };

    /// <summary>
    /// Lower-cases, joins dotted initialisms, spells out ligatures, strips accents and reads
    /// "&amp;" as "and".
    /// </summary>
    private static string Fold(string title)
    {
        // Apostrophes are removed rather than treated as separators, so "Marvel's" is one word
        // and not "marvel" followed by a stray "s" that no upload would ever repeat.
        var text = JoinInitialisms(title)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant()
            .Replace("&", " and ", StringComparison.Ordinal)
            .Replace("æ", "ae", StringComparison.Ordinal)
            .Replace("œ", "oe", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal)
            .Replace("ø", "o", StringComparison.Ordinal)
            .Replace("ł", "l", StringComparison.Ordinal)
            .Replace("đ", "d", StringComparison.Ordinal)
            .Replace("þ", "th", StringComparison.Ordinal);

        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// Joins a run of single letters separated by dots into one word, so "S.H.I.E.L.D." is a word
    /// rather than six of them and matches an upload that spells it "SHIELD".
    /// </summary>
    private static string JoinInitialisms(string title)
    {
        var builder = new StringBuilder(title.Length);

        for (var i = 0; i < title.Length; i++)
        {
            // A dot between two letters, where the letter before it stands alone, is part of an
            // initialism rather than a sentence.
            if (title[i] == '.'
                && i > 0 && char.IsLetter(title[i - 1])
                && (i == 1 || !char.IsLetterOrDigit(title[i - 2]))
                && i + 1 < title.Length && char.IsLetter(title[i + 1]))
            {
                continue;
            }

            builder.Append(title[i]);
        }

        return builder.ToString();
    }
}
