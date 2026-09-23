#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using Jellyfin.Data.Enums;
using MediaBrowser.Common;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Jellyfin.Plugin.xThemeSong.Models;
using Jellyfin.Plugin.xThemeSong.Services;

namespace Jellyfin.Plugin.xThemeSong
{
    public class ThemeSongTask : IScheduledTask
    {
        private readonly ILogger<ThemeSongTask> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly ThemeDownloadService _downloadService;
        private readonly ThemeResolverService _resolverService;
        private readonly LookupRetryCache _retryCache;

        public ThemeSongTask(
            ILogger<ThemeSongTask> logger,
            ILibraryManager libraryManager,
            ThemeDownloadService downloadService,
            ThemeResolverService resolverService,
            LookupRetryCache retryCache)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _downloadService = downloadService;
            _resolverService = resolverService;
            _retryCache = retryCache;
        }

        /// <summary>
        /// Gets the plugin configuration safely.
        /// </summary>
        private PluginConfiguration GetConfiguration()
        {
            return Plugin.Instance?.Configuration ?? new PluginConfiguration();
        }

        public string Name => "xTheme Songs";

        public string Description => "Scans the library and assigns theme songs to media items.";

        public string Category => "xThemeSong";

        public string Key => "xThemeSongTask";

        public bool IsHidden => false;

        public bool IsEnabled => true;

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            _logger.LogInformation("xThemeSong task started.");

            var config = GetConfiguration();
            _logger.LogInformation($"Overwrite Existing Files: {config.OverwriteExistingFiles}");
            _logger.LogInformation($"Audio Bitrate: {config.AudioBitrate}");

            // Get only Movies and Series from the library to avoid deserialization errors
            var mediaItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series },
                Recursive = true
            }).ToList();

            var totalItems = mediaItems.Count;
            var processedItems = 0;

            foreach (var item in mediaItems)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                _logger.LogInformation($"Processing item: {item.Name}");
                await ProcessMediaItem(item, config, cancellationToken);

                processedItems++;
                var percentComplete = (double)processedItems / totalItems * 100;
                progress.Report(percentComplete);
            }

            _logger.LogInformation("xThemeSong task finished.");
        }

        /// <summary>
        /// Gets the correct directory for storing theme files based on item type.
        /// For Series: Use the series folder directly (item.Path is the folder)
        /// For Movie: Use the parent directory of the movie file
        /// </summary>
        private string? GetThemeDirectory(BaseItem item)
        {
            var itemPath = item.Path;
            if (string.IsNullOrEmpty(itemPath))
            {
                return null;
            }

            // For Series, the Path IS the series folder
            if (item is Series)
            {
                _logger.LogDebug("Item is Series, using path directly: {Path}", itemPath);
                return itemPath;
            }

            // For Movie and other file-based items, use the containing directory
            var directory = Path.GetDirectoryName(itemPath);
            _logger.LogDebug("Item is {Type}, using directory: {Path}", item.GetType().Name, directory);
            return directory;
        }

        private async Task ProcessMediaItem(BaseItem item, PluginConfiguration config, CancellationToken cancellationToken)
        {
            // Determine the path where the theme song should be saved
            var itemDirectory = GetThemeDirectory(item);
            if (string.IsNullOrEmpty(itemDirectory))
            {
                _logger.LogWarning("Item {ItemName} has no valid path, skipping.", item.Name);
                return;
            }

            _logger.LogDebug("Theme directory for {ItemName} ({ItemType}): {Directory}", 
                item.Name, item.GetType().Name, itemDirectory);

            var themeSongFilePath = Path.Combine(itemDirectory, "theme.mp3");
            var themeJsonPath = Path.Combine(itemDirectory, "theme.json");

            // Check if theme song already exists
            var themeSongExists = File.Exists(themeSongFilePath);

            // Feature 2: Handle theme.mp3 without theme.json - skip if mp3 exists but no json
            if (themeSongExists && !File.Exists(themeJsonPath))
            {
                _logger.LogInformation("Theme.mp3 exists without theme.json for {ItemName}, skipping (existing manual theme).", item.Name);
                return;
            }

            if (themeSongExists && !config.OverwriteExistingFiles)
            {
                _logger.LogInformation($"Theme song already exists for {item.Name} and overwrite is disabled. Skipping.");
                return;
            }

            _logger.LogInformation($"Attempting to assign theme song for {item.Name}.");

            // No theme.json means nothing has been assigned yet, so look one up.
            if (!File.Exists(themeJsonPath))
            {
                await LookupAndAssignTheme(item, itemDirectory, themeJsonPath, config, cancellationToken);
                return;
            }

            try
            {
                var themeMetadata = JsonSerializer.Deserialize<ThemeMetadata>(
                    await File.ReadAllTextAsync(themeJsonPath, cancellationToken));

                if (themeMetadata == null)
                {
                    _logger.LogWarning("Invalid theme.json for {ItemName} at {Path}, skipping.", item.Name, themeJsonPath);
                    return;
                }

                if (themeMetadata.IsUserUploaded)
                {
                    _logger.LogDebug("Theme song for {ItemName} is user uploaded, skipping scheduled download.", item.Name);
                    return;
                }

                if (string.IsNullOrEmpty(themeMetadata.YouTubeId))
                {
                    _logger.LogInformation(
                        "theme.json for {ItemName} has no YouTube ID, looking one up.", item.Name);
                    await LookupAndAssignTheme(item, itemDirectory, themeJsonPath, config, cancellationToken);
                    return;
                }

                // A theme recorded by the automatic lookup is only downloaded when the
                // user has asked for that. Without this the "record but do not download"
                // setting would just postpone the download to the next run.
                if (!config.AutoDownloadFoundThemes && IsAutomaticSource(themeMetadata.Source))
                {
                    _logger.LogInformation(
                        "Theme song for {ItemName} was found automatically ({Source}) and automatic "
                        + "downloading is off; leaving it for review.",
                        item.Name,
                        themeMetadata.Source);
                    return;
                }

                _logger.LogInformation("Downloading theme song for {ItemName} from YouTube ID: {YouTubeId}", item.Name, themeMetadata.YouTubeId);

                try
                {
                    await _downloadService.DownloadFromYouTube(
                        themeMetadata.YouTubeId,
                        itemDirectory,
                        config.AudioBitrate,
                        cancellationToken);

                    _logger.LogInformation("Successfully downloaded theme song for {ItemName}", item.Name);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to download theme song for {ItemName} from YouTube ID {YouTubeId}", item.Name, themeMetadata.YouTubeId);
                }
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Error deserializing theme.json for {ItemName} at {Path}", item.Name, themeJsonPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing theme song for {ItemName}", item.Name);
            }
        }

        /// <summary>
        /// Tells an automatically found theme apart from one the user entered by hand,
        /// which has no source recorded.
        /// </summary>
        private static bool IsAutomaticSource(string? source)
        {
            return Enum.TryParse<ThemeLookupSource>(source, out var parsed)
                && parsed != ThemeLookupSource.None;
        }

        /// <summary>
        /// Runs the automatic lookup chain for an item and records what it found.
        /// ThemerrDB is tried first; a soundtrack listing is the fallback.
        /// </summary>
        private async Task LookupAndAssignTheme(
            BaseItem item,
            string itemDirectory,
            string themeJsonPath,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            if (!config.EnableThemerrDb && !config.EnableSoundtrackFallback)
            {
                _logger.LogDebug("Automatic theme lookup is disabled, skipping {ItemName}.", item.Name);
                return;
            }

            if (_retryCache.ShouldSkip(item.Id, config.LookupRetryDays))
            {
                _logger.LogDebug(
                    "A recent lookup for {ItemName} found nothing, not retrying yet.", item.Name);
                return;
            }

            ThemeLookupResult lookup;
            try
            {
                lookup = await _resolverService.ResolveAsync(item, config, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Theme lookup failed for {ItemName}", item.Name);
                _retryCache.RecordFailure(item.Id);
                return;
            }

            if (!lookup.HasTheme)
            {
                _logger.LogInformation("No theme song found for {ItemName}.", item.Name);
                _retryCache.RecordFailure(item.Id);

                // Keep a soundtrack listing even without a match, so the listing does
                // not have to be fetched again when the user picks a theme by hand.
                if (lookup.Soundtrack.Count > 0)
                {
                    await SaveLookupMetadata(lookup, themeJsonPath, cancellationToken);
                }

                return;
            }

            _retryCache.Clear(item.Id);

            if (!config.AutoDownloadFoundThemes)
            {
                _logger.LogInformation(
                    "Recording theme song {Url} for {ItemName} without downloading it.",
                    lookup.YouTubeUrl,
                    item.Name);
                await SaveLookupMetadata(lookup, themeJsonPath, cancellationToken);
                return;
            }

            try
            {
                // The download writes theme.json itself, including the provenance.
                await _downloadService.DownloadFromYouTube(
                    lookup.YouTubeId!,
                    itemDirectory,
                    config.AudioBitrate,
                    cancellationToken,
                    lookup);

                _logger.LogInformation(
                    "Assigned theme song for {ItemName} from {Source}.", item.Name, lookup.Source);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to download the theme song found for {ItemName} ({Url})",
                    item.Name,
                    lookup.YouTubeUrl);

                // Record what was found so a later run can retry without looking it up again.
                await SaveLookupMetadata(lookup, themeJsonPath, cancellationToken);
            }
        }

        /// <summary>
        /// Writes a lookup result to theme.json without downloading any audio.
        /// </summary>
        private async Task SaveLookupMetadata(
            ThemeLookupResult lookup,
            string themeJsonPath,
            CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            var metadata = new ThemeMetadata
            {
                YouTubeId = lookup.YouTubeId,
                YouTubeUrl = lookup.YouTubeUrl,
                Title = lookup.Title,
                DateAdded = now,
                DateModified = now,
                IsUserUploaded = false,
                Source = lookup.Source.ToString(),
                ImdbId = lookup.ImdbId,
                Soundtrack = lookup.Soundtrack.Count > 0 ? lookup.Soundtrack : null
            };

            try
            {
                var json = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(themeJsonPath, json, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not write theme.json at {Path}", themeJsonPath);
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Daily trigger at 2 AM
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.DailyTrigger,
                    TimeOfDayTicks = TimeSpan.FromHours(2).Ticks
                }
            };
        }
    }
}
