#nullable enable

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Client for ThemerrDB (https://github.com/LizardByte/ThemerrDB), a community
    /// curated mapping from movies and TV shows to their theme song on YouTube.
    ///
    /// Entries are served as static JSON keyed by provider ID; a missing entry is a
    /// plain 404. The theme song, when present, is in "youtube_theme_url".
    /// </summary>
    public class ThemerrDbService
    {
        private const string BaseUrl = "https://app.lizardbyte.dev/ThemerrDB";

        private readonly ILogger<ThemerrDbService> _logger;
        private readonly BrowserHttpClient _httpClient;

        public ThemerrDbService(ILogger<ThemerrDbService> logger, BrowserHttpClient httpClient)
        {
            _logger = logger;
            _httpClient = httpClient;
        }

        /// <summary>
        /// Looks up the theme song URL for an item.
        /// </summary>
        /// <param name="tmdbId">TMDB ID, if known.</param>
        /// <param name="imdbId">IMDb ID, if known.</param>
        /// <param name="isSeries">True for a TV show, false for a movie.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The YouTube theme song URL, or null when ThemerrDB has no entry.</returns>
        public async Task<string?> GetThemeSongUrlAsync(
            string? tmdbId,
            string? imdbId,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            // ThemerrDB indexes TV shows by TMDB ID only; movies are indexed by both.
            var category = isSeries ? "tv_shows" : "movies";

            if (!string.IsNullOrEmpty(tmdbId))
            {
                var url = await QueryAsync($"{BaseUrl}/{category}/themoviedb/{tmdbId}.json", cancellationToken)
                    .ConfigureAwait(false);
                if (url != null)
                {
                    return url;
                }
            }

            if (!string.IsNullOrEmpty(imdbId) && !isSeries)
            {
                var url = await QueryAsync($"{BaseUrl}/{category}/imdb/{imdbId}.json", cancellationToken)
                    .ConfigureAwait(false);
                if (url != null)
                {
                    return url;
                }
            }

            return null;
        }

        /// <summary>
        /// Fetches one ThemerrDB document and reads its theme song URL.
        /// </summary>
        private async Task<string?> QueryAsync(string requestUrl, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Querying ThemerrDB: {Url}", requestUrl);

            var body = await _httpClient
                .GetStringOrNullAsync(requestUrl, expectJson: true, referer: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);

                if (!document.RootElement.TryGetProperty("youtube_theme_url", out var themeUrl))
                {
                    _logger.LogDebug("ThemerrDB entry at {Url} has no theme song", requestUrl);
                    return null;
                }

                var value = themeUrl.ValueKind == JsonValueKind.String ? themeUrl.GetString() : null;
                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                _logger.LogInformation("ThemerrDB returned theme song {ThemeUrl}", value);
                return value;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse ThemerrDB response from {Url}", requestUrl);
                return null;
            }
        }
    }
}
