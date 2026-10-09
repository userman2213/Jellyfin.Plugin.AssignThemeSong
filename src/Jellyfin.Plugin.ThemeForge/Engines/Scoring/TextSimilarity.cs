using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring;

/// <summary>
/// String comparison helpers used by the title-matching rule.
/// </summary>
/// <remarks>
/// Pure and dependency-free so the behaviour can be pinned down by tests. These are measures of
/// how alike two strings are, and nothing more. Deciding whether a candidate names a particular
/// work is a different question, answered by
/// <see cref="Jellyfin.Plugin.ThemeForge.Engines.Identity.TitleAnchor"/>: string similarity alone
/// cannot tell "Girls" from "The Golden Girls", because as strings they are almost the same and
/// as works they are not related at all.
/// </remarks>
public static class TextSimilarity
{
    /// <summary>Similarity at or above which two individual words count as the same.</summary>
    private const double WordMatchThreshold = 0.9;

    /// <summary>
    /// Measures how much of the wanted title is present in the candidate text.
    /// </summary>
    /// <param name="wantedTokens">Words of the normalised media title.</param>
    /// <param name="candidateTokens">Words of the normalised candidate title.</param>
    /// <returns>The fraction of wanted words found, from 0 to 1.</returns>
    public static double Coverage(IReadOnlyList<string> wantedTokens, IReadOnlyList<string> candidateTokens)
    {
        if (wantedTokens is null || wantedTokens.Count == 0)
        {
            return 0;
        }

        if (candidateTokens is null || candidateTokens.Count == 0)
        {
            return 0;
        }

        var matched = wantedTokens.Count(wanted =>
            candidateTokens.Any(actual =>
                string.Equals(wanted, actual, StringComparison.Ordinal)
                || JaroWinkler(wanted, actual) >= WordMatchThreshold));

        return (double)matched / wantedTokens.Count;
    }

    /// <summary>
    /// Computes the Jaro-Winkler similarity of two strings, which rewards a shared prefix and
    /// tolerates transpositions — the usual shape of a typo or a transliteration difference.
    /// </summary>
    /// <param name="first">First string.</param>
    /// <param name="second">Second string.</param>
    /// <returns>A score from 0 to 1.</returns>
    public static double JaroWinkler(string first, string second)
    {
        var jaro = Jaro(first, second);
        if (jaro < 0.7)
        {
            return jaro;
        }

        var prefix = 0;
        var maxPrefix = Math.Min(4, Math.Min(first.Length, second.Length));
        while (prefix < maxPrefix && first[prefix] == second[prefix])
        {
            prefix++;
        }

        return jaro + (prefix * 0.1 * (1 - jaro));
    }

    /// <summary>Computes the Jaro similarity of two strings.</summary>
    private static double Jaro(string first, string second)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second))
        {
            return string.IsNullOrEmpty(first) && string.IsNullOrEmpty(second) ? 1 : 0;
        }

        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 1;
        }

        var window = Math.Max(0, (Math.Max(first.Length, second.Length) / 2) - 1);
        var firstMatched = new bool[first.Length];
        var secondMatched = new bool[second.Length];
        var matches = 0;

        for (var i = 0; i < first.Length; i++)
        {
            var start = Math.Max(0, i - window);
            var end = Math.Min(i + window + 1, second.Length);

            for (var j = start; j < end; j++)
            {
                if (secondMatched[j] || first[i] != second[j])
                {
                    continue;
                }

                firstMatched[i] = true;
                secondMatched[j] = true;
                matches++;
                break;
            }
        }

        if (matches == 0)
        {
            return 0;
        }

        var transpositions = 0;
        var k = 0;
        for (var i = 0; i < first.Length; i++)
        {
            if (!firstMatched[i])
            {
                continue;
            }

            while (!secondMatched[k])
            {
                k++;
            }

            if (first[i] != second[k])
            {
                transpositions++;
            }

            k++;
        }

        var m = (double)matches;
        return ((m / first.Length) + (m / second.Length) + ((m - (transpositions / 2.0)) / m)) / 3.0;
    }
}
