using System;
using System.Net.Http;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>
/// The browser ThemeForge presents itself as when reading IMDb.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="Engines.Credits.PoliteRequest"/>, which names ThemeForge to the
/// databases that ask to be told who is calling. IMDb's pages are built for a browser and answer a
/// request that does not look like one with <c>403</c>, so this sends what a browser sends, down to
/// the client hints and fetch metadata a page load carries.
/// </para>
/// <para>
/// Confined to one clearly-named place, used by nothing but the IMDb source, so the requests made
/// in ThemeForge's own name stay that way.
/// </para>
/// </remarks>
public static class BrowserIdentity
{
    /// <summary>Gets the user agent sent to IMDb, and given to the headless browser.</summary>
    /// <remarks>
    /// Overridable in the settings so it can be moved on if it goes stale.
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

        // Client hints and fetch metadata, which Chrome sends on every navigation.
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
