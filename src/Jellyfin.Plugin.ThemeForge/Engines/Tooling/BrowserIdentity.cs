using System;
using System.Net.Http;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>
/// The browser ThemeForge presents itself as when reading IMDb.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately not <see cref="Engines.Credits.PoliteRequest"/>, and the difference is the
/// point. Every other source is asked in ThemeForge's own name, because Wikimedia and MusicBrainz
/// ask to be told who is calling and it is the courteous thing to do. IMDb does not offer that
/// bargain: it answers a request that identifies itself as a program with <c>403</c>, and a
/// browser-shaped one with a challenge. Reading it at all means looking like a browser.
/// </para>
/// <para>
/// Keeping that in one clearly-named place, used by nothing but the IMDb source, is what stops it
/// leaking into the requests that are made honestly.
/// </para>
/// </remarks>
public static class BrowserIdentity
{
    /// <summary>Gets the user agent sent to IMDb, and given to the headless browser.</summary>
    /// <remarks>
    /// Overridable in the settings so it can be moved on when it starts looking stale to a bot
    /// filter, which is the usual reason this stops working.
    /// </remarks>
    public static string UserAgent
    {
        get
        {
            var configured = Plugin.Instance?.Configuration?.ImdbUserAgent;
            return string.IsNullOrWhiteSpace(configured) ? Default : configured!;
        }
    }

    /// <summary>The user agent used when the settings name none.</summary>
    public const string Default =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    /// <summary>Puts the headers a desktop browser sends on a request.</summary>
    /// <param name="request">The request about to be sent.</param>
    /// <param name="referer">The page a browser would have come from, when there is one.</param>
    public static void Disguise(HttpRequestMessage request, string? referer = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = request.Headers;
        headers.TryAddWithoutValidation("User-Agent", UserAgent);
        headers.TryAddWithoutValidation(
            "Accept",
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

        // Client hints and fetch metadata: Chrome sends these on every navigation, and their
        // absence is on its own enough for a bot filter to notice.
        headers.TryAddWithoutValidation(
            "sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"131\"");
        headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        headers.TryAddWithoutValidation("Sec-Fetch-Site", referer is null ? "none" : "same-origin");
        headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
        headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

        if (!string.IsNullOrEmpty(referer))
        {
            headers.TryAddWithoutValidation("Referer", referer);
        }
    }
}
