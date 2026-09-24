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
/// The last source asked: only about works that Wikidata and Wikipedia could not name a theme for,
/// and only about works with an IMDb id, since the listing is keyed on the id and nothing is
/// searched for by name.
/// </para>
/// <para>
/// The pages are built for a browser, so the request sends what a browser sends. Some networks are
/// answered with an Amazon bot check instead of the page -- a <c>2xx</c> status carrying a script
/// that clears it -- and where that happens the page is rendered in headless Chrome instead. The
/// direct request is always tried first: it is an order of magnitude faster, and on a connection
/// that is never challenged it is all that is needed.
/// </para>
/// <para>
/// A refusal is not a miss. A work IMDb did not answer about is reported as failed, so it is asked
/// again on the next run rather than remembered as having no theme for a fortnight.
/// </para>
/// </remarks>
public sealed class ImdbSoundtrackSource : ICreditsSource
{
    /// <summary>Where a title's soundtrack listing lives.</summary>
    public const string TitleUrl = "https://www.imdb.com/title/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long to leave between requests. One a couple of seconds is plenty for a source consulted
    /// only about what nothing else could answer, and going through a library faster than that is
    /// the quickest way to wear out a welcome.
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
    /// <remarks>
    /// The listing credits who wrote and who performed each entry, so it answers both questions:
    /// what the theme is called, and who scored the work.
    /// </remarks>
    public CreditsQuestion Answers => CreditsQuestion.Composers | CreditsQuestion.Theme;

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

                // Who scored the work is a separate question from what its theme is called, and the
                // listing can answer one without the other: a film that names no theme still credits
                // its score cues, and a series with one entry names a theme and no score.
                var composers = ImdbSoundtrackPage.ScoreComposers(listing.Entries);
                var themeComposers = theme is null
                    ? (IReadOnlyList<string>)Array.Empty<string>()
                    : ImdbSoundtrackPage.SplitNames(theme.Writer);

                if (theme is null && composers.Count == 0)
                {
                    // A film listing that names no theme is the common case, and taking its first
                    // entry would be taking whatever song plays first. See ImdbSoundtrackPage.
                    _logger.LogDebug(
                        "IMDb lists {Count} entries for {Label}, none of them a theme and no writer "
                        + "credited more than once",
                        listing.Entries.Count,
                        work.Label);
                }
                else
                {
                    var credits = new ResearchedCredits(composers, null, null)
                    {
                        Theme = theme is null ? null : new ThemeSong(theme.Title, theme.Performer),
                        ThemeComposers = themeComposers,
                    };

                    found[work.Key] = credits;

                    if (theme is not null)
                    {
                        _logger.LogInformation(
                            "IMDb names {Theme} as the theme for {Label}", credits.Theme, work.Label);
                    }

                    if (composers.Count > 0)
                    {
                        _logger.LogInformation(
                            "IMDb credits {Composers} with the music of {Label}",
                            string.Join(", ", composers),
                            work.Label);
                    }
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
                "IMDb did not answer the direct request for {Url}, and rendering in a browser is "
                + "switched off.",
                soundtrackUrl);
            return new Listing(Array.Empty<SoundtrackEntry>(), true, false, null);
        }

        _logger.LogDebug(
            "IMDb did not answer the direct request for {Url}; rendering it instead", soundtrackUrl);

        var rendered = await _browser
            .RenderAsync(soundtrackUrl, LooksLikeTheListing, cancellationToken)
            .ConfigureAwait(false);

        if (rendered is null)
        {
            _logger.LogWarning(
                "Could not read {Url}, in a browser or otherwise. Whether this network is served a "
                + "bot check instead of the page depends on where the request comes from; the "
                + "settings page can test it against one title.",
                soundtrackUrl);
            return new Listing(Array.Empty<SoundtrackEntry>(), true, true, null);
        }

        return new Listing(ImdbSoundtrackPage.Parse(rendered), false, true, rendered);
    }

    /// <summary>Tells a rendered listing apart from a check's page standing in for it.</summary>
    /// <param name="html">The document.</param>
    /// <returns><see langword="true"/> when it is the real page.</returns>
    internal static bool LooksLikeTheListing(string html) =>
        html.Contains("__NEXT_DATA__", StringComparison.Ordinal)
        || html.Contains("soundTrack", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Asks IMDb for a page as a browser would.
    /// </summary>
    /// <returns>
    /// The body, or null with <c>refused</c> set when the request was turned away or answered with a
    /// check, rather than the page simply not existing.
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

            // A check arrives with a success status, so the status code alone would let it through
            // as content.
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

    /// <summary>Recognises the interstitial served in place of the page where a check applies.</summary>
    /// <param name="body">The response body.</param>
    /// <returns><see langword="true"/> when it is a challenge.</returns>
    internal static bool IsChallenge(string body)
    {
        // A real listing is well over a megabyte; a check's stub page is a couple of kilobytes.
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
