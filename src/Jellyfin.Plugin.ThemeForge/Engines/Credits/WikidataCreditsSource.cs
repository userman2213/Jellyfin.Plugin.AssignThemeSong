using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Asks Wikidata who wrote the music and what the theme is called, fifty works at a time.
/// </summary>
/// <remarks>
/// <para>
/// Wikidata records a work's composer and its theme music as statements on the work itself, keyed
/// on the same IMDb and TMDB ids Jellyfin already holds, and its query service will answer about
/// a whole batch in one request. A library of a thousand titles costs a handful of requests
/// rather than a thousand. The data is public domain, so nothing has to be attributed or cached
/// under conditions. The same request also returns the work's English Wikipedia article, which is
/// where the next source finds a theme Wikidata does not record.
/// </para>
/// <para>
/// TheTVDB ids are deliberately not used. They are not unique in Wikidata -- one show's id
/// returned two entities, only one of which carried a composer -- so a match on one would
/// sometimes be a match on the wrong work, which is the one failure this source exists to avoid.
/// For the same reason seasons and episodes are excluded: <c>tt0106179</c> is recorded on both
/// The X-Files and on its tenth season, and the season's article is not the show's.
/// </para>
/// </remarks>
public sealed class WikidataCreditsSource : ICreditsSource
{
    /// <summary>The query service endpoint.</summary>
    public const string Endpoint = "https://query.wikidata.org/sparql";

    /// <summary>How many works go into one query.</summary>
    /// <remarks>
    /// Large enough that a library is a few requests, small enough to stay well inside the
    /// service's sixty-second limit on any one query.
    /// </remarks>
    internal const int BatchSize = 50;

    /// <summary>How many names are kept for one work, matching what Jellyfin's own lookup keeps.</summary>
    private const int MaxNames = 3;

    /// <summary>Where an English Wikipedia article's URL starts.</summary>
    private const string WikipediaPrefix = "https://en.wikipedia.org/wiki/";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ids are pasted into a query, so anything that is not plainly an id is dropped.</summary>
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);

    /// <summary>What the label service returns for an entity with no English name: its bare id.</summary>
    private static readonly Regex BareId = new(@"^Q\d+$", RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<WikidataCreditsSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="WikidataCreditsSource"/> class.</summary>
    /// <param name="httpClientFactory">Makes the request.</param>
    /// <param name="logger">Logger.</param>
    public WikidataCreditsSource(IHttpClientFactory httpClientFactory, IThemeForgeLogger<WikidataCreditsSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Wikidata";

    /// <inheritdoc />
    public int Order => 0;

    /// <inheritdoc />
    public CreditsQuestion Answers => CreditsQuestion.Composers | CreditsQuestion.Theme;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ResearchComposers;
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
        var chunks = batch.Chunk(BatchSize).ToList();

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var answers = await AskAsync(client, chunks[index], cancellationToken).ConfigureAwait(false);
            if (answers is null)
            {
                failed.UnionWith(chunks[index].Select(work => work.Key));
            }
            else
            {
                foreach (var pair in answers)
                {
                    found[pair.Key] = pair.Value;
                }
            }

            progress?.Report(100.0 * (index + 1) / chunks.Count);
        }

        return new CreditsAnswer(found, failed);
    }

    /// <summary>Runs one query and reads the answers out of it.</summary>
    /// <returns>The answers, or null when the question could not be asked.</returns>
    private async Task<IReadOnlyDictionary<string, ResearchedCredits>?> AskAsync(
        HttpClient client,
        IReadOnlyList<CreditsRequest> chunk,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(chunk);
        if (query is null)
        {
            // Nothing in the chunk has an id this source can use. That is a definite answer --
            // nothing -- rather than a failure, so it must not be retried every night.
            return new Dictionary<string, ResearchedCredits>();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                // Posted rather than put in the URL: fifty ids makes a query longer than some
                // proxies will carry as a query string.
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) }),
            };

            request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");
            PoliteRequest.Identify(request);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ThemeForge: Wikidata answered {Status} for a batch of {Count} titles; they will be asked about again next time.",
                    (int)response.StatusCode,
                    chunk.Count);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Attribute(chunk, Parse(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ThemeForge: could not reach Wikidata; those titles will be asked about again next time.");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ThemeForge: Wikidata returned something that could not be read; those titles will be asked about again next time.");
            return null;
        }
    }

    /// <summary>
    /// Builds one query asking about every work in the chunk at once.
    /// </summary>
    /// <param name="chunk">The works.</param>
    /// <returns>The query, or null when none of them carry an id this source can use.</returns>
    internal static string? BuildQuery(IReadOnlyList<CreditsRequest> chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        var imdb = Values(chunk.Select(work => work.ImdbId));
        var films = Values(chunk.Where(work => !work.IsSeries).Select(work => work.TmdbId));
        var shows = Values(chunk.Where(work => work.IsSeries).Select(work => work.TmdbId));

        var branches = new List<string>(3);
        if (imdb.Length > 0)
        {
            branches.Add($"{{ ?item wdt:P345 ?imdb. VALUES ?imdb {{ {imdb} }} }}");
        }

        if (films.Length > 0)
        {
            branches.Add($"{{ ?item wdt:P4947 ?tmdbFilm. VALUES ?tmdbFilm {{ {films} }} }}");
        }

        if (shows.Length > 0)
        {
            branches.Add($"{{ ?item wdt:P4983 ?tmdbTv. VALUES ?tmdbTv {{ {shows} }} }}");
        }

        if (branches.Count == 0)
        {
            return null;
        }

        // P86 is the composer of the work. P406 is its soundtrack release, which supplies a
        // performing artist when no composer is stated and the MusicBrainz id that lets the next
        // source skip a step. P942 is its theme music, and P175 on that is who performs it. The
        // sitelink is the English Wikipedia article, where a theme Wikidata lacks is often
        // recorded. Seasons (Q3464665) and episodes (Q21191270) share their series' ids and are
        // not the series. The label service resolves every name in the same request.
        return string.Create(
            CultureInfo.InvariantCulture,
            $@"SELECT ?imdb ?tmdbFilm ?tmdbTv ?composerLabel ?performerLabel ?mbid ?themeLabel ?themePerformerLabel ?article WHERE {{
  {string.Join("\n  UNION\n  ", branches)}
  MINUS {{ ?item wdt:P31 wd:Q3464665. }}
  MINUS {{ ?item wdt:P31 wd:Q21191270. }}
  OPTIONAL {{ ?item wdt:P86 ?composer. }}
  OPTIONAL {{
    ?item wdt:P406 ?album.
    OPTIONAL {{ ?album wdt:P175 ?performer. }}
    OPTIONAL {{ ?album wdt:P436 ?mbid. }}
  }}
  OPTIONAL {{
    ?item wdt:P942 ?theme.
    OPTIONAL {{ ?theme wdt:P175 ?themePerformer. }}
  }}
  OPTIONAL {{ ?article schema:about ?item; schema:isPartOf <https://en.wikipedia.org/>. }}
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
}}");
    }

    /// <summary>
    /// Turns provider-keyed answers into answers keyed the way the batch asked.
    /// </summary>
    /// <remarks>
    /// A work is asked about under every id it has and may be answered under any of them: a film
    /// whose Wikidata entity records a different IMDb id than Jellyfin holds still matches on its
    /// TMDB id, and the answer would otherwise be filed under a key nobody looks for. Each part of
    /// the answer is taken from the first id that has it.
    /// </remarks>
    /// <param name="chunk">The works that were asked about.</param>
    /// <param name="parsed">What came back, keyed on provider ids.</param>
    /// <returns>The answers, keyed on <see cref="CreditsRequest.Key"/>.</returns>
    private static IReadOnlyDictionary<string, ResearchedCredits> Attribute(
        IReadOnlyList<CreditsRequest> chunk,
        IReadOnlyDictionary<string, ResearchedCredits> parsed)
    {
        var answers = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);

        foreach (var work in chunk)
        {
            var matches = work.Keys.Where(parsed.ContainsKey).Select(key => parsed[key]).ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            var named = matches.FirstOrDefault(credits => credits.Any) ?? matches[0];
            answers[work.Key] = named with
            {
                ReleaseGroupId = matches.Select(credits => credits.ReleaseGroupId).FirstOrDefault(id => id is not null),
                Theme = matches.Select(credits => credits.Theme).FirstOrDefault(theme => theme is not null),
                WikipediaTitle = matches.Select(credits => credits.WikipediaTitle).FirstOrDefault(title => title is not null),
            };
        }

        return answers;
    }

    private static string Values(IEnumerable<string?> ids) =>
        string.Join(
            ' ',
            ids.Where(id => !string.IsNullOrWhiteSpace(id) && SafeId.IsMatch(id!.Trim()))
                .Select(id => id!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => "\"" + id + "\""));

    /// <summary>
    /// Reads the answers out of a query result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A row is not one work. The same work comes back once per id that matched it and again for
    /// every combination of composer, soundtrack performer and theme performer it has, so Blade
    /// Runner 2049 arrives as four rows for two composers. Everything is therefore gathered per
    /// key and deduplicated.
    /// </para>
    /// <para>
    /// Two things are refused. A name that is only an entity id -- <c>Q115522708</c> -- is what the
    /// label service returns for something with no English name, and searching for it finds
    /// nothing. And a theme performer is kept only when exactly one is named, because P175 on a
    /// song lists every recording of it: Titanic's theme names Céline Dion and three cover acts.
    /// </para>
    /// </remarks>
    /// <param name="json">The query service's JSON.</param>
    /// <returns>What was found, keyed the way the cache stores it.</returns>
    internal static IReadOnlyDictionary<string, ResearchedCredits> Parse(string json)
    {
        var works = new Dictionary<string, Gathered>(StringComparer.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("bindings", out var bindings)
            || bindings.ValueKind != JsonValueKind.Array)
        {
            return new Dictionary<string, ResearchedCredits>();
        }

        foreach (var row in bindings.EnumerateArray())
        {
            var keys = new List<string>(3);
            if (Value(row, "imdb") is { } imdb)
            {
                keys.Add("imdb:" + imdb);
            }

            if (Value(row, "tmdbFilm") is { } film)
            {
                keys.Add("tmdb:" + film);
            }

            if (Value(row, "tmdbTv") is { } show)
            {
                keys.Add("tmdbtv:" + show);
            }

            var composer = Label(row, "composerLabel");
            var performer = Label(row, "performerLabel");
            var mbid = Value(row, "mbid");
            var theme = Label(row, "themeLabel");
            var themePerformer = Label(row, "themePerformerLabel");
            var article = Article(Value(row, "article"));

            foreach (var key in keys)
            {
                var work = works.TryGetValue(key, out var existing) ? existing : works[key] = new Gathered();

                if (composer is not null && work.Composers.Count < MaxNames
                    && !work.Composers.Contains(composer, StringComparer.OrdinalIgnoreCase))
                {
                    work.Composers.Add(composer);
                }

                work.Performer ??= performer;
                work.ReleaseGroup ??= mbid;
                work.Theme ??= theme;
                work.Article ??= article;

                if (themePerformer is not null)
                {
                    work.ThemePerformers.Add(themePerformer);
                }
            }
        }

        var found = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, work) in works)
        {
            // The performer is only worth keeping when nobody is credited with writing it: on a
            // soundtrack they are usually the same person, and where they differ the composer is
            // the one that identifies the work.
            var credits = new ResearchedCredits(work.Composers, work.Composers.Count > 0 ? null : work.Performer, work.ReleaseGroup)
            {
                Theme = work.Theme is null
                    ? null
                    : new ThemeSong(work.Theme, work.ThemePerformers.Count == 1 ? work.ThemePerformers.Single() : null),
                WikipediaTitle = work.Article,
            };

            if (credits.Any || credits.Theme is not null || credits.ReleaseGroupId is not null || credits.WikipediaTitle is not null)
            {
                found[key] = credits;
            }
        }

        return found;
    }

    private static string? Value(JsonElement row, string name) =>
        row.TryGetProperty(name, out var cell)
        && cell.TryGetProperty("value", out var value)
        && value.GetString() is { Length: > 0 } text
            ? text
            : null;

    /// <summary>Reads a label, refusing the bare id the service returns for something unnamed.</summary>
    private static string? Label(JsonElement row, string name) =>
        Value(row, name) is { } label && !BareId.IsMatch(label) ? label.Trim() : null;

    /// <summary>Turns a sitelink into the article title the Wikipedia API expects.</summary>
    private static string? Article(string? url)
    {
        if (url is null || !url.StartsWith(WikipediaPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var title = Uri.UnescapeDataString(url[WikipediaPrefix.Length..]).Replace('_', ' ').Trim();
        return title.Length == 0 ? null : title;
    }

    /// <summary>Everything the rows said about one key.</summary>
    private sealed class Gathered
    {
        public List<string> Composers { get; } = new();

        public string? Performer { get; set; }

        public string? ReleaseGroup { get; set; }

        public string? Theme { get; set; }

        public HashSet<string> ThemePerformers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Article { get; set; }
    }
}
