using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>
/// Tells one country's version of a show from another's of the same name.
/// </summary>
/// <remarks>
/// <para>
/// The Office is two shows: the 2005 American series and the 2001 British one, with different
/// themes. Their uploads say which they are -- "The Office (UK) Opening Theme and Closing Credits"
/// -- and that upload was assigned to the American show, because everything else about it matched:
/// it names "The Office", it says "opening theme", and its length is right. Shameless, Skins, House of
/// Cards, Being Human and Life on Mars are in the same position.
/// </para>
/// <para>
/// Only an explicit marker counts: a country code in brackets, a code or a demonym before "version",
/// "intro" and the like, or an uppercase code standing alone. The work's own title is taken out
/// first, so "American Horror Story" and "Us" are not read as markers, and nothing is concluded
/// when the metadata does not say where the work was made, or when it names both countries.
/// </para>
/// </remarks>
public static class CountryEdition
{
    private static readonly (Regex Pattern, string Country)[] Markers =
    {
        (Bracketed("uk|u\\.k\\.|gb"), "UK"),
        (Bracketed("us|u\\.s\\.|usa|u\\.s\\.a\\."), "US"),
        (Bracketed("au|aus"), "AU"),
        (Bracketed("ca|can"), "CA"),
        (Qualified("uk|u\\.k\\.|british"), "UK"),
        (Qualified("us|u\\.s\\.|usa|american"), "US"),
        (Qualified("australian"), "AU"),
        (Qualified("canadian"), "CA"),

        // Standing alone, only in capitals: "us" is an ordinary word, "US" is a country.
        (new Regex(@"(?<![\p{L}\p{N}])(UK)(?![\p{L}\p{N}])", RegexOptions.Compiled | RegexOptions.CultureInvariant), "UK"),
        (new Regex(@"(?<![\p{L}\p{N}])(USA?)(?![\p{L}\p{N}])", RegexOptions.Compiled | RegexOptions.CultureInvariant), "US"),
    };

    private static readonly Dictionary<string, string> Countries = new(StringComparer.OrdinalIgnoreCase)
    {
        // Jellyfin's production locations are names, TMDB's; research gives ISO 3166 codes,
        // in which the United Kingdom is GB.
        ["United States of America"] = "US",
        ["United States"] = "US",
        ["USA"] = "US",
        ["US"] = "US",
        ["America"] = "US",
        ["United Kingdom"] = "UK",
        ["UK"] = "UK",
        ["GB"] = "UK",
        ["Great Britain"] = "UK",
        ["Britain"] = "UK",
        ["England"] = "UK",
        ["Scotland"] = "UK",
        ["Wales"] = "UK",
        ["Northern Ireland"] = "UK",
        ["Australia"] = "AU",
        ["AU"] = "AU",
        ["Canada"] = "CA",
        ["CA"] = "CA",
    };

    private static readonly Dictionary<string, string> Described = new(StringComparer.Ordinal)
    {
        ["US"] = "the American version",
        ["UK"] = "the British version",
        ["AU"] = "the Australian version",
        ["CA"] = "the Canadian version",
    };

    /// <summary>
    /// Reports when a candidate says it belongs to another country's version of this work.
    /// </summary>
    /// <param name="candidateTitle">The candidate's title.</param>
    /// <param name="identity">The work.</param>
    /// <returns>A description of the other version, or null when there is no contradiction.</returns>
    public static string? Contradiction(string candidateTitle, MediaIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (string.IsNullOrWhiteSpace(candidateTitle) || identity.Countries.Count == 0)
        {
            return null;
        }

        var produced = identity.Countries
            .Select(country => Countries.TryGetValue(country.Trim(), out var code) ? code : null)
            .Where(code => code is not null)
            .ToHashSet(StringComparer.Ordinal);

        if (produced.Count == 0)
        {
            return null;
        }

        var text = WithoutTheWorksOwnTitle(candidateTitle, identity);

        foreach (var (pattern, country) in Markers)
        {
            if (pattern.IsMatch(text) && !produced.Contains(country))
            {
                return Described[country];
            }
        }

        return null;
    }

    private static string WithoutTheWorksOwnTitle(string candidateTitle, MediaIdentity identity)
    {
        var titles = new[] { identity.Title, identity.OriginalTitle }
            .Concat(identity.AlternateTitles)
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .OrderByDescending(title => title!.Length);

        var text = candidateTitle;
        foreach (var title in titles)
        {
            text = text.Replace(title!, " ", StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static Regex Bracketed(string codes) => new(
        @"[\(\[]\s*(?:" + codes + @")\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static Regex Qualified(string codes) => new(
        @"(?<![\p{L}\p{N}])(?:" + codes + @")\s+(?:version|edition|intro|opening|theme|remake|series|tv)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
}
