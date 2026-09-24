using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Reads a title's theme out of its IMDb soundtrack listing.
/// </summary>
/// <remarks>
/// <para>
/// The last source asked, and the only one that is not asked politely. IMDb answers a request that
/// identifies itself as a program with <c>403</c>, and a browser-shaped one with an Amazon bot
/// challenge that carries a <c>2xx</c> status and clears only when a browser engine runs its
/// script. Reading it means presenting as a browser and, when that is refused, rendering the page
/// in headless Chrome. IMDb's terms do not permit automated reading, which is why this is a
/// deliberate, separable source with its own switch rather than part of the ordinary chain.
/// </para>
/// <para>
/// It is asked only about works that Wikidata and Wikipedia could not name a theme for, and only
/// about works with an IMDb id -- the listing is keyed on the id, so nothing is searched for by
/// name. A plain request is tried first because it is an order of magnitude faster and succeeds
/// where IMDb is not challenging the server; the browser is a fallback, not the first move.
/// </para>
/// <para>
/// A challenge is not a miss. A work IMDb refused to answer about is reported as failed, so it is
/// asked again on the next run rather than being remembered as having no theme for a fortnight.
/// </para>
/// </remarks>
public sealed class ImdbSoundtrackSource : ICreditsSource
{
    /// <summary>Where a title's soundtrack listing lives.</summary>
    public const string TitleUrl = "https://www.imdb.com/title/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to leave between requests. IMDb is being read against its wishes; going through it
    /// quickly is both rude and the fastest way to be blocked outright.
    /// </summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(2);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IHeadlessBrowser _browser;
    private readonly IThemeForgeLogger<ImdbSoundtrackSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImdbSoundtrackSource"/> class.</summary>
    /// <param name="httpClientFactory">Makes the plain request.</param>
    /// <param name="browser">Renders the page when the plain request is refused.</param>
    /// <param name="logger">Logger.</param>
    public ImdbSoundtrackSource(
        IHttpClientFactory httpClientFactory,
        IHeadlessBrowser browser,
        IThemeForgeLogger<ImdbSoundtrackSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _browser = browser;
        _logger = logger;
    }

    /// <summary>What came of trying to read one title's listing.</summary>
    /// <param name="Entries">The listing, in the order IMDb gives it.</param>
    /// <param name="Refused">Whether IMDb would not answer, as opposed to having nothing to say.</param>
    /// <param name="UsedBrowser">Whether the browser had to be used.</param>
    /// <param name="Html">The page, kept only for the settings page's test.</param>
    internal sealed record Listing(
        IReadOnlyList<SoundtrackEntry> Entries,
        bool Refused,
        bool UsedBrowser,
        string? Html);

    /// <inheritdoc />
    public string Name => "IMDb";

    /// <inheritdoc />
    /// <remarks>Last: everything else answers without any of this.</remarks>
    public int Order => 30;

    /// <inheritdoc />
    public CreditsQuestion Answers => CreditsQuestion.Theme;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ResearchComposers && configuration.UseImdbSoundtrack;
    }

    /// <inheritdoc />
    public async Task<CreditsAnswer> LookUpAsync(
        IReadOnlyList<CreditsRequest> batch,
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(configuration);

        var found = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var done = 0;
        foreach (var work in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(work.ImdbId))
            {
                // Nothing to look up by. Not a miss: an id may arrive later.
                failed.Add(work.Key);
                continue;
            }

            if (done > 0)
            {
                await Task.Delay(Spacing, cancellationToken).ConfigureAwait(false);
            }

            var listing = await ReadListingAsync(work.ImdbId!, configuration, cancellationToken)
                .ConfigureAwait(false);

            if (listing.Refused)
            {
                _logger.LogDebug("IMDb would not answer about {Label}; it will be asked again", work.Label);
                failed.Add(work.Key);
            }
            else if (listing.Entries.Count == 0)
            {
                _logger.LogDebug("IMDb lists no soundtrack for {Label}", work.Label);
            }
            else
            {
                var theme = ImdbSoundtrackPage.ChooseTheme(listing.Entries, work.IsSeries, work.Label);

                if (theme is null)
                {
                    // A film listing that names no theme is the common case, and taking its first
                    // entry would be taking whatever song plays first. See ImdbSoundtrackPage.
                    _logger.LogDebug(
                        "IMDb lists {Count} entries for {Label}, none of them a theme",
                        listing.Entries.Count,
                        work.Label);
                }
                else
                {
                    found[work.Key] = new ResearchedCredits(Array.Empty<string>(), null, null)
                    {
                        Theme = new ThemeSong(theme.Title, theme.Performer),
                        ThemeComposers = theme.Writer is null
                            ? Array.Empty<string>()
                            : new[] { theme.Writer },
                    };

                    _logger.LogInformation(
                        "IMDb names {Theme} as the theme for {Label}",
                        found[work.Key].Theme,
                        work.Label);
                }
            }

            done++;
            progress?.Report(done * 100d / batch.Count);
        }

        return new CreditsAnswer(found, failed);
    }

    /// <summary>
    /// Reads one title's listing, trying a plain request and falling back to the browser.
    /// </summary>
    /// <param name="imdbId">The IMDb id.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What came of it.</returns>
    internal async Task<Listing> ReadListingAsync(
        string imdbId,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var titleUrl = TitleUrl + imdbId + "/";
        var soundtrackUrl = titleUrl + "soundtrack/";

        var (html, refused) = await RequestAsync(soundtrackUrl, titleUrl, cancellationToken)
            .ConfigureAwait(false);

        if (html is not null)
        {
            return new Listing(ImdbSoundtrackPage.Parse(html), false, false, html);
        }

        if (!refused)
        {
            return new Listing(Array.Empty<SoundtrackEntry>(), true, false, null);
        }

        if (!configuration.UseImdbBrowser)
        {
            _logger.LogWarning(
                "IMDb refused a plain request for {Url} and rendering in a browser is switched off.",
                soundtrackUrl);
            return new Listing(Array.Empty<SoundtrackEntry>(), true, false, null);
        }

        _logger.LogDebug("IMDb refused a plain request for {Url}; rendering it instead", soundtrackUrl);

        var rendered = await _browser
            .RenderAsync(soundtrackUrl, LooksLikeTheListing, cancellationToken)
            .ConfigureAwait(false);

        if (rendered is null)
        {
            _logger.LogWarning(
                "IMDb's bot check did not clear for {Url}, in a browser or otherwise. This is usual "
                + "on a hosted server and unusual on a home connection; the settings page can test it.",
                soundtrackUrl);
            return new Listing(Array.Empty<SoundtrackEntry>(), true, true, null);
        }

        return new Listing(ImdbSoundtrackPage.Parse(rendered), false, true, rendered);
    }

    /// <summary>Tells a rendered listing apart from a challenge standing in for it.</summary>
    /// <param name="html">The document.</param>
    /// <returns><see langword="true"/> when it is the real page.</returns>
    internal static bool LooksLikeTheListing(string html) =>
        html.Contains("__NEXT_DATA__", StringComparison.Ordinal)
        || html.Contains("soundTrack", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks IMDb for a page as a browser would.
    /// </summary>
    /// <returns>
    /// The body, or null with <c>refused</c> set when a bot filter turned the request away rather
    /// than the page simply not existing.
    /// </returns>
    private async Task<(string? Html, bool Refused)> RequestAsync(
        string url,
        string referer,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = RequestTimeout;

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            BrowserIdentity.Disguise(request, referer);

            using var response = await client
                .SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return (null, false);
            }

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            {
                return (null, true);
            }

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug(
                    "IMDb answered {Status} for {Url}", (int)response.StatusCode, url);
                return (null, false);
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            // The challenge arrives with a success status, so the status code alone would let it
            // through as content.
            if (IsChallenge(body))
            {
                return (null, true);
            }

            return (body, false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("The request to {Url} timed out", url);
            return (null, true);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "The request to {Url} failed", url);
            return (null, false);
        }
    }

    /// <summary>Recognises the interstitial a bot filter serves in place of the page.</summary>
    /// <param name="body">The response body.</param>
    /// <returns><see langword="true"/> when it is a challenge.</returns>
    internal static bool IsChallenge(string body)
    {
        // A real listing is well over a megabyte; a challenge stub is a couple of kilobytes.
        if (string.IsNullOrEmpty(body) || body.Length > 20000)
        {
            return false;
        }

        return body.Contains("awswaf", StringComparison.OrdinalIgnoreCase)
            || body.Contains("gokuProps", StringComparison.Ordinal)
            || body.Contains("Human Verification", StringComparison.OrdinalIgnoreCase)
            || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase);
    }
}
