using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Policy;

/// <summary>The settings that apply to one item, after any per-library override.</summary>
/// <param name="Enabled">Whether ThemeForge processes the item at all.</param>
/// <param name="Overwrite">What may be done about an existing theme.</param>
/// <param name="LibraryName">The library the item belongs to, for log messages.</param>
public sealed record ResolvedThemePolicy(bool Enabled, ThemeOverwritePolicy Overwrite, string LibraryName);

/// <summary>One Jellyfin library, with the rule currently applied to it.</summary>
/// <param name="Id">The library's id, in the same form the rules are stored against.</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">"movies", "tvshows", and so on.</param>
/// <param name="Enabled">Whether ThemeForge processes it.</param>
/// <param name="Overwrite">Its overwrite rule, possibly <see cref="ThemeOverwritePolicy.UseDefault"/>.</param>
public sealed record LibrarySummary(string Id, string Name, string CollectionType, bool Enabled, ThemeOverwritePolicy Overwrite);

/// <summary>
/// A library as the resolver sees it: the id it matches on, and enough to label it.
/// </summary>
/// <remarks>
/// Exists so the matching and audit logic can be exercised without a live
/// <see cref="ILibraryManager"/>. The id mismatch this type was introduced to prevent was
/// invisible precisely because nothing tested the two paths against each other.
/// </remarks>
/// <param name="Id">The collection folder's id, the one and only id used for matching.</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">"movies", "tvshows", or "mixed".</param>
public sealed record LibraryFolderRef(Guid Id, string Name, string CollectionType);

/// <summary>What the resolver actually sees for one library, as opposed to what the form shows.</summary>
/// <param name="Id">The library's id, as used for matching at runtime.</param>
/// <param name="Name">The library's display name.</param>
/// <param name="CollectionType">"movies", "tvshows", or "mixed".</param>
/// <param name="HasStoredRule">Whether a rule is stored against this library.</param>
/// <param name="Enabled">Whether ThemeForge processes it.</param>
/// <param name="StoredOverwrite">The overwrite value as stored, before the default is applied.</param>
/// <param name="EffectiveOverwrite">The overwrite value the pipeline will actually use.</param>
public sealed record LibraryDiagnostic(
    string Id,
    string Name,
    string CollectionType,
    bool HasStoredRule,
    bool Enabled,
    ThemeOverwritePolicy StoredOverwrite,
    ThemeOverwritePolicy EffectiveOverwrite);

/// <summary>Everything the resolver can say about the stored rules, for the diagnostics view.</summary>
/// <param name="Libraries">One row per library ThemeForge can act on.</param>
/// <param name="OrphanedRules">Stored rules that match no library on this server.</param>
/// <param name="Problems">Human-readable descriptions of anything wrong, empty when all is well.</param>
public sealed record LibraryPolicyAudit(
    IReadOnlyList<LibraryDiagnostic> Libraries,
    IReadOnlyList<LibrarySummary> OrphanedRules,
    IReadOnlyList<string> Problems);

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

    /// <summary>
    /// Reports what the resolver actually resolves, so a rule that looks saved but matches
    /// nothing is visible rather than silently inert.
    /// </summary>
    /// <param name="configuration">The active settings.</param>
    /// <returns>The audit.</returns>
    LibraryPolicyAudit Audit(PluginConfiguration configuration);

    /// <summary>
    /// Writes the rule that will actually be used for each library, and any problem found, to
    /// the log.
    /// </summary>
    /// <param name="configuration">The active settings.</param>
    void LogEffectiveRules(PluginConfiguration configuration);
}

/// <summary>
/// Maps an item to its Jellyfin library and applies that library's rule.
/// </summary>
/// <remarks>
/// <para>
/// Overwrite behaviour belongs at library level because that is where the intent differs. A
/// television library is usually worth re-running as scoring improves, while a curated film
/// library may have themes chosen by hand that should never be touched. A single server-wide
/// switch forces the more cautious of the two onto both.
/// </para>
/// <para>
/// Every library id here comes from one place: the collection folders under the user root, which
/// is the same set <see cref="ILibraryManager.GetCollectionFolders(BaseItem)"/> matches an item
/// against. An earlier version listed libraries by <c>VirtualFolderInfo.ItemId</c> and matched
/// items by <c>CollectionFolder.Id</c>. Nothing in Jellyfin's API guarantees those are the same
/// value, and if they are not, a rule saved from the settings page displays perfectly — both the
/// list and the form read the same id — while never matching a single item at runtime. That is
/// exactly the "I set the rule, it looks saved, nothing happens" failure, and it is impossible to
/// see from the settings page. Using one source removes the possibility rather than testing for it.
/// </para>
/// </remarks>
public sealed class LibraryPolicyResolver : ILibraryPolicyResolver
{
    private readonly ILibraryManager _libraryManager;
    private readonly IThemeForgeLogger<LibraryPolicyResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="LibraryPolicyResolver"/> class.</summary>
    /// <param name="libraryManager">Used to map an item to the library containing it.</param>
    /// <param name="logger">Logger.</param>
    public LibraryPolicyResolver(ILibraryManager libraryManager, IThemeForgeLogger<LibraryPolicyResolver> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <summary>
    /// Renders a library id the one way it is ever stored, so a value read back from the saved
    /// configuration compares equal to one produced here.
    /// </summary>
    /// <param name="id">The library's id.</param>
    /// <returns>The canonical string form.</returns>
    public static string CanonicalId(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses a stored library id, accepting any form <see cref="Guid"/> understands.
    /// </summary>
    /// <remarks>
    /// Rules saved by an earlier version were stored in whatever form that version handed to the
    /// settings page, which was not necessarily this one. Comparing parsed values rather than
    /// strings means those rules keep working instead of silently going inert after an upgrade.
    /// </remarks>
    /// <param name="value">The stored id.</param>
    /// <returns>The id, or <see cref="Guid.Empty"/> if it is not one.</returns>
    public static Guid ParseId(string? value) =>
        Guid.TryParse(value, out var id) ? id : Guid.Empty;

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
        return Summarise(Libraries(), configuration);
    }

    /// <summary>Pairs each library with its stored rule. Pure, so it can be tested directly.</summary>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="configuration">The active settings.</param>
    /// <returns>One summary per library.</returns>
    public static IReadOnlyList<LibrarySummary> Summarise(
        IReadOnlyList<LibraryFolderRef> libraries,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = configuration.LibraryPolicies ?? Array.Empty<LibraryThemePolicy>();

        return libraries
            .Select(library =>
            {
                var rule = MatchRule(rules, library.Id);
                return new LibrarySummary(
                    CanonicalId(library.Id),
                    library.Name,
                    library.CollectionType,
                    rule?.Enabled ?? true,
                    rule?.Overwrite ?? ThemeOverwritePolicy.UseDefault);
            })
            .OrderBy(library => library.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <inheritdoc />
    public LibraryPolicyAudit Audit(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Audit(Libraries(), configuration);
    }

    /// <summary>Builds the audit from an explicit set of libraries. Pure, so it can be tested directly.</summary>
    /// <param name="libraries">The server's libraries.</param>
    /// <param name="configuration">The active settings.</param>
    /// <returns>The audit.</returns>
    public static LibraryPolicyAudit Audit(
        IReadOnlyList<LibraryFolderRef> libraries,
        PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(configuration);

        var rules = configuration.LibraryPolicies ?? Array.Empty<LibraryThemePolicy>();
        var problems = new List<string>();

        var rows = libraries
            .Select(library =>
            {
                var rule = MatchRule(rules, library.Id);
                var stored = rule?.Overwrite ?? ThemeOverwritePolicy.UseDefault;
                return new LibraryDiagnostic(
                    CanonicalId(library.Id),
                    library.Name,
                    library.CollectionType,
                    rule is not null,
                    rule?.Enabled ?? true,
                    stored,
                    Effective(stored, configuration));
            })
            .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var known = libraries.Select(library => library.Id).ToHashSet();
        var orphans = rules
            .Where(rule => !known.Contains(ParseId(rule.LibraryId)))
            .Select(rule => new LibrarySummary(
                rule.LibraryId,
                string.IsNullOrWhiteSpace(rule.LibraryName) ? "(removed library)" : rule.LibraryName,
                "unknown",
                rule.Enabled,
                rule.Overwrite))
            .ToList();

        if (libraries.Count == 0 && rules.Length > 0)
        {
            // Only worth saying when rules exist. A server with no movie or show libraries at all
            // is not misconfigured, and the run report already says it considered nothing.
            problems.Add("Rules are stored, but no movie or show library was found, so none of them can apply to anything.");
        }

        foreach (var orphan in orphans)
        {
            problems.Add(
                $"A rule is stored for \"{orphan.Name}\" ({orphan.Id}) but no library on this server has that id, so the rule does nothing.");
        }

        return new LibraryPolicyAudit(rows, orphans, problems);
    }

    /// <summary>
    /// Writes any problem found by <see cref="Audit"/> to the log.
    /// </summary>
    /// <remarks>
    /// Called at the start of a run rather than at plugin startup: the library database is
    /// reliably ready by then, and a rule that matches nothing only matters when a run is about
    /// to ignore it.
    /// </remarks>
    /// <param name="configuration">The active settings.</param>
    public void LogEffectiveRules(PluginConfiguration configuration)
    {
        LibraryPolicyAudit audit;

        try
        {
            audit = Audit(configuration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not check the per-library rules.");
            return;
        }

        foreach (var problem in audit.Problems)
        {
            _logger.LogWarning("ThemeForge: {Problem}", problem);
        }

        foreach (var library in audit.Libraries.Where(row => row.HasStoredRule))
        {
            _logger.LogInformation(
                "ThemeForge: library \"{Name}\" ({Id}) — enabled {Enabled}, existing themes: {Overwrite}.",
                library.Name,
                library.Id,
                library.Enabled,
                library.EffectiveOverwrite);
        }
    }

    /// <summary>Only libraries that can contain a movie or a series are worth showing or ruling on.</summary>
    private static bool IsRelevant(string collectionType) =>
        collectionType.Equals("mixed", StringComparison.OrdinalIgnoreCase)
        || collectionType.Equals("movies", StringComparison.OrdinalIgnoreCase)
        || collectionType.Equals("tvshows", StringComparison.OrdinalIgnoreCase);

    /// <summary>Collapses <see cref="ThemeOverwritePolicy.UseDefault"/> to the configured default.</summary>
    private static ThemeOverwritePolicy Effective(ThemeOverwritePolicy policy, PluginConfiguration configuration) =>
        policy == ThemeOverwritePolicy.UseDefault
            ? (configuration.DefaultOverwritePolicy == ThemeOverwritePolicy.UseDefault
                ? ThemeOverwritePolicy.Never
                : configuration.DefaultOverwritePolicy)
            : policy;

    private static LibraryThemePolicy? MatchRule(IReadOnlyList<LibraryThemePolicy> rules, Guid libraryId)
    {
        if (libraryId == Guid.Empty)
        {
            return null;
        }

        for (var i = 0; i < rules.Count; i++)
        {
            if (ParseId(rules[i].LibraryId) == libraryId)
            {
                return rules[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The one place a library id comes from. These are the same folder objects
    /// <see cref="ILibraryManager.GetCollectionFolders(BaseItem)"/> returns for an item, so an id
    /// listed here matches an item's library by construction.
    /// </summary>
    private IReadOnlyList<LibraryFolderRef> Libraries()
    {
        try
        {
            return _libraryManager.GetUserRootFolder()
                .Children
                .OfType<Folder>()
                .Select(folder => new LibraryFolderRef(
                    folder.Id,
                    folder.Name ?? "(unnamed)",
                    CollectionTypeOf(folder)))
                .Where(library => IsRelevant(library.CollectionType))
                .ToList();
        }
        catch (Exception ex)
        {
            // Enumerating libraries fails only if the library database is unavailable, which is a
            // transient startup condition. Returning nothing means every item falls back to the
            // server-wide default, which never widens what ThemeForge may do.
            _logger.LogWarning(ex, "ThemeForge: could not enumerate the server's libraries.");
            return Array.Empty<LibraryFolderRef>();
        }
    }

    private static string CollectionTypeOf(Folder folder) =>
        folder is ICollectionFolder collection && collection.CollectionType is { } type
            ? type.ToString()
            : "mixed";

    private LibraryThemePolicy? FindRule(BaseItem item, PluginConfiguration configuration)
    {
        var rules = configuration.LibraryPolicies;
        if (rules is null || rules.Length == 0)
        {
            return null;
        }

        try
        {
            foreach (var folder in _libraryManager.GetCollectionFolders(item))
            {
                var rule = MatchRule(rules, folder.Id);
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
}
