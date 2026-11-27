using System;
using System.Linq;

namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// The words ThemeForge ships for telling a theme from everything else YouTube returns, and the
/// ones it used to.
/// </summary>
/// <remarks>
/// The same bargain as <see cref="ShippedTemplates"/>: a saved list identical to a previous default
/// was never edited, and is brought up to date; a list that differs in any way is somebody's
/// decision and is left alone.
/// </remarks>
public static class ShippedKeywords
{
    /// <summary>Words that disqualify a candidate.</summary>
    /// <remarks>
    /// "arr.", "arranged by" and "arrangement" since 2.10: a concert band's "Game of Thrones (Theme)
    /// by Ramin Djawadi/arr. Brown" names the theme and its composer as well as the soundtrack's own
    /// upload does, and is a cover. "piano", "violin" and "cello" since 2.11, when the main theme
    /// started to rank first: "Amélie Theme - Comptine d'un autre été (PIANO) - Brooklyn Duo" calls
    /// itself the theme, and is a duo's cover of it.
    /// </remarks>
    public static readonly string[] Negative =
    {
        "reaction", "cover", "remix", "tutorial", "lesson", "how to play",
        "1 hour", "10 hours", "hour loop", "loop", "extended", "amv",
        "trailer", "review", "explained", "recap", "full episode", "episode",
        "karaoke", "sheet music", "guitar", "piano tutorial", "8 bit", "8-bit",
        "nightcore", "slowed", "reverb", "fan made", "fanmade", "parody",
        "behind the scenes", "interview", "compilation", "every",
        "joke", "bloopers", "deleted scene", "best of", "funniest", "scene",
        "arr.", "arranged by", "arrangement", "piano", "violin", "cello",
    };

    /// <summary>Every list of disqualifying words a previous release shipped.</summary>
    private static readonly string[][] PreviousNegative =
    {
        // 2.10.
        new[]
        {
            "reaction", "cover", "remix", "tutorial", "lesson", "how to play",
            "1 hour", "10 hours", "hour loop", "loop", "extended", "amv",
            "trailer", "review", "explained", "recap", "full episode", "episode",
            "karaoke", "sheet music", "guitar", "piano tutorial", "8 bit", "8-bit",
            "nightcore", "slowed", "reverb", "fan made", "fanmade", "parody",
            "behind the scenes", "interview", "compilation", "every",
            "joke", "bloopers", "deleted scene", "best of", "funniest", "scene",
            "arr.", "arranged by", "arrangement",
        },

        // Up to 2.9.
        new[]
        {
            "reaction", "cover", "remix", "tutorial", "lesson", "how to play",
            "1 hour", "10 hours", "hour loop", "loop", "extended", "amv",
            "trailer", "review", "explained", "recap", "full episode", "episode",
            "karaoke", "sheet music", "guitar", "piano tutorial", "8 bit", "8-bit",
            "nightcore", "slowed", "reverb", "fan made", "fanmade", "parody",
            "behind the scenes", "interview", "compilation", "every",
            "joke", "bloopers", "deleted scene", "best of", "funniest", "scene",
        },
    };

    /// <summary>
    /// Replaces a list that is still exactly a previous default with the current one.
    /// </summary>
    /// <param name="configuration">The saved settings.</param>
    /// <returns><see langword="true"/> when something changed and the settings should be saved.</returns>
    public static bool Upgrade(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (PreviousNegative.Any(previous => previous.SequenceEqual(configuration.NegativeKeywords ?? Array.Empty<string>(), StringComparer.Ordinal)))
        {
            configuration.NegativeKeywords = Negative.ToArray();
            return true;
        }

        return false;
    }
}
