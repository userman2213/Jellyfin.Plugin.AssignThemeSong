using System;
using System.Net;
using System.Net.Http;
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
/// Looks a series up in Plex's television theme archive by its TheTVDB id.
/// </summary>
/// <remarks>
/// <para>
/// The archive is keyed on the same id Jellyfin already holds, so a hit is the theme for that
/// exact series rather than a video that looked right — which is why it is worth having at all.
/// Coverage measured against a real library is around 43% for American series and 21% worldwide.
/// It is asked after ThemerrDB, which also covers shows; this is the fallback for the ones
/// ThemerrDB does not have.
/// </para>
/// <para>
/// It is <b>off by default and has to be turned on deliberately</b>. The files are hosted by
/// Plex for Plex's own clients, and the fair-use rationale Plex publishes for them — clips capped
/// at thirty seconds, served to their own software — is theirs and does not extend to this
/// plugin. Whether to use it is the server owner's decision to make knowingly, not a default to
/// inherit by accident, so the setting says so in as many words and ships switched off.
/// </para>
/// </remarks>
public sealed class PlexTvThemeSource : IThemeProvenanceSource
{
    private const string Endpoint = "https://tvthemes.plexapp.com/{0}.mp3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<PlexTvThemeSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="PlexTvThemeSource"/> class.</summary>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public PlexTvThemeSource(IHttpClientFactory httpClientFactory, IThemeForgeLogger<PlexTvThemeSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Plex television theme archive";

    /// <inheritdoc />
    /// <remarks>
    /// After ThemerrDB, which covers shows too and is keyed on the same TMDB id Jellyfin already
    /// holds. This one is the fallback for the series ThemerrDB does not have, which is most of
    /// them: it lists around 1300 shows against this archive's several thousand.
    /// </remarks>
    public int Order => 10;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration) =>
        configuration is not null && configuration.UsePlexThemeArchive;

    /// <inheritdoc />
    public async Task<Candidate?> FindAsync(
        MediaIdentity identity,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (!identity.IsSeries || string.IsNullOrWhiteSpace(identity.TvdbId))
        {
            return null;
        }

        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, Endpoint, identity.TvdbId);

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);

            // A HEAD asks the only question that matters -- does this id have a theme -- without
            // pulling the audio for the majority of series that do not.
            using var request = new HttpRequestMessage(HttpMethod.Head, url);
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "ThemeForge: the Plex archive answered {Status} for TVDB {TvdbId}.",
                    (int)response.StatusCode,
                    identity.TvdbId);
                return null;
            }

            _logger.LogInformation(
                "ThemeForge: the Plex archive has a theme for \"{Item}\" (TVDB {TvdbId}).",
                identity.Label,
                identity.TvdbId);

            return new Candidate
            {
                Id = $"plex:{identity.TvdbId}",
                Url = url,
                Title = $"{identity.Title} (Plex theme archive)",
                Channel = "Plex television theme archive",
                Description = $"Keyed on TheTVDB id {identity.TvdbId}.",
                FoundBy = new SearchQuery($"tvdb:{identity.TvdbId}", 0, "provenance"),
                IsHydrated = true,
                Provenance = Name,
                IsDirectAudio = true,
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A catalogue being unreachable is not a reason to fail the item: the search ladder
            // still runs, and the next pass will ask again.
            _logger.LogDebug(ex, "ThemeForge: could not reach the Plex archive for TVDB {TvdbId}.", identity.TvdbId);
            return null;
        }
    }
}
