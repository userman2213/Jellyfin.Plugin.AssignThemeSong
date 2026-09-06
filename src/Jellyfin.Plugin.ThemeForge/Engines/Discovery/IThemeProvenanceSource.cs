using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// A catalogue that answers "the theme for this exact work", keyed on the item's own database id.
/// </summary>
/// <remarks>
/// <para>
/// Searching is guesswork: it asks for videos whose titles look right, and the results have to be
/// scored, ranked and second-guessed because the search corpus contains the themes of every
/// similarly-named show. A lookup by TVDB or TMDB id has none of that uncertainty — either the
/// catalogue holds a theme for that id or it does not — so a hit is applied on where it came from
/// rather than on a score. Running a known-correct answer through a weighted sum of heuristics
/// can only degrade it.
/// </para>
/// <para>
/// Each source is asked once per item, before any search, and a hit ends the work for that item.
/// </para>
/// </remarks>
public interface IThemeProvenanceSource
{
    /// <summary>Gets a short name, shown wherever the theme's origin is reported.</summary>
    string Name { get; }

    /// <summary>Reports whether the user has turned this source on.</summary>
    /// <param name="configuration">The active settings.</param>
    /// <returns><see langword="true"/> when it should be consulted.</returns>
    bool IsEnabled(PluginConfiguration configuration);

    /// <summary>
    /// Looks the item up, returning a candidate only on a definite hit.
    /// </summary>
    /// <param name="identity">The item, with whatever provider ids it has.</param>
    /// <param name="configuration">The active settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The theme for this work, or <see langword="null"/> if the catalogue has none.</returns>
    Task<Candidate?> FindAsync(MediaIdentity identity, PluginConfiguration configuration, CancellationToken cancellationToken);
}
