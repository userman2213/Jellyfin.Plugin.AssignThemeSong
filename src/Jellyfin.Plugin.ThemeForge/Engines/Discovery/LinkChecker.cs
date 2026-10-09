using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>Whether a theme link still leads anywhere.</summary>
public enum LinkState
{
    /// <summary>It plays.</summary>
    Alive,

    /// <summary>It is gone: the video was removed, or never existed.</summary>
    Dead,

    /// <summary>The check could not say, so the link is given the benefit of the doubt.</summary>
    Unknown,
}

/// <summary>Finds out whether a catalogue's theme link still works, before anything is downloaded.</summary>
public interface ILinkChecker
{
    /// <summary>Checks one link.</summary>
    /// <param name="url">The link.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it still leads anywhere.</returns>
    Task<LinkState> CheckAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Asks YouTube whether a video still exists, through its oEmbed endpoint.
/// </summary>
/// <remarks>
/// <para>
/// ThemerrDB is a list of links that people curated over years, and YouTube removes videos. A
/// sample of 291 entries found 6% of the film links and 2% of the show links answering 404. A dead
/// link was being handed straight to yt-dlp, which failed; the item was marked failed, and the next
/// run asked the catalogue again -- which is never behind a backoff -- got the same dead link and
/// failed again. The item never reached the search, so it never got a theme at all.
/// </para>
/// <para>
/// oEmbed is the cheap question: one small request, no video page, and it does not trip the bot
/// check yt-dlp runs into from some addresses. It answers 404 for a removed video and 400 for an
/// id that was never valid. It answers 401 both for a private video and for one whose owner only
/// switched embedding off -- and yt-dlp downloads the second perfectly well -- so 401 is not taken
/// as dead. Anything the check cannot answer leaves the link alone: a flaky check must never be the
/// reason a good theme goes unused.
/// </para>
/// </remarks>
public sealed class LinkChecker : ILinkChecker
{
    /// <summary>YouTube's oEmbed endpoint.</summary>
    public const string OEmbedEndpoint = "https://www.youtube.com/oembed?format=json&url=";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<LinkChecker> _logger;

    /// <summary>Initializes a new instance of the <see cref="LinkChecker"/> class.</summary>
    /// <param name="httpClientFactory">Makes the request.</param>
    /// <param name="logger">Logger.</param>
    public LinkChecker(IHttpClientFactory httpClientFactory, IThemeForgeLogger<LinkChecker> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<LinkState> CheckAsync(string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url) || !IsYouTube(url))
        {
            // Only YouTube links are checked here; anything else is checked by whoever produced it.
            return LinkState.Unknown;
        }

        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            // Identified as ThemeForge rather than dressed as a browser, unlike the IMDb page: this
            // is YouTube's API, and a browser's page request can be sent to YouTube's cookie consent
            // page instead, which answers 200 for a video that no longer exists.
            using var request = new HttpRequestMessage(HttpMethod.Get, OEmbedEndpoint + Uri.EscapeDataString(url));
            PoliteRequest.Identify(request);

            using var response = await client.SendAsync(request, timeout.Token).ConfigureAwait(false);
            return Interpret(response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "ThemeForge: could not check whether {Url} still exists.", url);
            return LinkState.Unknown;
        }
    }

    /// <summary>Reads oEmbed's answer.</summary>
    /// <param name="status">The status it answered with.</param>
    /// <returns>What it means for the link.</returns>
    internal static LinkState Interpret(HttpStatusCode status) => status switch
    {
        _ when (int)status is >= 200 and < 300 => LinkState.Alive,

        // Removed, or an id that was never valid.
        HttpStatusCode.NotFound or HttpStatusCode.BadRequest or HttpStatusCode.Gone => LinkState.Dead,

        // 401 is a private video or an embed-disabled one, and yt-dlp can fetch the latter. A rate
        // limit or a server error says nothing about the video at all.
        _ => LinkState.Unknown,
    };

    /// <summary>Reports whether a link points at YouTube.</summary>
    internal static bool IsYouTube(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase));
}
