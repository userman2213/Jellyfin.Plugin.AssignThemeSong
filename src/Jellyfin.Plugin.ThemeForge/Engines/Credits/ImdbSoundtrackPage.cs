using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>One entry in a title's soundtrack listing.</summary>
/// <param name="Title">The piece, as credited.</param>
/// <param name="Performer">Who performs it, when the listing says.</param>
/// <param name="Writer">Who wrote or composed it, when the listing says.</param>
public sealed record SoundtrackEntry(string Title, string? Performer, string? Writer);

/// <summary>
/// Reads a title's soundtrack listing, and decides which of its entries is the theme.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from the fetching so it can be tested against saved pages. IMDb serves the listing
/// in two shapes: the current pages carry it as JSON in a <c>__NEXT_DATA__</c> script, and some
/// clients and regions still get server-rendered <c>soundTrack</c> blocks.
/// </para>
/// <para>
/// Choosing the entry is the hard part, and the reason this is not simply "take the first". For a
/// series the listing usually opens with the main title theme, but for a film it is the licensed
/// songs in playback order: the first entry for Fight Club is a Rolfe Kent cue, and for The
/// Godfather a wedding sequence, neither of which is the theme anybody means. So an entry is
/// accepted only when it says it is a theme -- "Main Title", "Opening Theme", "Love Theme from
/// The Godfather" -- or when the work is a series, where the first entry is a reasonable guess.
/// A film whose listing names no theme yields nothing, which is the right answer: no theme found
/// is recoverable, and the wrong song assigned as a theme is not.
/// </para>
/// </remarks>
public static class ImdbSoundtrackPage
{
    /// <summary>Words that mark an entry as the title music rather than a song used in the film.</summary>
    private static readonly string[] ThemeWords =
    {
        "main title",
        "opening title",
        "opening theme",
        "main theme",
        "end title",
        "end credits",
        "closing theme",
        "title theme",
        "theme song",
        "love theme",
        "prologue",
        "overture",
        "main titles",
        "title sequence",
    };

    private static readonly Regex NextDataRegex = new(
        "<script[^>]+id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyBlockRegex = new(
        "<div[^>]*class=\"[^\"]*soundTrack[^\"]*\"[^>]*>(.*?)</div>\\s*(?=<div[^>]*class=\"[^\"]*soundTrack|</div>)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LegacyTitleRegex = new(
        "^\\s*<div[^>]*>(.*?)</div>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new("<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly Regex BothRolesRegex = new(
        @"(?:Written|Composed|Music)\s+and\s+Performed\s+by\s+(.+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PerformerRegex = new(
        @"Performed\s+by\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WriterRegex = new(
        @"(?:Written|Composed|Music)\s+by\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Reads every soundtrack entry off a page, in the order it lists them.</summary>
    /// <param name="html">The page, as a browser would see it.</param>
    /// <returns>The entries, or empty when the page has none.</returns>
    public static IReadOnlyList<SoundtrackEntry> Parse(string? html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return Array.Empty<SoundtrackEntry>();
        }

        var entries = ParseEmbeddedJson(html);
        return entries.Count > 0 ? entries : ParseLegacyMarkup(html);
    }

    /// <summary>
    /// Picks the entry that is the work's theme, if any of them is.
    /// </summary>
    /// <param name="entries">The listing, in order.</param>
    /// <param name="isSeries">Whether the work is a series.</param>
    /// <param name="title">The work's own title, which a theme is often named after.</param>
    /// <returns>The theme, or null when the listing does not name one.</returns>
    public static SoundtrackEntry? ChooseTheme(
        IReadOnlyList<SoundtrackEntry> entries,
        bool isSeries,
        string? title)
    {
        ArgumentNullException.ThrowIfNull(entries);

        if (entries.Count == 0)
        {
            return null;
        }

        // An entry that calls itself the title music is the theme whatever the work is.
        var named = entries.FirstOrDefault(entry => NamesATheme(entry.Title));
        if (named is not null)
        {
            return named;
        }

        // Failing that, one named after the work: "The Sopranos Theme", "Hawaii Five-O".
        if (!string.IsNullOrWhiteSpace(title))
        {
            var afterTheWork = entries.FirstOrDefault(entry => SharesName(entry.Title, title!));
            if (afterTheWork is not null)
            {
                return afterTheWork;
            }
        }

        // A series listing opens with its title music often enough to be worth taking. A film
        // listing opens with whatever plays first, so it is left alone.
        return isSeries ? entries[0] : null;
    }

    /// <summary>Reports whether an entry's own name says it is the title music.</summary>
    /// <param name="entryTitle">The entry's title.</param>
    /// <returns><see langword="true"/> when it does.</returns>
    public static bool NamesATheme(string? entryTitle)
    {
        if (string.IsNullOrWhiteSpace(entryTitle))
        {
            return false;
        }

        return ThemeWords.Any(word => entryTitle.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reports whether an entry is named after the work itself.</summary>
    private static bool SharesName(string entryTitle, string workTitle)
    {
        var work = TitleNormalizer.Normalize(workTitle);
        if (work.Length < 4)
        {
            // "Up", "It" and the like match far too much to be used this way.
            return false;
        }

        var entry = TitleNormalizer.Normalize(entryTitle);
        return entry.Contains(work, StringComparison.Ordinal);
    }

    /// <summary>Reads the entries out of the JSON the current pages embed.</summary>
    /// <remarks>
    /// The blob is walked rather than indexed by a fixed path: the listing has moved within it
    /// before, and every entry is recognisable on its own as an object with a row title and a list
    /// of credit lines.
    /// </remarks>
    private static IReadOnlyList<SoundtrackEntry> ParseEmbeddedJson(string html)
    {
        var match = NextDataRegex.Match(html);
        if (!match.Success)
        {
            return Array.Empty<SoundtrackEntry>();
        }

        var entries = new List<SoundtrackEntry>();

        try
        {
            using var document = JsonDocument.Parse(match.Groups[1].Value);
            Collect(document.RootElement, entries);
        }
        catch (JsonException)
        {
            return Array.Empty<SoundtrackEntry>();
        }

        return entries;
    }

    private static void Collect(JsonElement element, List<SoundtrackEntry> entries)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (element.TryGetProperty("rowTitle", out var rowTitle)
                    && rowTitle.ValueKind == JsonValueKind.String
                    && element.TryGetProperty("listContent", out var listContent)
                    && listContent.ValueKind == JsonValueKind.Array)
                {
                    var title = Clean(rowTitle.GetString());
                    if (title.Length > 0)
                    {
                        string? performer = null;
                        string? writer = null;

                        foreach (var line in listContent.EnumerateArray())
                        {
                            if (line.ValueKind == JsonValueKind.Object
                                && line.TryGetProperty("html", out var lineHtml)
                                && lineHtml.ValueKind == JsonValueKind.String)
                            {
                                ReadCredit(Clean(lineHtml.GetString()), ref performer, ref writer);
                            }
                        }

                        entries.Add(new SoundtrackEntry(title, performer, writer));
                    }
                }

                foreach (var property in element.EnumerateObject())
                {
                    Collect(property.Value, entries);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, entries);
                }

                break;
        }
    }

    /// <summary>Reads the entries out of the older server-rendered markup.</summary>
    private static IReadOnlyList<SoundtrackEntry> ParseLegacyMarkup(string html)
    {
        var entries = new List<SoundtrackEntry>();

        foreach (Match block in LegacyBlockRegex.Matches(html))
        {
            var content = block.Groups[1].Value;
            var title = string.Empty;

            // The entry's name is in a leading div of its own, with the credit lines after it.
            var titleMatch = LegacyTitleRegex.Match(content);
            if (titleMatch.Success)
            {
                title = Clean(titleMatch.Groups[1].Value);
                content = content[titleMatch.Length..];
            }

            var lines = Regex.Split(content, "<br\\s*/?>", RegexOptions.IgnoreCase)
                .Select(Clean)
                .Where(line => line.Length > 0)
                .ToList();

            if (title.Length == 0)
            {
                if (lines.Count == 0)
                {
                    continue;
                }

                title = lines[0];
                lines = lines.Skip(1).ToList();
            }

            string? performer = null;
            string? writer = null;
            foreach (var line in lines)
            {
                ReadCredit(line, ref performer, ref writer);
            }

            entries.Add(new SoundtrackEntry(title, performer, writer));
        }

        return entries;
    }

    /// <summary>Files one credit line onto the entry being read.</summary>
    private static void ReadCredit(string line, ref string? performer, ref string? writer)
    {
        if (line.Length == 0)
        {
            return;
        }

        // "Written and Performed by X" credits one person with both.
        var both = BothRolesRegex.Match(line);
        if (both.Success)
        {
            var name = Clean(both.Groups[1].Value);
            performer ??= name;
            writer ??= name;
            return;
        }

        var performed = PerformerRegex.Match(line);
        if (performed.Success)
        {
            performer ??= Clean(performed.Groups[1].Value);
            return;
        }

        var written = WriterRegex.Match(line);
        if (written.Success)
        {
            writer ??= Clean(written.Groups[1].Value);
        }
    }

    /// <summary>Strips markup and entities, collapses whitespace, and drops wrapping quotes.</summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var text = WebUtility.HtmlDecode(TagRegex.Replace(value, " "));
        text = WhitespaceRegex.Replace(text, " ").Trim();
        return text.Trim('"', '“', '”').Trim();
    }
}
