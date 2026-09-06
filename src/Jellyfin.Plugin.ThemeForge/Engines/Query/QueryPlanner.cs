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
/// The ordering matters as much as the content. The orchestrator walks the ladder lazily and
/// stops as soon as something clears the auto-assign threshold, so putting the queries most
/// likely to find the real theme first is what keeps a large library affordable to process.
/// </remarks>
public sealed class QueryPlanner : IQueryPlanner
{
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

        var queries = new List<SearchQuery>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var template in templates ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                continue;
            }

            foreach (var title in titles)
            {
                var text = Expand(template, title, identity.Year);
                if (text.Length == 0 || !seen.Add(text))
                {
                    continue;
                }

                queries.Add(new SearchQuery(text, queries.Count, template));
            }
        }

        return queries;
    }

    /// <summary>
    /// Substitutes the placeholders in a template. When a title has no year, the <c>{year}</c>
    /// placeholder collapses rather than leaving a literal token in the search text.
    /// </summary>
    private static string Expand(string template, string title, int? year)
    {
        var text = template
            .Replace("{title}", title, StringComparison.OrdinalIgnoreCase)
            .Replace(
                "{year}",
                year?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

        return string.Join(' ', text.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
    }
}
