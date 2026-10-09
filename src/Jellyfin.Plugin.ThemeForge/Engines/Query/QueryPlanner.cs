using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Query;

/// <summary>Builds the ordered list of searches to try for one item.</summary>
public interface IQueryPlanner
{
    /// <summary>Plans the search ladder.</summary>
    /// <param name="identity">The item to search for.</param>
    /// <param name="configuration">Settings supplying the templates.</param>
    /// <returns>Queries ordered most-specific first.</returns>
    IReadOnlyList<SearchQuery> Plan(MediaIdentity identity, PluginConfiguration configuration);
}

/// <summary>
/// Expands the configured templates into concrete searches, most specific first.
/// </summary>
/// <remarks>
/// <para>
/// The ordering matters as much as the content. The orchestrator walks the ladder lazily and
/// stops as soon as something clears the auto-assign threshold, so putting the queries most
/// likely to find the real theme first is what keeps a large library affordable to process.
/// </para>
/// <para>
/// A template may name the composer with <c>{composer}</c>. That is the most specific search
/// there is — "Firefly Greg Edmonson theme" cannot be about any other Firefly — and Jellyfin
/// already holds the name for most films. A composer template is expanded once per composer on
/// record, and skipped entirely when there is none: collapsing the placeholder would only repeat
/// a generic search the ladder already runs.
/// </para>
/// <para>
/// A template may also name the theme itself with <c>{theme}</c>: its title, and its performer
/// when one is known. For a show whose theme is a song, "The Sopranos Woke Up This Morning
/// Alabama 3" finds what "The Sopranos theme song" only sometimes does. It is skipped when the
/// theme is not known, and searched under the primary title only, since a song is called the same
/// thing in every market. The title is left out when the song already contains it -- "Skyfall
/// Adele", not "Skyfall Skyfall Adele".
/// </para>
/// </remarks>
public sealed class QueryPlanner : IQueryPlanner
{
    /// <summary>The placeholder a composer template carries.</summary>
    public const string ComposerPlaceholder = "{composer}";

    /// <summary>The placeholder a theme template carries.</summary>
    public const string ThemePlaceholder = "{theme}";

    /// <summary>More composers than this on one work is a compilation, and each costs a search.</summary>
    private const int MaxComposersSearched = 2;

    /// <inheritdoc />
    public IReadOnlyList<SearchQuery> Plan(MediaIdentity identity, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(configuration);

        var templates = identity.IsSeries
            ? configuration.SeriesQueryTemplates
            : configuration.MovieQueryTemplates;

        var titles = new List<string> { identity.Title };
        titles.AddRange(identity.AlternateTitles);

        var composers = identity.Composers.Take(MaxComposersSearched).ToList();

        var queries = new List<SearchQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            var namesComposer = template.Contains(ComposerPlaceholder, StringComparison.OrdinalIgnoreCase);
            if (namesComposer && composers.Count == 0)
            {
                continue;
            }

            var namesTheme = template.Contains(ThemePlaceholder, StringComparison.OrdinalIgnoreCase);
            if (namesTheme && identity.Theme is null)
            {
                continue;
            }

            var theme = identity.Theme?.SearchText ?? string.Empty;

            foreach (var composer in namesComposer ? composers : new List<string> { string.Empty })
            {
                foreach (var title in namesTheme ? titles.Take(1) : titles)
                {
                    var text = Expand(template, title, identity.Year, composer, theme);
                    if (text.Length == 0 || !seen.Add(text))
                    {
                        continue;
                    }

                    queries.Add(new SearchQuery(text, queries.Count, template));
                }
            }
        }

        return queries;
    }

    /// <summary>
    /// Substitutes the placeholders in a template. When a title has no year, the <c>{year}</c>
    /// placeholder collapses rather than leaving a literal token in the search text.
    /// </summary>
    private static string Expand(string template, string title, int? year, string composer, string theme)
    {
        // A song that already carries the title does not need it twice. Whole words, so a film
        // called "Her" keeps its title next to a song with "other" in it.
        if (theme.Length > 0 && TitleAnchor.Names(title, theme))
        {
            title = string.Empty;
        }

        var text = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace(ComposerPlaceholder, composer, StringComparison.OrdinalIgnoreCase)
            .Replace(ThemePlaceholder, theme, StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{year}",
                year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
