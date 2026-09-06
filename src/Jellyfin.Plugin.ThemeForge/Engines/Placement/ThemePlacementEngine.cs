using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Placement;

/// <summary>The outcome of trying to publish a theme into the library.</summary>
/// <param name="Success">Whether the theme was written.</param>
/// <param name="Path">Where it was written, when successful.</param>
/// <param name="Reason">Why it was not written, when unsuccessful.</param>
public sealed record PlacementResult(bool Success, string? Path, string? Reason)
{
    /// <summary>Creates a successful result.</summary>
    /// <param name="path">Where the theme was written.</param>
    /// <returns>The result.</returns>
    public static PlacementResult Placed(string path) => new(true, path, null);

    /// <summary>Creates a skipped or failed result.</summary>
    /// <param name="reason">Why nothing was written.</param>
    /// <returns>The result.</returns>
    public static PlacementResult Skipped(string reason) => new(false, null, reason);
}

/// <summary>Publishes a finished theme into the media library.</summary>
public interface IThemePlacementEngine
{
    /// <summary>
    /// Works out where an item's theme belongs.
    /// </summary>
    /// <param name="item">The library item.</param>
    /// <param name="configuration">Settings, which decide whether a shared folder is acceptable.</param>
    /// <returns>The directory, or a reason it cannot be determined.</returns>
    (string? Directory, string? Reason) ResolveThemeDirectory(BaseItem item, PluginConfiguration configuration);

    /// <summary>Reports whether an item already has a theme file on disk.</summary>
    /// <param name="item">The library item.</param>
    /// <param name="configuration">Settings.</param>
    /// <returns><c>true</c> when a theme is already present.</returns>
    bool HasExistingTheme(BaseItem item, PluginConfiguration configuration);

    /// <summary>Publishes a staged theme into the library.</summary>
    /// <param name="item">The library item.</param>
    /// <param name="audio">The staged, verified theme.</param>
    /// <param name="configuration">Settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    Task<PlacementResult> PlaceAsync(BaseItem item, AcquiredAudio audio, PluginConfiguration configuration, CancellationToken cancellationToken);
}

/// <summary>
/// Writes <c>theme.mp3</c> beside an item and prompts Jellyfin to notice it.
/// </summary>
/// <remarks>
/// Jellyfin finds theme music by looking for a file named <c>theme.*</c> in the item's own
/// folder, or any audio inside a <c>theme-music</c> folder there. That convention is the reason
/// this engine cares so much about which directory an item really owns.
/// </remarks>
public sealed class ThemePlacementEngine : IThemePlacementEngine
{
    private const string ThemeFileName = "theme.mp3";

    private readonly IFileSystem _fileSystem;
    private readonly ILogger<ThemePlacementEngine> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemePlacementEngine"/> class.</summary>
    /// <param name="fileSystem">Jellyfin's file system abstraction, needed to trigger a refresh.</param>
    /// <param name="logger">Logger.</param>
    public ThemePlacementEngine(IFileSystem fileSystem, ILogger<ThemePlacementEngine> logger)
    {
        _fileSystem = fileSystem;
        _logger = logger;
    }

    /// <inheritdoc />
    public (string? Directory, string? Reason) ResolveThemeDirectory(BaseItem item, PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuration);

        if (string.IsNullOrEmpty(item.Path))
        {
            return (null, "the item has no path on disk");
        }

        // A film that shares its folder with other films has no directory of its own. Writing
        // theme.mp3 there would hand the same theme to every film in that folder, so refuse
        // rather than quietly corrupt the rest of the library.
        if (item.IsInMixedFolder && configuration.RequireDedicatedFolder)
        {
            return (null, "the item shares a folder with other media, so a theme written there would apply to all of them; give it its own folder, or turn off \"require a dedicated folder\"");
        }

        var directory = item.ContainingFolderPath;
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return (null, string.Format(CultureInfo.InvariantCulture, "the folder \"{0}\" does not exist or is not readable", directory));
        }

        return (directory, null);
    }

    /// <inheritdoc />
    public bool HasExistingTheme(BaseItem item, PluginConfiguration configuration)
    {
        var (directory, _) = ResolveThemeDirectory(item, configuration);
        if (directory is null)
        {
            return false;
        }

        // Both shapes Jellyfin recognises count as "already has a theme".
        if (Directory.EnumerateFiles(directory, "theme.*").Any())
        {
            return true;
        }

        var themeMusic = Path.Combine(directory, "theme-music");
        return Directory.Exists(themeMusic) && Directory.EnumerateFiles(themeMusic).Any();
    }

    /// <inheritdoc />
    public async Task<PlacementResult> PlaceAsync(
        BaseItem item,
        AcquiredAudio audio,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(configuration);

        var (directory, reason) = ResolveThemeDirectory(item, configuration);
        if (directory is null)
        {
            return PlacementResult.Skipped(reason ?? "the theme directory could not be resolved");
        }

        var target = Path.Combine(directory, ThemeFileName);

        try
        {
            if (File.Exists(target))
            {
                if (!configuration.OverwriteExisting)
                {
                    return PlacementResult.Skipped("a theme already exists here and overwriting is disabled");
                }

                if (configuration.BackupExistingThemes)
                {
                    BackUp(target);
                }
            }

            // Copy to a temporary name in the destination folder and move into place, so a
            // reader never sees a partially written theme and a failed copy leaves the
            // previous one intact. Same-directory moves are atomic on every supported platform.
            var temporary = Path.Combine(directory, ".themeforge-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(audio.StagingPath, temporary, overwrite: true);
            File.Move(temporary, target, overwrite: true);

            _logger.LogInformation("ThemeForge: wrote the theme for \"{Item}\" to {Path}.", item.Name, target);

            await RefreshAsync(item, cancellationToken).ConfigureAwait(false);
            return PlacementResult.Placed(target);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "ThemeForge: no permission to write a theme into {Directory}.", directory);
            return PlacementResult.Skipped($"no permission to write into \"{directory}\"");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "ThemeForge: could not write the theme for \"{Item}\".", item.Name);
            return PlacementResult.Skipped($"could not write the theme: {ex.Message}");
        }
    }

    /// <summary>Moves an existing theme aside rather than destroying somebody's manual choice.</summary>
    private void BackUp(string target)
    {
        var backup = string.Format(
            CultureInfo.InvariantCulture,
            "{0}.themeforge-backup-{1:yyyyMMddHHmmss}",
            target,
            DateTime.UtcNow);

        try
        {
            File.Move(target, backup, overwrite: false);
            _logger.LogInformation("ThemeForge: kept the previous theme as {Path}.", backup);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not back up the existing theme at {Path}; it will be replaced.", target);
        }
    }

    /// <summary>
    /// Asks Jellyfin to re-read the item so the new theme appears without waiting for a full
    /// library scan. Failure here is not fatal — the theme is on disk and the next scan finds it.
    /// </summary>
    private async Task RefreshAsync(BaseItem item, CancellationToken cancellationToken)
    {
        try
        {
            await item.RefreshMetadata(
                new MetadataRefreshOptions(new DirectoryService(_fileSystem))
                {
                    MetadataRefreshMode = MetadataRefreshMode.None,
                    ImageRefreshMode = MetadataRefreshMode.None,
                    ReplaceAllMetadata = false,
                    ReplaceAllImages = false,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "ThemeForge: could not refresh \"{Item}\"; the theme is written and will appear after the next library scan.",
                item.Name);
        }
    }
}
