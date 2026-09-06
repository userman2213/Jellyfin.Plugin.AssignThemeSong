using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// Looks a film up in ThemerrDB by its TMDB id.
/// </summary>
/// <remarks>
/// <para>
/// ThemerrDB is a community-curated index that maps a film's TMDB id to the video someone chose
/// as its theme. It supplies a link rather than audio, so the download still goes through the
/// ordinary acquisition path — but the link was picked by a person for that exact film, which is
/// a far better starting point than the best guess a search can offer. Coverage measured on
/// popular films is around 85%, and much lower on obscure ones.
/// </para>
/// <para>
/// Films are its strength; its television set was a strict subset of Plex's, so series are not
/// asked for here.
/// </para>
/// </remarks>
public sealed class ThemerrDbSource : IThemeProvenanceSource
{
    private const string Endpoint = "https://app.lizardbyte.dev/ThemerrDB/movies/themoviedb/{0}.json";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<ThemerrDbSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemerrDbSource"/> class.</summary>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ThemerrDbSource(IHttpClientFactory httpClientFactory, IThemeForgeLogger<ThemerrDbSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "ThemerrDB";

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration) =>
        configuration is not null && configuration.UseThemerrDb;

    /// <inheritdoc />
    public async Task<Candidate?> FindAsync(
        MediaIdentity identity,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (identity.IsSeries || string.IsNullOrWhiteSpace(identity.TmdbId))
        {
            return null;
        }

        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, Endpoint, identity.TmdbId);

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var youtubeUrl = ReadYoutubeUrl(body);

            if (youtubeUrl is null)
            {
                return null;
            }

            _logger.LogInformation(
                "ThemeForge: ThemerrDB has a theme for \"{Item}\" (TMDB {TmdbId}).",
                identity.Label,
                identity.TmdbId);

            return new Candidate
            {
                Id = Orchestration.ThemeOrchestrator.ExtractVideoId(youtubeUrl),
                Url = youtubeUrl,
                Title = $"{identity.Title} (ThemerrDB)",
                Channel = "ThemerrDB",
                Description = $"Chosen for TMDB id {identity.TmdbId}.",
                FoundBy = new SearchQuery($"tmdb:{identity.TmdbId}", 0, "provenance"),
                IsHydrated = true,
                Provenance = Name,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _logger.LogDebug(ex, "ThemeForge: could not read ThemerrDB for TMDB {TmdbId}.", identity.TmdbId);
            return null;
        }
    }

    /// <summary>
    /// Reads the theme link out of a ThemerrDB document.
    /// </summary>
    /// <remarks>
    /// Only a YouTube link is accepted. The field is community-supplied, so treating it as a URL
    /// to hand to a downloader without checking what it points at would let an arbitrary entry
    /// choose what the server fetches.
    /// </remarks>
    /// <param name="json">The document body.</param>
    /// <returns>The link, or null when the document has none that can be used.</returns>
    internal static string? ReadYoutubeUrl(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (!document.RootElement.TryGetProperty("youtube_theme_url", out var element)
            || element.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = element.GetString();
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        // Compared whole. Trimming a "www." prefix by characters rather than as a string would
        // also accept "wyoutube.com", which is exactly the sort of thing this check exists to stop.
        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
    }
}
