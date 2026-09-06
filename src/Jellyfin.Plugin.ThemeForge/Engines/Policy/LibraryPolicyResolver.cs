using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.ThemeForge.Engines.Policy;

/// <summary>The settings that apply to one item, after any per-library override.</summary>
/// <param name="Enabled">Whether ThemeForge processes the item at all.</param>
/// <param name="Overwrite">What may be done about an existing theme.</param>
/// <param name="LibraryName">The library the item belongs to, for log messages.</param>
public sealed record ResolvedThemePolicy(bool Enabled, ThemeOverwritePolicy Overwrite, string LibraryName);

/// <summary>One Jellyfin library, with the rule currently applied to it.</summary>
/// <param name="Id">The library's id.</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">"movies", "tvshows", and so on.</param>
/// <param name="Enabled">Whether ThemeForge processes it.</param>
/// <param name="Overwrite">Its overwrite rule, possibly <see cref="ThemeOverwritePolicy.UseDefault"/>.</param>
public sealed record LibrarySummary(string Id, string Name, string CollectionType, bool Enabled, ThemeOverwritePolicy Overwrite);

/// <summary>Works out which settings apply to a given item.</summary>
public interface ILibraryPolicyResolver
{
    /// <summary>Resolves the effective policy for an item.</summary>
    /// <param name="item">The library item.</param>
    /// <param name="configuration">The active settings.</param>
    /// <returns>The policy to apply.</returns>
    ResolvedThemePolicy Resolve(BaseItem item, PluginConfiguration configuration);

    /// <summary>Lists the server's libraries alongside their configured rules.</summary>
    /// <param name="configuration">The active settings.</param>
    /// <returns>One entry per library that can hold movies or series.</returns>
    IReadOnlyList<LibrarySummary> ListLibraries(PluginConfiguration configuration);
}

/// <summary>
/// Maps an item to its Jellyfin library and applies that library's rule.
/// </summary>
/// <remarks>
/// Overwrite behaviour belongs at library level because that is where the intent differs. A
/// television library is usually worth re-running as scoring improves, while a curated film
/// library may have themes chosen by hand that should never be touched. A single server-wide
/// switch forces the more cautious of the two onto both.
/// </remarks>
public sealed class LibraryPolicyResolver : ILibraryPolicyResolver
{
    private readonly ILibraryManager _libraryManager;

    /// <summary>Initializes a new instance of the <see cref="LibraryPolicyResolver"/> class.</summary>
    /// <param name="libraryManager">Used to map an item to the library containing it.</param>
    public LibraryPolicyResolver(ILibraryManager libraryManager) => _libraryManager = libraryManager;

    /// <inheritdoc />
    public ResolvedThemePolicy Resolve(BaseItem item, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuration);

        var rule = FindRule(item, configuration);

        if (rule is null)
        {
            // An item in a library with no rule of its own follows the server-wide default.
            return new ResolvedThemePolicy(true, Effective(ThemeOverwritePolicy.UseDefault, configuration), string.Empty);
        }

        return new ResolvedThemePolicy(rule.Enabled, Effective(rule.Overwrite, configuration), rule.LibraryName);
    }

    /// <inheritdoc />
    public IReadOnlyList<LibrarySummary> ListLibraries(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = configuration.LibraryPolicies ?? new List<LibraryThemePolicy>();

        return _libraryManager.GetVirtualFolders()
            .Where(folder => IsRelevant(folder.CollectionType?.ToString()))
            .Select(folder =>
            {
                var rule = rules.FirstOrDefault(r => string.Equals(r.LibraryId, folder.ItemId, StringComparison.Ordinal));
                return new LibrarySummary(
                    folder.ItemId ?? string.Empty,
                    folder.Name ?? "(unnamed)",
                    folder.CollectionType?.ToString() ?? "mixed",
                    rule?.Enabled ?? true,
                    rule?.Overwrite ?? ThemeOverwritePolicy.UseDefault);
            })
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Only libraries that can contain a movie or a series are worth showing or ruling on.</summary>
    private static bool IsRelevant(string? collectionType) =>
        collectionType is null
        || collectionType.Equals("movies", StringComparison.OrdinalIgnoreCase)
        || collectionType.Equals("tvshows", StringComparison.OrdinalIgnoreCase);

    private LibraryThemePolicy? FindRule(BaseItem item, PluginConfiguration configuration)
    {
        var rules = configuration.LibraryPolicies;
        if (rules is null || rules.Count == 0)
        {
            return null;
        }

        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                var id = folder.Id.ToString("N");
                var rule = rules.FirstOrDefault(r =>
                    string.Equals(r.LibraryId.Replace("-", string.Empty, StringComparison.Ordinal), id, StringComparison.OrdinalIgnoreCase));

                if (rule is not null)
                {
                    return rule;
                }
            }
        }
        catch (Exception)
        {
            // An item whose library cannot be resolved falls back to the server-wide default,
            // which is the safe direction: it never widens what ThemeForge is allowed to do.
        }

        return null;
    }

    /// <summary>Collapses <see cref="ThemeOverwritePolicy.UseDefault"/> to the configured default.</summary>
    private static ThemeOverwritePolicy Effective(ThemeOverwritePolicy policy, PluginConfiguration configuration) =>
        policy == ThemeOverwritePolicy.UseDefault
            ? (configuration.DefaultOverwritePolicy == ThemeOverwritePolicy.UseDefault
                ? ThemeOverwritePolicy.Never
                : configuration.DefaultOverwritePolicy)
            : policy;
}
