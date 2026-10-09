using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Reads a television article's infobox, and the theme out of it.
/// </summary>
/// <remarks>
/// <para>
/// Wikipedia's <c>{{Infobox television}}</c> has a field for the opening theme and one for who
/// wrote it, and editors fill them in for most shows whose theme is a song. They do it in at least
/// six ways, every one of them seen in the articles this is tested against:
/// <c>"Song" by [[Artist]]</c>, <c>"Song" performed by [[Artist]]</c>,
/// <c>{{theme song|"Song"|[[Artist]]}}</c>, <c>{{Based on|"Song"|[[Artist]]}}</c>, a
/// <c>{{plainlist|}}</c> of several of those one per line, and
/// <c>{{Unbulleted list|item_style=…|[[A]]|[[B]]}}</c>. Footnotes, references, comments, italics
/// and line breaks are sprinkled through all of them.
/// </para>
/// <para>
/// So the infobox is parsed as a template -- by nesting depth, splitting only on pipes at the top
/// level -- rather than read a line at a time. A line-based reading misses every value written as
/// a list, because those start on the line after the field name, and reads the next field's text
/// as the value of an empty one.
/// </para>
/// </remarks>
public static class WikipediaInfobox
{
    /// <summary>The infobox this reads. Films' infoboxes have no theme field.</summary>
    private static readonly Regex Start = new(@"\{\{\s*Infobox[ _]television\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Comment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Reference = new(@"<ref[^>/]*/>|<ref[^>]*>.*?</ref>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LineBreak = new(@"<br\s*/?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);

    private static readonly Regex WikiLink = new(@"\[\[(?:[^\]|]*\|)?([^\]]*)\]\]", RegexOptions.Compiled);

    private static readonly Regex ExternalLink = new(@"\[(?:https?:)?//[^\s\]]+\s*([^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex Quoted = new("[\"“]([^\"”]+)[\"”]", RegexOptions.Compiled);

    private static readonly Regex PerformedBy = new(@"\b(?:performed\s+)?by\s+(.+)$", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>A version note at the end of a song title, which is not part of what to search for.</summary>
    private static readonly Regex VersionNote = new(
        @"\s*\((?=[^)]*\b(?:mix|version|instrumental|edit|remix|remaster(?:ed)?|cover|extended|single)\b)[^)]*\)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Parenthetical = new(@"\s*\([^)]*\)", RegexOptions.Compiled);

    private static readonly Regex NamedArgument = new(@"^\s*[A-Za-z_][A-Za-z0-9_ ]*=", RegexOptions.Compiled);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Templates that only wrap their argument, whose argument is the text.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.OrdinalIgnoreCase)
    {
        "nowrap", "nobr", "small", "noitalic", "lang", "nowiki",
    };

    /// <summary>Templates that lay out a list, whose items are the values.</summary>
    private static readonly HashSet<string> Lists = new(StringComparer.OrdinalIgnoreCase)
    {
        "plainlist", "plain list", "unbulleted list", "ubl", "ubil", "flatlist", "flat list", "hlist",
    };

    /// <summary>Templates whose first two arguments are a song and who performs it.</summary>
    private static readonly HashSet<string> SongByArtist = new(StringComparer.OrdinalIgnoreCase)
    {
        "theme song", "based on",
    };

    /// <summary>How many names are kept, matching the other sources.</summary>
    private const int MaxNames = 3;

    /// <summary>
    /// Reads the fields of the article's television infobox.
    /// </summary>
    /// <param name="wikitext">The article's source.</param>
    /// <returns>Field names, lower-cased, to their raw values; empty when there is no such infobox.</returns>
    public static IReadOnlyDictionary<string, string> Read(string? wikitext)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(wikitext))
        {
            return fields;
        }

        // Comments go first: they can hold anything, pipes and braces included, and are never
        // what an editor meant as the value.
        var text = Comment.Replace(wikitext, string.Empty);

        var start = Start.Match(text);
        if (!start.Success)
        {
            return fields;
        }

        var body = TemplateBody(text, start.Index);
        if (body is null)
        {
            return fields;
        }

        foreach (var part in SplitTopLevel(body).Skip(1))
        {
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            var name = part[..equals].Trim();
            if (name.Length > 0 && !fields.ContainsKey(name))
            {
                fields[name] = part[(equals + 1)..].Trim();
            }
        }

        return fields;
    }

    /// <summary>Reads the theme song out of an article, when its infobox names one.</summary>
    /// <param name="wikitext">The article's source.</param>
    /// <returns>The theme, or null.</returns>
    public static ThemeSong? Theme(string? wikitext)
    {
        var fields = Read(wikitext);
        return ReadTheme(fields.TryGetValue("open_theme", out var value) ? value
            : fields.TryGetValue("opentheme", out var old) ? old : null);
    }

    /// <summary>Reads who wrote the theme out of an article, when its infobox says.</summary>
    /// <param name="wikitext">The article's source.</param>
    /// <returns>The names, which may be none.</returns>
    public static IReadOnlyList<string> ThemeComposers(string? wikitext)
    {
        var fields = Read(wikitext);
        return ReadNames(fields.TryGetValue("theme_music_composer", out var value) ? value : null);
    }

    /// <summary>
    /// Reads a theme song out of an <c>open_theme</c> value.
    /// </summary>
    /// <param name="value">The field's raw value.</param>
    /// <returns>The song, and its performer when one is named; null when no song title is given.</returns>
    public static ThemeSong? ReadTheme(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            return null;
        }

        // A list names a theme per season or per market; the first is the one the show began
        // with, and the one most people know.
        if (ListItems(text) is { } items)
        {
            return items.Select(ReadTheme).FirstOrDefault(theme => theme is not null);
        }

        foreach (var (name, arguments) in Templates(text))
        {
            if (SongByArtist.Contains(name) && arguments.Count > 0)
            {
                var title = SongTitle(arguments[0]);
                return title is null ? null : new ThemeSong(title, arguments.Count > 1 ? Performer(arguments[1]) : null);
            }
        }

        var plain = Plain(text);
        var quoted = Quoted.Match(plain);
        if (!quoted.Success)
        {
            // An unquoted value is a description -- "Instrumental", "Main theme by the composer"
            // -- rather than a title, and searching for it would find nothing in particular.
            return null;
        }

        var song = SongTitle(quoted.Groups[1].Value);
        if (song is null)
        {
            return null;
        }

        var by = PerformedBy.Match(plain[(quoted.Index + quoted.Length)..]);
        return new ThemeSong(song, by.Success ? Performer(by.Groups[1].Value) : null);
    }

    /// <summary>
    /// Reads the names out of a field such as <c>theme_music_composer</c>.
    /// </summary>
    /// <param name="value">The field's raw value.</param>
    /// <returns>The names, at most three; none when the field is empty or only a comment.</returns>
    public static IReadOnlyList<string> ReadNames(string? value)
    {
        var text = Clean(value);
        if (text.Length == 0)
        {
            return Array.Empty<string>();
        }

        IEnumerable<string> parts = ListItems(text)
            ?? (IEnumerable<string>)Plain(LineBreak.Replace(text, ",")).Split(new[] { ",", ";", " and ", " & " }, StringSplitOptions.None);

        return parts
            .Select(part => Parenthetical.Replace(Plain(part), string.Empty).Trim(' ', '.', '*', '"'))
            .Where(name => name.Length > 1 && name.Length <= 60)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxNames)
            .ToList();
    }

    /// <summary>Removes comments and references, which are never part of a value.</summary>
    private static string Clean(string? value) =>
        value is null ? string.Empty : Reference.Replace(Comment.Replace(value, string.Empty), string.Empty).Trim();

    private static string? SongTitle(string raw)
    {
        var title = VersionNote.Replace(Plain(raw).Trim().Trim('"', '“', '”').Trim(), string.Empty).Trim();
        return title.Length == 0 ? null : title;
    }

    private static string? Performer(string raw)
    {
        // Split on what separates two acts, never on "and" or "&": those are inside band names --
        // Florence and the Machine, Hall & Oates -- far more often than between two performers
        // of a theme song. Composer credits, which list people, are split on them below.
        var name = Parenthetical.Replace(Plain(raw), string.Empty);
        name = name.Split(new[] { ",", ";", " with ", " featuring ", " feat. " }, StringSplitOptions.None)[0];
        name = name.Trim().Trim('.', '"', '“', '”', ' ');
        return name.Length == 0 ? null : name;
    }

    /// <summary>The items of a list template, when the value is one.</summary>
    private static List<string>? ListItems(string text)
    {
        var trimmed = text.TrimStart();
        foreach (var (name, arguments) in Templates(trimmed))
        {
            if (!Lists.Contains(name))
            {
                continue;
            }

            // Plainlist and flatlist put one item per line, bulleted; the unbulleted list takes
            // them as arguments, alongside styling arguments that are not items.
            var joined = string.Join("|", arguments);
            var bulleted = joined.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.StartsWith('*'))
                .Select(line => line.TrimStart('*').Trim())
                .Where(line => line.Length > 0)
                .ToList();

            return bulleted.Count > 0
                ? bulleted
                : arguments.Where(argument => !NamedArgument.IsMatch(argument)).Select(argument => argument.Trim()).Where(argument => argument.Length > 0).ToList();
        }

        return null;
    }

    /// <summary>Reduces wikitext to what a reader would see.</summary>
    private static string Plain(string text)
    {
        var builder = new StringBuilder(text.Length);
        var index = 0;

        // Templates are dropped -- footnotes, citations, formatting -- except the ones that only
        // wrap their argument, which is kept.
        while (index < text.Length)
        {
            if (text.AsSpan(index).StartsWith("{{") && TemplateBody(text, index) is { } body)
            {
                var parts = SplitTopLevel(body);
                if (parts.Count > 1 && Wrappers.Contains(parts[0].Trim()))
                {
                    builder.Append(Plain(parts[^1]));
                }

                index += body.Length + 4;
                continue;
            }

            builder.Append(text[index]);
            index++;
        }

        var plain = LineBreak.Replace(builder.ToString(), " ");
        plain = WikiLink.Replace(plain, match => StripAnchor(match.Groups[1].Value));
        plain = ExternalLink.Replace(plain, "$1");
        plain = plain.Replace("'''", string.Empty, StringComparison.Ordinal).Replace("''", string.Empty, StringComparison.Ordinal);
        plain = Tag.Replace(plain, string.Empty);
        return Whitespace.Replace(plain, " ").Trim();
    }

    /// <summary>A bare link to a section shows the page name; the anchor is not part of it.</summary>
    private static string StripAnchor(string target)
    {
        var hash = target.IndexOf('#', StringComparison.Ordinal);
        return hash > 0 ? target[..hash] : target;
    }

    /// <summary>Every top-level template in a value, with its arguments.</summary>
    private static IEnumerable<(string Name, IReadOnlyList<string> Arguments)> Templates(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var open = text.IndexOf("{{", index, StringComparison.Ordinal);
            if (open < 0 || TemplateBody(text, open) is not { } body)
            {
                yield break;
            }

            var parts = SplitTopLevel(body);
            yield return (parts[0].Trim(), parts.Skip(1).ToList());
            index = open + body.Length + 4;
        }
    }

    /// <summary>The text between a template's braces, or null when they never close.</summary>
    private static string? TemplateBody(string text, int open)
    {
        var depth = 0;
        for (var index = open; index < text.Length - 1; index++)
        {
            if (text[index] == '{' && text[index + 1] == '{')
            {
                depth++;
                index++;
            }
            else if (text[index] == '}' && text[index + 1] == '}')
            {
                depth--;
                index++;
                if (depth == 0)
                {
                    return text.Substring(open + 2, index - 1 - (open + 2));
                }
            }
        }

        return null;
    }

    /// <summary>Splits on the pipes that separate arguments, not the ones inside links or templates.</summary>
    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;

        for (var index = 0; index < body.Length; index++)
        {
            var two = index + 1 < body.Length ? body.Substring(index, 2) : string.Empty;
            if (two is "{{" or "[[")
            {
                depth++;
                current.Append(two);
                index++;
            }
            else if (two is "}}" or "]]" && depth > 0)
            {
                depth--;
                current.Append(two);
                index++;
            }
            else if (body[index] == '|' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(body[index]);
            }
        }

        parts.Add(current.ToString());
        return parts;
    }
}
