#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.xThemeSong.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Pulls soundtrack track names for a title.
    ///
    /// OMDb (https://www.omdbapi.com) resolves a title to an IMDb ID when the item's
    /// Jellyfin metadata does not already carry one; the track names themselves come
    /// from the IMDb soundtrack listing for that ID, which OMDb does not expose.
    ///
    /// IMDb answers 403 Forbidden to requests without browser-like headers, so every
    /// request here goes through <see cref="BrowserHttpClient"/>.
    /// </summary>
    public class SoundtrackLookupService
    {
        private const string OmdbBaseUrl = "https://www.omdbapi.com/";
        private const string ImdbBaseUrl = "https://www.imdb.com";

        private static readonly Regex NextDataRegex = new Regex(
            "<script[^>]+id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex LegacySoundtrackRegex = new Regex(
            "<div[^>]*class=\"[^\"]*soundTrack[^\"]*\"[^>]*>(.*?)</div>\\s*(?=<div[^>]*class=\"[^\"]*soundTrack|<\\/div>)",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // In the legacy markup the track name sits in its own leading <div>, with the
        // credit lines following it as <br>-separated text.
        private static readonly Regex LegacyTitleRegex = new Regex(
            "^\\s*<div[^>]*>(.*?)</div>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TagRegex = new Regex("<[^>]+>", RegexOptions.Compiled);

        // Real listings credit one person for both roles, e.g. "Written and Performed by".
        private static readonly Regex WriterAndPerformerRegex = new Regex(
            @"(?:Written|Composed|Music)\s+and\s+Performed\s+by\s+(.+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PerformerRegex = new Regex(
            @"Performed\s+by\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex WriterRegex = new Regex(
            @"(?:Written|Composed|Music)\s+by\s+(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ILogger<SoundtrackLookupService> _logger;
        private readonly BrowserHttpClient _httpClient;
        private readonly BrowserPageFetcher _browser;

        public SoundtrackLookupService(
            ILogger<SoundtrackLookupService> logger,
            BrowserHttpClient httpClient,
            BrowserPageFetcher browser)
        {
            _logger = logger;
            _httpClient = httpClient;
            _browser = browser;
        }

        /// <summary>
        /// Resolves an IMDb ID for a title through the OMDb API.
        /// Returns null when no API key is configured or OMDb has no match.
        /// </summary>
        /// <param name="title">Title to search for.</param>
        /// <param name="year">Production year, used to disambiguate remakes.</param>
        /// <param name="isSeries">True for a TV show, false for a movie.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<string?> ResolveImdbIdAsync(
            string title,
            int? year,
            bool isSeries,
            CancellationToken cancellationToken)
        {
            var apiKey = Plugin.Instance?.Configuration?.OmdbApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                _logger.LogDebug(
                    "No OMDb API key configured, cannot resolve an IMDb ID for {Title}", title);
                return null;
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                return null;
            }

            var requestUrl = OmdbBaseUrl
                + "?apikey=" + Uri.EscapeDataString(apiKey!)
                + "&t=" + Uri.EscapeDataString(title)
                + "&type=" + (isSeries ? "series" : "movie");

            if (year.HasValue && year.Value > 0)
            {
                requestUrl += "&y=" + year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            var body = await _httpClient
                .GetStringOrNullAsync(requestUrl, expectJson: true, referer: null, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(body))
            {
                // OMDb answers 401 for a rejected key, which is the most common cause
                // of an empty body here; the request itself is a plain GET.
                _logger.LogWarning(
                    "OMDb returned no usable response for {Title}. If this repeats, check that the "
                    + "configured OMDb API key is valid and activated.",
                    title);
                return null;
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                // OMDb reports failure in the body with HTTP 200, not a status code.
                if (root.TryGetProperty("Response", out var response)
                    && string.Equals(response.GetString(), "False", StringComparison.OrdinalIgnoreCase))
                {
                    var error = root.TryGetProperty("Error", out var errorElement)
                        ? errorElement.GetString()
                        : "unknown error";
                    _logger.LogInformation("OMDb had no match for {Title}: {Error}", title, error);
                    return null;
                }

                if (root.TryGetProperty("imdbID", out var imdbId))
                {
                    var value = imdbId.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        _logger.LogInformation("OMDb resolved {Title} to IMDb ID {ImdbId}", title, value);
                        return value;
                    }
                }
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Could not parse OMDb response for {Title}", title);
            }

            return null;
        }

        /// <summary>
        /// Fetches the soundtrack listing for an IMDb ID.
        /// </summary>
        /// <param name="imdbId">IMDb ID, e.g. tt0137523.</param>
        /// <param name="maxTracks">Upper bound on returned tracks.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The tracks found, newest listing format first; empty when none.</returns>
        public async Task<List<SoundtrackTrack>> GetSoundtrackAsync(
            string imdbId,
            int maxTracks,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(imdbId))
            {
                return new List<SoundtrackTrack>();
            }

            var titleUrl = $"{ImdbBaseUrl}/title/{imdbId}/";
            var soundtrackUrl = $"{titleUrl}soundtrack/";

            var html = await FetchSoundtrackPageAsync(soundtrackUrl, titleUrl, cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(html))
            {
                _logger.LogInformation("No soundtrack listing available for {ImdbId}", imdbId);
                return new List<SoundtrackTrack>();
            }

            var tracks = ParseTracks(html!);

            if (tracks.Count == 0)
            {
                _logger.LogInformation("Soundtrack page for {ImdbId} listed no tracks", imdbId);
                return tracks;
            }

            if (maxTracks > 0 && tracks.Count > maxTracks)
            {
                tracks = tracks.Take(maxTracks).ToList();
            }

            _logger.LogInformation("Found {Count} soundtrack entries for {ImdbId}", tracks.Count, imdbId);
            return tracks;
        }

        /// <summary>
        /// Runs a soundtrack pull step by step and reports what each step did, so the
        /// settings page can show an administrator whether their server can reach IMDb
        /// and, when it cannot, which stage failed.
        /// </summary>
        /// <param name="imdbId">The IMDb ID to test with.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<SoundtrackDiagnostic> RunDiagnosticAsync(
            string imdbId,
            CancellationToken cancellationToken)
        {
            var diagnostic = new SoundtrackDiagnostic { ImdbId = imdbId };
            var total = Stopwatch.StartNew();

            var titleUrl = $"{ImdbBaseUrl}/title/{imdbId}/";
            var soundtrackUrl = $"{titleUrl}soundtrack/";

            // Step 1: the plain HTTP request, which is all a permissive network needs.
            var step = Stopwatch.StartNew();
            var direct = await _httpClient
                .GetPageAsync(soundtrackUrl, expectJson: false, referer: titleUrl, cancellationToken)
                .ConfigureAwait(false);
            step.Stop();

            string? html = direct.Body;
            diagnostic.Steps.Add(new DiagnosticStep
            {
                Name = "Direct HTTP request",
                Ok = !string.IsNullOrEmpty(direct.Body),
                ElapsedMs = step.ElapsedMilliseconds,
                Detail = !string.IsNullOrEmpty(direct.Body)
                    ? $"IMDb returned the page ({direct.Body!.Length:N0} bytes)"
                    : direct.Blocked
                        ? "IMDb refused the request or answered with a bot check"
                        : "IMDb returned nothing usable"
            });

            // Step 2: the browser, only needed when the direct request was refused.
            if (string.IsNullOrEmpty(html) && direct.Blocked)
            {
                step = Stopwatch.StartNew();
                var browserPath = await _browser.EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
                step.Stop();

                diagnostic.BrowserPath = browserPath;
                diagnostic.BrowserSource = _browser.DescribeBrowserSource();

                diagnostic.Steps.Add(new DiagnosticStep
                {
                    Name = "Browser available",
                    Ok = browserPath != null,
                    ElapsedMs = step.ElapsedMilliseconds,
                    Detail = browserPath != null
                        ? $"Using the browser {diagnostic.BrowserSource} at {browserPath}"
                        : "No browser could be found or downloaded"
                });

                if (browserPath != null)
                {
                    diagnostic.BrowserVersion = await _browser
                        .GetBrowserVersionAsync(cancellationToken).ConfigureAwait(false);

                    step = Stopwatch.StartNew();
                    html = await _browser
                        .GetRenderedHtmlAsync(
                            soundtrackUrl,
                            BrowserHttpClient.GetUserAgent(),
                            HasSoundtrackMarkup,
                            cancellationToken)
                        .ConfigureAwait(false);
                    step.Stop();

                    diagnostic.Steps.Add(new DiagnosticStep
                    {
                        Name = "Browser render",
                        Ok = !string.IsNullOrEmpty(html),
                        ElapsedMs = step.ElapsedMilliseconds,
                        Detail = !string.IsNullOrEmpty(html)
                            ? $"Rendered the page ({html!.Length:N0} bytes)"
                            : "The browser ran but IMDb's bot check did not clear"
                    });
                }
            }

            // Step 3: parsing.
            if (!string.IsNullOrEmpty(html))
            {
                step = Stopwatch.StartNew();
                diagnostic.Tracks = ParseTracks(html!);
                step.Stop();

                diagnostic.Steps.Add(new DiagnosticStep
                {
                    Name = "Parse soundtrack",
                    Ok = diagnostic.Tracks.Count > 0,
                    ElapsedMs = step.ElapsedMilliseconds,
                    Detail = diagnostic.Tracks.Count > 0
                        ? $"Read {diagnostic.Tracks.Count} track(s)"
                        : "The page loaded but listed no tracks"
                });
            }

            total.Stop();
            diagnostic.ElapsedMs = total.ElapsedMilliseconds;
            diagnostic.Success = diagnostic.Tracks.Count > 0;
            diagnostic.BrowserSource ??= _browser.DescribeBrowserSource();
            diagnostic.Summary = BuildSummary(diagnostic);

            return diagnostic;
        }

        /// <summary>
        /// Turns the steps into one line an administrator can act on.
        /// </summary>
        private static string BuildSummary(SoundtrackDiagnostic diagnostic)
        {
            if (diagnostic.Success)
            {
                var viaBrowser = diagnostic.Steps.Exists(s => s.Name == "Browser render" && s.Ok);
                return viaBrowser
                    ? $"Working. Pulled {diagnostic.Tracks.Count} tracks from IMDb using the headless browser."
                    : $"Working. Pulled {diagnostic.Tracks.Count} tracks from IMDb with a direct request, "
                      + "so no browser was needed.";
            }

            var browserStep = diagnostic.Steps.Find(s => s.Name == "Browser available");
            if (browserStep != null && !browserStep.Ok)
            {
                return "IMDb refused a direct request and no browser is available. Install one with "
                     + "the button above, or set a browser path in the settings.";
            }

            if (diagnostic.Steps.Exists(s => s.Name == "Browser render" && !s.Ok))
            {
                return "IMDb refused a direct request and its bot check did not clear in the browser "
                     + "either. This usually means IMDb is challenging this server's IP address; it is "
                     + "common on hosted or VPS servers and rare on a home connection.";
            }

            if (diagnostic.Tracks.Count == 0 && diagnostic.Steps.Exists(s => s.Name == "Parse soundtrack"))
            {
                return "IMDb served the page but it lists no soundtrack entries for this title.";
            }

            return "The lookup did not complete. See the steps above.";
        }

        /// <summary>
        /// Fetches the soundtrack page, trying a plain HTTP request first because it is
        /// an order of magnitude faster, and rendering the page in a headless browser
        /// when the request is turned away by the bot filter.
        /// </summary>
        private async Task<string?> FetchSoundtrackPageAsync(
            string soundtrackUrl,
            string titleUrl,
            CancellationToken cancellationToken)
        {
            _logger.LogDebug("Fetching soundtrack listing from {Url}", soundtrackUrl);

            var result = await _httpClient
                .GetPageAsync(soundtrackUrl, expectJson: false, referer: titleUrl, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(result.Body))
            {
                return result.Body;
            }

            if (!result.Blocked)
            {
                return null;
            }

            var useBrowser = Plugin.Instance?.Configuration?.UseBrowserForSoundtrack ?? true;
            if (!useBrowser)
            {
                _logger.LogWarning(
                    "IMDb refused a plain request for {Url} and the browser fallback is switched off. "
                    + "Enable it in the plugin settings to read soundtrack listings from this server.",
                    soundtrackUrl);
                return null;
            }

            if (!_browser.IsAvailable)
            {
                _logger.LogWarning(
                    "IMDb refused a plain request for {Url} and no browser is installed to fall back "
                    + "on. Install Chrome or Chromium, or set a browser path in the plugin settings.",
                    soundtrackUrl);
                return null;
            }

            _logger.LogInformation(
                "IMDb refused a plain request for {Url}; rendering it in a browser instead",
                soundtrackUrl);

            // The browser exits successfully even when all it rendered was the
            // challenge, so completeness is judged from the document itself.
            var rendered = await _browser
                .GetRenderedHtmlAsync(
                    soundtrackUrl,
                    BrowserHttpClient.GetUserAgent(),
                    HasSoundtrackMarkup,
                    cancellationToken)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(rendered))
            {
                _logger.LogWarning(
                    "The browser could not get past IMDb's bot protection for {Url}", soundtrackUrl);
                return null;
            }

            return rendered;
        }

        /// <summary>
        /// Tells a rendered soundtrack page apart from a bot challenge standing in for it.
        /// </summary>
        private static bool HasSoundtrackMarkup(string html)
        {
            return html.Contains("__NEXT_DATA__", StringComparison.Ordinal)
                || html.Contains("soundTrack", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Reads tracks from a soundtrack page in whichever markup it arrived in.
        /// </summary>
        private List<SoundtrackTrack> ParseTracks(string html)
        {
            var tracks = ParseEmbeddedJson(html);

            if (tracks.Count == 0)
            {
                tracks = ParseLegacyHtml(html);
            }

            return tracks;
        }

        /// <summary>
        /// Reads tracks out of the __NEXT_DATA__ blob the current IMDb pages embed.
        /// The blob is walked rather than indexed by a fixed path, so a reshuffle of
        /// the surrounding structure does not break the parse.
        /// </summary>
        private List<SoundtrackTrack> ParseEmbeddedJson(string html)
        {
            var tracks = new List<SoundtrackTrack>();

            var match = NextDataRegex.Match(html);
            if (!match.Success)
            {
                return tracks;
            }

            try
            {
                using var document = JsonDocument.Parse(match.Groups[1].Value);
                CollectTracks(document.RootElement, tracks);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Could not parse the embedded JSON on the soundtrack page");
            }

            return tracks;
        }

        /// <summary>
        /// Walks a JSON tree collecting every soundtrack row. A row is an object with
        /// a "rowTitle" (the track name) alongside a "listContent" array holding the
        /// credit lines.
        /// </summary>
        private static void CollectTracks(JsonElement element, List<SoundtrackTrack> tracks)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    if (element.TryGetProperty("rowTitle", out var rowTitle)
                        && rowTitle.ValueKind == JsonValueKind.String
                        && element.TryGetProperty("listContent", out var listContent)
                        && listContent.ValueKind == JsonValueKind.Array)
                    {
                        var title = CleanText(rowTitle.GetString());
                        if (!string.IsNullOrWhiteSpace(title))
                        {
                            var track = new SoundtrackTrack { Title = title };

                            foreach (var line in listContent.EnumerateArray())
                            {
                                if (line.ValueKind != JsonValueKind.Object
                                    || !line.TryGetProperty("html", out var lineHtml)
                                    || lineHtml.ValueKind != JsonValueKind.String)
                                {
                                    continue;
                                }

                                ApplyCreditLine(track, CleanText(lineHtml.GetString()));
                            }

                            tracks.Add(track);
                        }
                    }

                    foreach (var property in element.EnumerateObject())
                    {
                        CollectTracks(property.Value, tracks);
                    }

                    break;

                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray())
                    {
                        CollectTracks(item, tracks);
                    }

                    break;
            }
        }

        /// <summary>
        /// Reads tracks from the older server-rendered soundtrack markup, still served
        /// to some clients and regions.
        /// </summary>
        private static List<SoundtrackTrack> ParseLegacyHtml(string html)
        {
            var tracks = new List<SoundtrackTrack>();

            foreach (Match block in LegacySoundtrackRegex.Matches(html))
            {
                var content = block.Groups[1].Value;
                string title;

                var titleMatch = LegacyTitleRegex.Match(content);
                if (titleMatch.Success)
                {
                    title = CleanText(titleMatch.Groups[1].Value);
                    content = content.Substring(titleMatch.Length);
                }
                else
                {
                    title = string.Empty;
                }

                // The remaining credit lines are separated by <br>.
                var lines = Regex.Split(content, "<br\\s*/?>", RegexOptions.IgnoreCase)
                    .Select(CleanText)
                    .Where(line => !string.IsNullOrWhiteSpace(line))
                    .ToList();

                if (string.IsNullOrWhiteSpace(title))
                {
                    // No title div: fall back to the first line being the track name.
                    if (lines.Count == 0)
                    {
                        continue;
                    }

                    title = lines[0];
                    lines = lines.Skip(1).ToList();
                }

                var track = new SoundtrackTrack { Title = title };
                foreach (var line in lines)
                {
                    ApplyCreditLine(track, line);
                }

                tracks.Add(track);
            }

            return tracks;
        }

        /// <summary>
        /// Files a credit line such as "Performed by Pixies" onto the track.
        /// </summary>
        private static void ApplyCreditLine(SoundtrackTrack track, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            var both = WriterAndPerformerRegex.Match(line);
            if (both.Success)
            {
                var name = CleanText(both.Groups[1].Value);
                track.Performer ??= name;
                track.Writer ??= name;
                return;
            }

            var performer = PerformerRegex.Match(line);
            if (performer.Success)
            {
                track.Performer ??= CleanText(performer.Groups[1].Value);
                return;
            }

            var writer = WriterRegex.Match(line);
            if (writer.Success)
            {
                track.Writer ??= CleanText(writer.Groups[1].Value);
            }
        }

        /// <summary>
        /// Strips markup and entities and collapses whitespace.
        /// </summary>
        private static string CleanText(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var text = TagRegex.Replace(value, " ");
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"\s+", " ").Trim();

            // Listings frequently wrap the track name in quotes.
            return text.Trim('"', '“', '”').Trim();
        }
    }
}
