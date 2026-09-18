using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>
/// Reduces a title to a comparable form, so that "The Expanse (2015)" and "expanse"
/// are recognisably the same thing.
/// </summary>
/// <remarks>
/// Pure and deterministic by design: normalisation is the foundation every title-matching
/// score is built on, so it is kept free of I/O and configuration and covered directly by tests.
/// </remarks>
public static partial class TitleNormalizer
{
    private static readonly HashSet<string> LeadingArticles =
        new(StringComparer.Ordinal) { "the", "a", "an", "le", "la", "les", "der", "die", "das", "el", "los", "las" };

    /// <summary>
    /// Roman numerals worth converting. Deliberately excludes the single-character forms
    /// "i", "v" and "x": a lone "i" is far more often the pronoun or an initial from an
    /// acronym than a sequel number, and converting it mangles titles like "I, Robot" and
    /// "Marvel's Agents of S.H.I.E.L.D.".
    /// </summary>
    private static readonly Dictionary<string, string> RomanNumerals = new(StringComparer.Ordinal)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4",
        ["vi"] = "6", ["vii"] = "7", ["viii"] = "8", ["ix"] = "9",
        ["xi"] = "11", ["xii"] = "12", ["xiii"] = "13", ["xiv"] = "14", ["xv"] = "15",
    };

    /// <summary>
    /// Letters that Unicode decomposition leaves alone because they are distinct letters
    /// rather than accented forms, but which readers and uploaders spell out.
    /// </summary>
    private static readonly (string From, string To)[] LigatureReplacements =
    {
        ("\u00e6", "ae"), ("\u0153", "oe"), ("\u00df", "ss"),
        ("\u00f8", "o"), ("\u0142", "l"), ("\u0111", "d"), ("\u00fe", "th"),
    };

    [GeneratedRegex(@"[\(\[\{][^\)\]\}]*[\)\]\}]")]
    private static partial Regex BracketedRegex();

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>
    /// Normalises a library item's title: strips accents and bracketed qualifiers, folds
    /// punctuation to spaces, converts roman numerals to digits, and drops a leading article.
    /// </summary>
    /// <remarks>
    /// This is the form used to build search queries and the index's stable key. Candidates are
    /// not normalised this way: deciding whether an uploaded title names a work is a different
    /// job, done by <see cref="TitleAnchor"/> against the raw text, because the parts this throws
    /// away — brackets, the leading article, the exact word boundaries — are the parts that tell
    /// "Girls" from "The Golden Girls".
    /// </remarks>
    /// <param name="title">The raw title.</param>
    /// <returns>The normalised form, or an empty string when nothing survives.</returns>
    public static string Normalize(string? title) => Normalize(title, dropBracketedText: true);

    private static string Normalize(string? title, bool dropBracketedText)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var text = title.ToLowerInvariant();

        if (dropBracketedText)
        {
            text = BracketedRegex().Replace(text, " ");
        }

        text = ExpandLigatures(text);
        text = RemoveDiacritics(text);

        // "&" reads as "and" in most titles; doing this before punctuation folding keeps the word.
        text = text.Replace("&", " and ", StringComparison.Ordinal);

        text = NonAlphanumericRegex().Replace(text, " ");
        text = WhitespaceRegex().Replace(text, " ").Trim();

        if (text.Length == 0)
        {
            return string.Empty;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Only a *leading* article is dropped: "The Wire" and "Wire" should match, but
        // removing every "the" would mangle titles like "Wag the Dog".
        if (words.Count > 1 && LeadingArticles.Contains(words[0]))
        {
            words.RemoveAt(0);
        }

        for (var i = 0; i < words.Count; i++)
        {
            if (RomanNumerals.TryGetValue(words[i], out var arabic))
            {
                words[i] = arabic;
            }
        }

        return string.Join(' ', words);
    }

    /// <summary>Splits a normalised title into its words.</summary>
    /// <param name="normalized">A string already passed through <see cref="Normalize"/>.</param>
    /// <returns>The individual words.</returns>
    public static IReadOnlyList<string> Tokenize(string normalized) =>
        string.IsNullOrEmpty(normalized)
            ? Array.Empty<string>()
            : normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Spells out letters that Unicode decomposition does not split, such as "æ".</summary>
    private static string ExpandLigatures(string text)
    {
        foreach (var (from, to) in LigatureReplacements)
        {
            if (text.Contains(from, StringComparison.Ordinal))
            {
                text = text.Replace(from, to, StringComparison.Ordinal);
            }
        }

        return text;
    }

    /// <summary>Strips combining marks so "Amélie" compares equal to "Amelie".</summary>
    private static string RemoveDiacritics(string text)
    {
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
}
