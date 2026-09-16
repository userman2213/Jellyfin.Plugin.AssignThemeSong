using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>Reads the people credited on a library item.</summary>
/// <remarks>
/// One method, so the identity resolver can be tested without a live <c>ILibraryManager</c>,
/// which has far too many members to stub.
/// </remarks>
public interface IPeopleLookup
{
    /// <summary>Gets the composers credited on an item, most prominent first.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>Distinct composer names, or an empty list when none are recorded.</returns>
    IReadOnlyList<string> Composers(BaseItem item);
}

/// <summary>Reads composers out of Jellyfin's people table.</summary>
public sealed class JellyfinPeopleLookup : IPeopleLookup
{
    /// <summary>More than this many composers on one work is a compilation, not a credit.</summary>
    private const int MaxComposers = 3;

    private readonly ILibraryManager _libraryManager;
    private readonly IThemeForgeLogger<JellyfinPeopleLookup> _logger;

    /// <summary>Initializes a new instance of the <see cref="JellyfinPeopleLookup"/> class.</summary>
    /// <param name="libraryManager">Jellyfin's library.</param>
    /// <param name="logger">Logger.</param>
    public JellyfinPeopleLookup(ILibraryManager libraryManager, IThemeForgeLogger<JellyfinPeopleLookup> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Composers(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            return _libraryManager.GetPeople(item)
                .Where(person => person.Type == PersonKind.Composer && !string.IsNullOrWhiteSpace(person.Name))
                .Select(person => person.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxComposers)
                .ToList();
        }
        catch (Exception ex)
        {
            // People are an enrichment, not a requirement: an item without them is searched for by
            // title alone, as every item was before.
            _logger.LogDebug(ex, "ThemeForge: could not read the people credited on \"{Item}\".", item.Name);
            return Array.Empty<string>();
        }
    }
}

/// <summary>Knows nobody. For tests, and for callers with no library at hand.</summary>
public sealed class NoPeopleLookup : IPeopleLookup
{
    /// <summary>The shared instance.</summary>
    public static readonly NoPeopleLookup Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<string> Composers(BaseItem item) => Array.Empty<string>();
}
