using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>Reads the people credited on a library item.</summary>
/// <remarks>
/// Two methods, so the identity resolver can be tested without a live <c>ILibraryManager</c>,
/// which has far too many members to stub.
/// </remarks>
public interface IPeopleLookup
{
    /// <summary>Gets the composers credited on an item, most prominent first.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>Distinct composer names, or an empty list when none are recorded.</returns>
    IReadOnlyList<string> Composers(BaseItem item);

    /// <summary>Gets everybody else credited on the item's music.</summary>
    /// <remarks>
    /// The lyricist, conductor and arranger of a score are not its composer, but an upload that
    /// names one of them is still talking about this work's music rather than somebody else's.
    /// Worth a smaller nod when scoring; never worth searching for, since a lyricist's name pulls
    /// in songs rather than themes.
    /// </remarks>
    /// <param name="item">The library item.</param>
    /// <returns>Distinct names, or an empty list.</returns>
    IReadOnlyList<string> MusicCredits(BaseItem item);

    /// <summary>Gets what the item's theme is called, and who performs it.</summary>
    /// <remarks>
    /// Not a person, strictly, but asked in the same place and answered from the same research:
    /// the most specific thing a search can ask for is the theme's own title and performer.
    /// </remarks>
    /// <param name="item">The library item.</param>
    /// <returns>The theme, or null when nothing is known.</returns>
    ThemeSong? Theme(BaseItem item);
}

/// <summary>Reads composers out of Jellyfin's people table.</summary>
public sealed class JellyfinPeopleLookup : IPeopleLookup
{
    /// <summary>More than this many composers on one work is a compilation, not a credit.</summary>
    private const int MaxComposers = 3;

    /// <summary>Everybody else whose credit is about the music rather than the picture.</summary>
    private static readonly PersonKind[] MusicRoles =
    {
        PersonKind.Lyricist,
        PersonKind.Conductor,
        PersonKind.Arranger,
    };

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
    public IReadOnlyList<string> Composers(BaseItem item) =>
        Credited(item, kind => kind == PersonKind.Composer);

    /// <inheritdoc />
    public IReadOnlyList<string> MusicCredits(BaseItem item) =>
        Credited(item, kind => Array.IndexOf(MusicRoles, kind) >= 0);

    /// <inheritdoc />
    /// <remarks>Jellyfin has nowhere to record a theme song's title, so it never knows one.</remarks>
    public ThemeSong? Theme(BaseItem item) => null;

    private IReadOnlyList<string> Credited(BaseItem item, Func<PersonKind, bool> wanted)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            return _libraryManager.GetPeople(item)
                .Where(person => wanted(person.Type) && !string.IsNullOrWhiteSpace(person.Name))
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

/// <summary>
/// Falls back to the researched credits when Jellyfin has none of its own.
/// </summary>
/// <remarks>
/// <para>
/// This one decorator is the whole of the wiring. Identity resolution asks for an item's composers
/// synchronously, and three separate things downstream go quiet when the answer is empty: the
/// search ladder drops its composer rung, the scoring bonus abstains, and an ordinary title loses
/// its only item-side corroborator. Answering from the cache lights up all three without any of
/// them knowing where the name came from.
/// </para>
/// <para>
/// Jellyfin's own credits always win. They were entered or fetched for this library and can be
/// corrected by its owner; the cache is a guess made from public databases about a work that may
/// have been identified imperfectly, and it is only ever consulted when there is nothing to lose.
/// </para>
/// </remarks>
public sealed class ResearchedPeopleLookup : IPeopleLookup
{
    private readonly IPeopleLookup _inner;
    private readonly IComposerCatalogue _catalogue;

    /// <summary>Initializes a new instance of the <see cref="ResearchedPeopleLookup"/> class.</summary>
    /// <param name="inner">Jellyfin's own people, asked first.</param>
    /// <param name="catalogue">What research has established.</param>
    public ResearchedPeopleLookup(IPeopleLookup inner, IComposerCatalogue catalogue)
    {
        _inner = inner;
        _catalogue = catalogue;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Whoever wrote the theme goes first, because a theme is being searched for. Jellyfin's own
    /// composers follow and are never replaced; the researched ones stand in only when Jellyfin
    /// has none. The theme's composer is added even when Jellyfin has credits, because it answers
    /// a question Jellyfin never records: Dexter's composer is Daniel Licht, its theme Rolfe Kent's.
    /// </remarks>
    public IReadOnlyList<string> Composers(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var credited = _inner.Composers(item);
        if (!Plugin.Config.ResearchComposers)
        {
            return credited;
        }

        var known = _catalogue.Known(KeysFor(item));
        var composers = credited.Count > 0 ? credited : known.Names;

        return known.ThemeComposers.Count == 0
            ? composers
            : known.ThemeComposers.Concat(composers).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public IReadOnlyList<string> MusicCredits(BaseItem item) => _inner.MusicCredits(item);

    /// <inheritdoc />
    public ThemeSong? Theme(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return _inner.Theme(item)
            ?? (Plugin.Config.ResearchComposers ? _catalogue.Known(KeysFor(item)).Theme : null);
    }

    /// <summary>Builds the cache keys for a library item from the ids it carries.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>The keys, which may be empty for an item with no provider ids.</returns>
    public static IReadOnlyList<string> KeysFor(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return CreditsKeys.For(
            item.GetProviderId(MetadataProvider.Imdb),
            item.GetProviderId(MetadataProvider.Tmdb),
            item.GetProviderId(MetadataProvider.Tvdb),
            item is Series);
    }
}

/// <summary>Knows nobody. For tests, and for callers with no library at hand.</summary>
public sealed class NoPeopleLookup : IPeopleLookup
{
    /// <summary>The shared instance.</summary>
    public static readonly NoPeopleLookup Instance = new();

    /// <inheritdoc />
    public IReadOnlyList<string> Composers(BaseItem item) => Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<string> MusicCredits(BaseItem item) => Array.Empty<string>();

    /// <inheritdoc />
    public ThemeSong? Theme(BaseItem item) => null;
}
