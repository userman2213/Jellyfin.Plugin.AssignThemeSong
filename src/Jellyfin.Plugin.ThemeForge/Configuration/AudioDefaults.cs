using System;

namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// The audio processing defaults ThemeForge ships, and the ones it used to.
/// </summary>
/// <remarks>
/// Until 2.3 every theme was normalised to -23 LUFS and faded in and out, and both were on by
/// default -- so an upload at an ordinary volume came out markedly quieter than the rest of the
/// library. Saved settings keep the values they were saved with, so switching the defaults off in
/// code reaches nobody who has already installed the plugin. The distinction
/// <see cref="ShippedTemplates"/> draws applies here too: settings still exactly at the previous
/// defaults were never a decision and are moved to the current ones; anything edited is left alone.
/// </remarks>
public static class AudioDefaults
{
    /// <summary>
    /// Moves processing settings that are still the previous shipped defaults to the current ones.
    /// </summary>
    /// <param name="configuration">The saved settings.</param>
    /// <returns><see langword="true"/> when something changed and the settings should be saved.</returns>
    public static bool Upgrade(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!IsPreviousDefault(configuration))
        {
            return false;
        }

        configuration.EnableLoudnessNormalization = false;
        configuration.FadeInSeconds = 0;
        configuration.FadeOutSeconds = 0;
        return true;
    }

    /// <summary>Whether every processing setting is exactly what releases before 2.3 shipped.</summary>
    /// <param name="configuration">The saved settings.</param>
    /// <returns><see langword="true"/> when nothing has been edited since those defaults.</returns>
    internal static bool IsPreviousDefault(PluginConfiguration configuration) =>
        configuration.EnableLoudnessNormalization
        && Same(configuration.TargetLoudnessLufs, -23)
        && Same(configuration.TargetTruePeakDb, -1.5)
        && Same(configuration.TargetLoudnessRange, 11)
        && Same(configuration.FadeInSeconds, 0.5)
        && Same(configuration.FadeOutSeconds, 3);

    private static bool Same(double actual, double expected) => Math.Abs(actual - expected) < 0.0001;
}
