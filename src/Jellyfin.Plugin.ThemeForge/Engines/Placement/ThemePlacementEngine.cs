using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
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
    /// <param name="policy">The effective policy for this item's library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    Task<PlacementResult> PlaceAsync(BaseItem item, AcquiredAudio audio, PluginConfiguration configuration, ResolvedThemePolicy policy, CancellationToken cancellationToken);
}

/// <summary>
/// Writes <c>theme.&lt;ext&gt;</c> beside an item and prompts Jellyfin to notice it.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin finds theme music by looking for an audio file named <c>theme.*</c> in the item's own
/// folder, or any audio inside a <c>theme-music</c> folder there. That convention is the reason
/// this engine cares so much about which directory an item really owns.
/// </para>
/// <para>
/// The extension is whatever the encoder produced -- a copied Opus stream is <c>theme.opus</c>,
/// a processed one <c>theme.mp3</c> -- so replacing a theme has to clear every <c>theme.*</c>
/// file that is already there, not just the one with the same name. Jellyfin plays all of them
/// otherwise, alternating between the old theme and the new.
/// </para>
/// </remarks>
public sealed class ThemePlacementEngine : IThemePlacementEngine
{
    private const string ThemeStem = "theme";
    private const string BackupMarker = ".themeforge-backup-";

    /// <summary>The audio extensions Jellyfin accepts for a theme file; anything else named theme.* is not one.</summary>
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".m4a", ".m4b", ".aac", ".ogg", ".oga", ".opus", ".flac", ".wav", ".wma", ".mka",
        ".ac3", ".eac3", ".dts", ".aif", ".aiff", ".alac", ".ape", ".mpc", ".wv", ".mp2",
    };

    private readonly IFileSystem _fileSystem;
    private readonly IThemeForgeLogger<ThemePlacementEngine> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemePlacementEngine"/> class.</summary>
    /// <param name="fileSystem">Jellyfin's file system abstraction, needed to trigger a refresh.</param>
    /// <param name="logger">Logger.</param>
    public ThemePlacementEngine(IFileSystem fileSystem, IThemeForgeLogger<ThemePlacementEngine> logger)
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
        if (ExistingThemeFiles(directory).Count > 0)
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
        ResolvedThemePolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(policy);

        var (directory, reason) = ResolveThemeDirectory(item, configuration);
        if (directory is null)
        {
            return PlacementResult.Skipped(reason ?? "the theme directory could not be resolved");
        }

        var target = Path.Combine(directory, ThemeStem + Path.GetExtension(audio.StagingPath));

        try
        {
            var existing = ExistingThemeFiles(directory);
            if (existing.Count > 0 && policy.Overwrite == ThemeOverwritePolicy.Never)
            {
                return PlacementResult.Skipped("a theme already exists here and this library is set never to replace one");
            }

            // Copy to a temporary name in the destination folder first and move into place at the
            // end: a reader never sees a partially written theme, a failed copy leaves the previous
            // one intact, and the old theme is only cleared once the new one is safely in the
            // folder beside it. Same-directory moves are atomic on every supported platform.
            var temporary = Path.Combine(directory, ".themeforge-" + Guid.NewGuid().ToString("N") + ".tmp");
            File.Copy(audio.StagingPath, temporary, overwrite: true);

            foreach (var file in existing)
            {
                if (string.Equals(file, target, StringComparison.Ordinal))
                {
                    // Replaced by the move below; backed up first when asked.
                    if (configuration.BackupExistingThemes)
                    {
                        BackUp(file);
                    }

                    continue;
                }

                // A theme under another extension would play alongside the new one.
                if (!configuration.BackupExistingThemes || !BackUp(file))
                {
                    File.Delete(file);
                }
            }

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

    /// <summary>
    /// The theme files Jellyfin would play from this folder: <c>theme.&lt;audio&gt;</c>, not our
    /// backups of them and not a stray file that merely shares the name.
    /// </summary>
    internal static IReadOnlyList<string> ExistingThemeFiles(string directory) =>
        Directory.EnumerateFiles(directory, ThemeStem + ".*")
            .Where(path =>
                !path.Contains(BackupMarker, StringComparison.Ordinal)
                && AudioExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

    /// <summary>Moves an existing theme aside rather than destroying somebody's manual choice.</summary>
    /// <returns><see langword="true"/> when the file was moved; a caller that must clear it deletes it otherwise.</returns>
    private bool BackUp(string target)
    {
        var backup = string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}{2:yyyyMMddHHmmss}",
            target,
            BackupMarker,
            DateTime.UtcNow);

        try
        {
            File.Move(target, backup, overwrite: false);
            _logger.LogInformation("ThemeForge: kept the previous theme as {Path}.", backup);
            return true;
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not back up the existing theme at {Path}; it will be replaced.", target);
            return false;
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
