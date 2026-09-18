using System;
using System.Linq;

namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// The search ladders ThemeForge ships, and the ones it used to.
/// </summary>
/// <remarks>
/// Saved settings keep whatever ladder they were saved with, so a better default reaches nobody
/// who has already installed the plugin — unless it can be told apart from a deliberate edit. It
/// can: a saved ladder identical to a previous shipped default was never edited, and is replaced
/// with the current one. A ladder that differs in any way is somebody's decision and is left alone.
/// </remarks>
public static class ShippedTemplates
{
    /// <summary>The series ladder, most specific first.</summary>
    public static readonly string[] Series =
    {
        "{title} opening theme song",
        "{title} {composer} theme",
        "{title} main title theme",
        "{title} theme song",
        "{title} original soundtrack main title",
        "{title} intro",
        "{title} soundtrack main theme",
    };

    /// <summary>The film ladder, most specific first.</summary>
    public static readonly string[] Movies =
    {
        "{title} {year} main theme soundtrack",
        "{title} {composer} main theme",
        "{title} main title theme",
        "{title} original soundtrack",
        "{title} theme song",
        "{title} soundtrack suite",
    };

    /// <summary>Every series ladder a previous release shipped.</summary>
    private static readonly string[][] PreviousSeries =
    {
        new[]
        {
            "{title} opening theme song",
            "{title} main title theme",
            "{title} theme song",
            "{title} intro",
            "{title} soundtrack main theme",
        },
    };

    /// <summary>Every film ladder a previous release shipped.</summary>
    private static readonly string[][] PreviousMovies =
    {
        new[]
        {
            "{title} {year} main theme soundtrack",
            "{title} main title theme",
            "{title} theme song",
            "{title} soundtrack suite",
        },
    };

    /// <summary>
    /// Replaces a ladder that is still exactly a previous default with the current one.
    /// </summary>
    /// <param name="configuration">The saved settings.</param>
    /// <returns><see langword="true"/> when something changed and the settings should be saved.</returns>
    public static bool Upgrade(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var changed = false;

        if (PreviousSeries.Any(previous => previous.SequenceEqual(configuration.SeriesQueryTemplates ?? Array.Empty<string>(), StringComparer.Ordinal)))
        {
            configuration.SeriesQueryTemplates = Series.ToArray();
            changed = true;
        }

        if (PreviousMovies.Any(previous => previous.SequenceEqual(configuration.MovieQueryTemplates ?? Array.Empty<string>(), StringComparer.Ordinal)))
        {
            configuration.MovieQueryTemplates = Movies.ToArray();
            changed = true;
        }

        return changed;
    }
}
