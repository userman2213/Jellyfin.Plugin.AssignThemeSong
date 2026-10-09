using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Reads what a show's theme is called out of its Wikipedia article.
/// </summary>
/// <remarks>
/// <para>
/// Wikidata records the theme music of only the most famous shows. The Wikipedia article's
/// infobox records it for far more: The Sopranos, Firefly, House, Mad Men, True Detective and
/// Scrubs all have it there and none of them has it on Wikidata. The article is reached through
/// the sitelink the Wikidata query already returns, so nothing is searched for by name.
/// </para>
/// <para>
/// Asked only about series -- a film's infobox has no theme field -- and only about those Wikidata
/// left without a theme. Five articles per request and one request a second. Wikimedia does rate
/// limit, and did during testing: a <c>429</c> is waited out once, as the response asks, and a
/// second one ends the pass. Whatever was not asked is tried on the next run, and is not recorded
/// as a miss.
/// </para>
/// <para>
/// Only the song's title and the names are kept. Those are facts about the work, not Wikipedia's
/// text.
/// </para>
/// </remarks>
public sealed class WikipediaThemeSource : ICreditsSource
{
    /// <summary>The English Wikipedia API.</summary>
    public const string Endpoint = "https://en.wikipedia.org/w/api.php";

    /// <summary>How many articles go into one request.</summary>
    /// <remarks>
    /// Small, because each article's source is large -- The Sopranos is 190 KB -- and a request of
    /// twenty-five was refused where requests of five were not.
    /// </remarks>
    internal const int BatchSize = 5;

    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait when asked to and not told for how long.</summary>
    private static readonly TimeSpan DefaultBackOff = TimeSpan.FromSeconds(5);

    /// <summary>The longest wait this will honour before giving up for the day instead.</summary>
    private static readonly TimeSpan LongestBackOff = TimeSpan.FromSeconds(60);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<WikipediaThemeSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="WikipediaThemeSource"/> class.</summary>
    /// <param name="httpClientFactory">Makes the requests.</param>
    /// <param name="logger">Logger.</param>
    public WikipediaThemeSource(IHttpClientFactory httpClientFactory, IThemeForgeLogger<WikipediaThemeSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>What happened to one request.</summary>
    private enum Outcome
    {
        Answered,
        Failed,
        RateLimited,
    }

    /// <inheritdoc />
    public string Name => "Wikipedia";

    /// <inheritdoc />
    public int Order => 20;

    /// <inheritdoc />
    public CreditsQuestion Answers => CreditsQuestion.Theme;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ResearchComposers && configuration.UseWikipediaForThemes;
    }

    /// <inheritdoc />
    public async Task<CreditsAnswer> LookUpAsync(
        IReadOnlyList<CreditsRequest> batch,
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var found = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A film, or a show with no English article, cannot be answered here. That is a definite
        // "nothing", not a failure.
        var chunks = batch
            .Where(work => work.IsSeries && !string.IsNullOrWhiteSpace(work.WikipediaTitle))
            .Chunk(BatchSize)
            .ToList();

        if (chunks.Count == 0)
        {
            return new CreditsAnswer(found, failed);
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var chunk = chunks[index];
            var (outcome, articles) = await FetchAsync(client, chunk.Select(work => work.WikipediaTitle!).ToList(), cancellationToken).ConfigureAwait(false);

            if (outcome == Outcome.RateLimited)
            {
                var remaining = chunks.Skip(index).SelectMany(rest => rest).ToList();
                failed.UnionWith(remaining.Select(work => work.Key));
                _logger.LogInformation(
                    "ThemeForge: Wikipedia asked ThemeForge to slow down; {Count} shows will be looked up on the next run.",
                    remaining.Count);
                break;
            }

            if (outcome == Outcome.Failed)
            {
                failed.UnionWith(chunk.Select(work => work.Key));
            }
            else
            {
                foreach (var work in chunk)
                {
                    if (!articles.TryGetValue(work.WikipediaTitle!, out var source))
                    {
                        continue;
                    }

                    var theme = WikipediaInfobox.Theme(source);
                    var composers = WikipediaInfobox.ThemeComposers(source);
                    if (theme is not null || composers.Count > 0)
                    {
                        found[work.Key] = ResearchedCredits.None with { Theme = theme, ThemeComposers = composers };
                    }
                }
            }

            progress?.Report(100.0 * (index + 1) / chunks.Count);

            if (index < chunks.Count - 1)
            {
                await Task.Delay(Spacing, cancellationToken).ConfigureAwait(false);
            }
        }

        return new CreditsAnswer(found, failed);
    }

    /// <summary>Fetches the source of a few articles, waiting once if asked to.</summary>
    private async Task<(Outcome Outcome, IReadOnlyDictionary<string, string> Articles)> FetchAsync(
        HttpClient client,
        IReadOnlyList<string> titles,
        CancellationToken cancellationToken)
    {
        var none = new Dictionary<string, string>();
        var url = Endpoint
            + "?action=query&prop=revisions&rvprop=content&rvslots=main&format=json&formatversion=2&redirects=1&maxlag=5&titles="
            + Uri.EscapeDataString(string.Join('|', titles));

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                PoliteRequest.Identify(request);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var body = response.IsSuccessStatusCode
                    ? await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)
                    : null;

                // Both "too many requests" and "the database is lagging" (maxlag, which comes back
                // as a 200 carrying an error) mean the same thing: not now.
                var slowDown = response.StatusCode == HttpStatusCode.TooManyRequests || (body is not null && IsLagging(body));
                if (slowDown)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? DefaultBackOff;
                    if (attempt == 2 || wait > LongestBackOff)
                    {
                        return (Outcome.RateLimited, none);
                    }

                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (body is null)
                {
                    _logger.LogDebug("ThemeForge: Wikipedia answered {Status}.", (int)response.StatusCode);
                    return (Outcome.Failed, none);
                }

                return (Outcome.Answered, ParseArticles(body, titles));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "ThemeForge: could not reach Wikipedia.");
                return (Outcome.Failed, none);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "ThemeForge: Wikipedia returned something that could not be read.");
                return (Outcome.Failed, none);
            }
        }

        return (Outcome.RateLimited, none);
    }

    private static bool IsLagging(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.TryGetProperty("error", out var error)
            && error.TryGetProperty("code", out var code)
            && string.Equals(code.GetString(), "maxlag", StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads each article's source out of a response, keyed on the title it was asked for.
    /// </summary>
    /// <remarks>
    /// The API normalises titles and follows redirects, then answers under the title it ended up
    /// at. Those are traced back, so an answer is filed under the title the sitelink gave.
    /// A title with no article is simply absent.
    /// </remarks>
    /// <param name="json">The API's JSON.</param>
    /// <param name="asked">The titles that were asked for.</param>
    /// <returns>Each asked-for title that has an article, to that article's source.</returns>
    internal static IReadOnlyDictionary<string, string> ParseArticles(string json, IReadOnlyList<string> asked)
    {
        ArgumentNullException.ThrowIfNull(asked);

        var articles = new Dictionary<string, string>(StringComparer.Ordinal);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("query", out var query))
        {
            return articles;
        }

        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in new[] { "normalized", "redirects" })
        {
            if (query.TryGetProperty(step, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in list.EnumerateArray())
                {
                    if (entry.TryGetProperty("from", out var from) && entry.TryGetProperty("to", out var to)
                        && from.GetString() is { } fromTitle && to.GetString() is { } toTitle)
                    {
                        renamed[fromTitle] = toTitle;
                    }
                }
            }
        }

        var byTitle = new Dictionary<string, string>(StringComparer.Ordinal);
        if (query.TryGetProperty("pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                if (page.TryGetProperty("title", out var title) && title.GetString() is { } pageTitle
                    && page.TryGetProperty("revisions", out var revisions) && revisions.ValueKind == JsonValueKind.Array
                    && revisions.GetArrayLength() > 0
                    && revisions[0].TryGetProperty("slots", out var slots)
                    && slots.TryGetProperty("main", out var main)
                    && main.TryGetProperty("content", out var content)
                    && content.GetString() is { } source)
                {
                    byTitle[pageTitle] = source;
                }
            }
        }

        foreach (var title in asked)
        {
            // At most two hops: normalised, then redirected.
            var current = title;
            for (var hop = 0; hop < 3 && !byTitle.ContainsKey(current) && renamed.TryGetValue(current, out var next); hop++)
            {
                current = next;
            }

            if (byTitle.TryGetValue(current, out var source))
            {
                articles[title] = source;
            }
        }

        return articles;
    }
}
