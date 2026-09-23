#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.xThemeSong.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Search;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Finds a theme song for a media item.
    ///
    /// ThemerrDB is asked first because its entries are curated theme songs. When it
    /// has no entry, the soundtrack listing for the title is pulled instead and its
    /// track names are searched on YouTube, which gives a usable candidate for the
    /// many titles ThemerrDB does not cover.
    /// </summary>
    public class ThemeResolverService
    {
        /// <summary>
        /// Upper bound on a theme song's length. Searching a track name otherwise
        /// tends to surface full album uploads and hour-long compilations.
        /// </summary>
        private static readonly TimeSpan MaxThemeDuration = TimeSpan.FromMinutes(15);

        private readonly ILogger<ThemeResolverService> _logger;
        private readonly ThemerrDbService _themerrDb;
        private readonly SoundtrackLookupService _soundtrackLookup;
        private readonly YoutubeClient _youtube;

        public ThemeResolverService(
            ILogger<ThemeResolverService> logger,
            ThemerrDbService themerrDb,
            SoundtrackLookupService soundtrackLookup)
        {
            _logger = logger;
            _themerrDb = themerrDb;
            _soundtrackLookup = soundtrackLookup;
            _youtube = new YoutubeClient();
        }

        /// <summary>
        /// Resolves a theme song for a media item.
        /// </summary>
        /// <param name="item">The movie or series to find a theme for.</param>
        /// <param name="config">Current plugin configuration.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>
        /// The lookup result. <see cref="ThemeLookupResult.HasTheme"/> is false when
        /// nothing was found; the soundtrack list may still be populated in that case.
        /// </returns>
        public async Task<ThemeLookupResult> ResolveAsync(
            BaseItem item,
            PluginConfiguration config,
            CancellationToken cancellationToken)
        {
            var isSeries = item is Series;
            var result = new ThemeLookupResult
            {
                ImdbId = GetProviderId(item, MetadataProvider.Imdb),
                TmdbId = GetProviderId(item, MetadataProvider.Tmdb)
            };

            _logger.LogInformation(
                "Looking up a theme song for {ItemName} (IMDb: {ImdbId}, TMDB: {TmdbId})",
                item.Name,
                result.ImdbId ?? "none",
                result.TmdbId ?? "none");

            // Source 1: ThemerrDB.
            if (config.EnableThemerrDb)
            {
                var themerrUrl = await _themerrDb
                    .GetThemeSongUrlAsync(result.TmdbId, result.ImdbId, isSeries, cancellationToken)
                    .ConfigureAwait(false);

                if (!string.IsNullOrEmpty(themerrUrl))
                {
                    result.Source = ThemeLookupSource.ThemerrDb;
                    result.YouTubeUrl = themerrUrl;
                    result.YouTubeId = ExtractVideoId(themerrUrl);
                    result.Title = $"{item.Name} (ThemerrDB)";

                    _logger.LogInformation(
                        "ThemerrDB has a theme song for {ItemName}: {Url}", item.Name, themerrUrl);
                    return result;
                }

                _logger.LogInformation(
                    "ThemerrDB has no theme song for {ItemName}, falling back to the soundtrack listing",
                    item.Name);
            }

            // Source 2: the soundtrack listing, searched on YouTube.
            if (!config.EnableSoundtrackFallback)
            {
                return result;
            }

            // The soundtrack listing is keyed by IMDb ID; ask OMDb for one when the
            // item's own metadata does not supply it.
            if (string.IsNullOrEmpty(result.ImdbId))
            {
                result.ImdbId = await _soundtrackLookup
                    .ResolveImdbIdAsync(item.Name, item.ProductionYear, isSeries, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (string.IsNullOrEmpty(result.ImdbId))
            {
                _logger.LogInformation(
                    "No IMDb ID available for {ItemName}, cannot look up its soundtrack", item.Name);
                return result;
            }

            result.Soundtrack = await _soundtrackLookup
                .GetSoundtrackAsync(result.ImdbId!, config.MaxSoundtrackCandidates, cancellationToken)
                .ConfigureAwait(false);

            if (result.Soundtrack.Count == 0)
            {
                return result;
            }

            var video = await FindFirstPlayableTrackAsync(result.Soundtrack, item.Name, cancellationToken)
                .ConfigureAwait(false);

            if (video == null)
            {
                _logger.LogInformation(
                    "None of the {Count} soundtrack entries for {ItemName} matched a YouTube video",
                    result.Soundtrack.Count,
                    item.Name);
                return result;
            }

            result.Source = ThemeLookupSource.Soundtrack;
            result.YouTubeId = video.Id.Value;
            result.YouTubeUrl = $"https://www.youtube.com/watch?v={video.Id.Value}";
            result.Title = video.Title;

            _logger.LogInformation(
                "Soundtrack lookup picked \"{Title}\" for {ItemName}: {Url}",
                video.Title,
                item.Name,
                result.YouTubeUrl);

            return result;
        }

        /// <summary>
        /// Searches YouTube for each soundtrack track in listing order and returns the
        /// first result of plausible theme song length.
        /// </summary>
        private async Task<VideoSearchResult?> FindFirstPlayableTrackAsync(
            IReadOnlyList<SoundtrackTrack> tracks,
            string itemName,
            CancellationToken cancellationToken)
        {
            foreach (var track in tracks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var query = track.ToSearchQuery();
                if (string.IsNullOrWhiteSpace(query))
                {
                    continue;
                }

                try
                {
                    var results = await _youtube.Search
                        .GetVideosAsync(query, cancellationToken)
                        .CollectAsync(5)
                        .ConfigureAwait(false);

                    var match = results.FirstOrDefault(video =>
                        video.Duration.HasValue && video.Duration.Value <= MaxThemeDuration);

                    if (match != null)
                    {
                        return match;
                    }

                    _logger.LogDebug(
                        "No suitable YouTube result for \"{Query}\" ({ItemName})", query, itemName);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "YouTube search failed for \"{Query}\"", query);
                }
            }

            return null;
        }

        /// <summary>
        /// Reads a provider ID off an item, returning null when it is absent or blank.
        /// </summary>
        private static string? GetProviderId(BaseItem item, MetadataProvider provider)
        {
            var value = item.GetProviderId(provider);
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        /// <summary>
        /// Extracts the video ID from a YouTube watch URL.
        /// </summary>
        private static string? ExtractVideoId(string? input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return null;
            }

            if (input.Contains("youtube.com/watch?v=", StringComparison.OrdinalIgnoreCase))
            {
                return input.Split(new[] { "v=" }, StringSplitOptions.None)[1].Split('&')[0];
            }

            if (input.Contains("youtu.be/", StringComparison.OrdinalIgnoreCase))
            {
                return input.Split(new[] { "youtu.be/" }, StringSplitOptions.None)[1].Split('?')[0];
            }

            return input.Length == 11 ? input : null;
        }
    }
}
