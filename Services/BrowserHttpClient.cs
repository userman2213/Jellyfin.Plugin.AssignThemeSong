#nullable enable

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// The outcome of a web fetch: the body when one was read, plus whether the request
    /// was turned away by a bot filter rather than simply having nothing to return.
    /// </summary>
    /// <param name="Body">The response body, or null when nothing was read.</param>
    /// <param name="Blocked">True when a bot filter refused or challenged the request.</param>
    public readonly record struct WebFetchResult(string? Body, bool Blocked)
    {
        /// <summary>Nothing to return, and not a block.</summary>
        public static WebFetchResult Empty => new WebFetchResult(null, false);

        /// <summary>The request was refused or challenged by a bot filter.</summary>
        public static WebFetchResult Refused => new WebFetchResult(null, true);
    }

    /// <summary>
    /// Shared HTTP client that presents itself as a normal desktop browser.
    ///
    /// Several of the sources this plugin talks to (IMDb in particular) reject
    /// requests that carry a bare or missing User-Agent with 403 Forbidden, so
    /// every outbound request made by the plugin goes through here and gets the
    /// same header set a real Chrome session would send.
    /// </summary>
    public class BrowserHttpClient : IDisposable
    {
        /// <summary>
        /// Default desktop Chrome user agent. Overridable from plugin settings so
        /// users can bump it when it starts looking stale to a bot filter.
        /// </summary>
        public const string DefaultUserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

        private const string HtmlAccept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8,application/signed-exchange;v=b3;q=0.7";

        private const string JsonAccept = "application/json,text/plain,*/*;q=0.8";

        private static readonly Regex SecretParameterRegex = new Regex(
            @"\b(apikey|api_key|key|token|access_token|password)=[^&]*",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Minimum spacing between outbound requests. A first scan of a large library
        /// would otherwise fire hundreds of requests back to back, which is the quickest
        /// way to get the server's IP address rate limited or blocked outright.
        /// </summary>
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(500);

        private readonly ILogger<BrowserHttpClient> _logger;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _throttle = new SemaphoreSlim(1, 1);
        private DateTime _lastRequestUtc = DateTime.MinValue;
        private bool _disposed;

        public BrowserHttpClient(ILogger<BrowserHttpClient> logger)
        {
            _logger = logger;

            var handler = new HttpClientHandler
            {
                // Browsers always negotiate compression; sites notice when a client doesn't.
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                // A cookie jar keeps redirect/consent flows working across requests.
                CookieContainer = new CookieContainer(),
                UseCookies = true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
        }

        /// <summary>
        /// Gets the user agent to send, preferring the one configured in plugin settings.
        /// Shared with the headless browser so both present the same identity.
        /// </summary>
        public static string GetUserAgent()
        {
            var configured = Plugin.Instance?.Configuration?.UserAgent;
            return string.IsNullOrWhiteSpace(configured) ? DefaultUserAgent : configured!;
        }

        /// <summary>
        /// Builds a request carrying the headers a desktop browser sends.
        /// </summary>
        /// <param name="url">Absolute request URL.</param>
        /// <param name="expectJson">Ask for JSON rather than an HTML document.</param>
        /// <param name="referer">Optional Referer, as a browser would send when following a link.</param>
        private static HttpRequestMessage BuildRequest(string url, bool expectJson, string? referer)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            var headers = request.Headers;

            headers.TryAddWithoutValidation("User-Agent", GetUserAgent());
            headers.TryAddWithoutValidation("Accept", expectJson ? JsonAccept : HtmlAccept);
            headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
            headers.TryAddWithoutValidation("Accept-Encoding", "gzip, deflate, br");
            headers.TryAddWithoutValidation("DNT", "1");
            headers.TryAddWithoutValidation("Connection", "keep-alive");

            // Client hints and fetch metadata: modern Chrome sends these on every
            // navigation, and their absence is a common bot-filter signal.
            headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"131\", \"Not_A Brand\";v=\"24\", \"Google Chrome\";v=\"131\"");
            headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
            headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");

            if (expectJson)
            {
                headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                headers.TryAddWithoutValidation("Sec-Fetch-Site", referer == null ? "none" : "same-origin");
            }
            else
            {
                headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                headers.TryAddWithoutValidation("Sec-Fetch-Site", referer == null ? "none" : "same-origin");
                headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
                headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
            }

            if (!string.IsNullOrEmpty(referer))
            {
                headers.TryAddWithoutValidation("Referer", referer);
            }

            return request;
        }

        /// <summary>
        /// Fetches a URL with browser headers and returns the body, or null when the
        /// response was not a success.
        /// </summary>
        public async Task<string?> GetStringOrNullAsync(
            string url,
            bool expectJson,
            string? referer,
            CancellationToken cancellationToken)
        {
            var result = await GetPageAsync(url, expectJson, referer, cancellationToken)
                .ConfigureAwait(false);
            return result.Body;
        }

        /// <summary>
        /// Fetches a URL with browser headers, reporting separately whether the request
        /// was refused by a bot filter, which a caller can answer by rendering the page
        /// in a real browser instead. 404 is treated as an ordinary "not found" and
        /// logged quietly, since the lookup chain probes several sources in turn.
        /// </summary>
        public async Task<WebFetchResult> GetPageAsync(
            string url,
            bool expectJson,
            string? referer,
            CancellationToken cancellationToken)
        {
            try
            {
                await WaitForTurnAsync(cancellationToken).ConfigureAwait(false);

                using var request = BuildRequest(url, expectJson, referer);
                using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    _logger.LogDebug("No entry at {Url} (404)", Redact(url));
                    return WebFetchResult.Empty;
                }

                if (!response.IsSuccessStatusCode)
                {
                    // 403 and 429 are how a bot filter turns a request away.
                    var blocked = response.StatusCode == HttpStatusCode.Forbidden
                        || response.StatusCode == HttpStatusCode.TooManyRequests;

                    _logger.Log(
                        blocked ? LogLevel.Debug : LogLevel.Warning,
                        "Request to {Url} failed with {StatusCode} ({Reason})",
                        Redact(url),
                        (int)response.StatusCode,
                        response.ReasonPhrase);

                    return blocked ? WebFetchResult.Refused : WebFetchResult.Empty;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (LooksLikeBotChallenge(body))
                {
                    // A challenge arrives with a 2xx status, so the status code alone
                    // would have let this through as content.
                    _logger.LogDebug(
                        "Request to {Url} returned a bot-protection challenge instead of content",
                        Redact(url));
                    return WebFetchResult.Refused;
                }

                return new WebFetchResult(body, false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TaskCanceledException)
            {
                _logger.LogWarning("Request to {Url} timed out", Redact(url));
                return WebFetchResult.Empty;
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Request to {Url} failed", Redact(url));
                return WebFetchResult.Empty;
            }
        }

        /// <summary>
        /// Blocks until enough time has passed since the previous request.
        /// </summary>
        private async Task WaitForTurnAsync(CancellationToken cancellationToken)
        {
            await _throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var sinceLast = DateTime.UtcNow - _lastRequestUtc;
                if (sinceLast < MinimumRequestInterval)
                {
                    await Task.Delay(MinimumRequestInterval - sinceLast, cancellationToken).ConfigureAwait(false);
                }

                _lastRequestUtc = DateTime.UtcNow;
            }
            finally
            {
                _throttle.Release();
            }
        }

        /// <summary>
        /// Masks credentials carried in a query string so they do not reach the log.
        /// The OMDb endpoint takes its API key as a query parameter, and Jellyfin logs
        /// are routinely shared when asking for help.
        /// </summary>
        internal static string Redact(string url)
        {
            return SecretParameterRegex.Replace(url, "$1=***");
        }

        /// <summary>
        /// Detects the interstitial pages bot filters serve in place of real content.
        /// These come back with a 2xx status, so the status code alone is not enough.
        /// </summary>
        private static bool LooksLikeBotChallenge(string body)
        {
            if (string.IsNullOrEmpty(body) || body.Length > 20000)
            {
                // Real content pages are far larger than a challenge stub.
                return false;
            }

            return body.Contains("awswaf", StringComparison.OrdinalIgnoreCase)
                || body.Contains("gokuProps", StringComparison.Ordinal)
                || body.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase)
                || body.Contains("Enable JavaScript and cookies to continue", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                _httpClient.Dispose();
                _throttle.Dispose();
            }

            _disposed = true;
        }
    }
}
