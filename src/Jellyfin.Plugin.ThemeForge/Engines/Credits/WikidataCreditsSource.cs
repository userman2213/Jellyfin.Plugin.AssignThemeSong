using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Asks Wikidata who wrote the music, a hundred works at a time.
/// </summary>
/// <remarks>
/// <para>
/// Wikidata records a work's composer as a statement on the work itself, keyed on the same IMDb
/// and TMDB ids Jellyfin already holds, and its query service will answer about a whole batch in
/// one request. A library of a thousand titles costs a handful of requests rather than a thousand.
/// The data is public domain, so nothing has to be attributed or cached under conditions.
/// </para>
/// <para>
/// TheTVDB ids are deliberately not used. They are not unique in Wikidata -- one show's id
/// returned two entities, only one of which carried a composer -- so a match on one would
/// sometimes be a match on the wrong work, which is the one failure this source exists to avoid.
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

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Ids are pasted into a query, so anything that is not plainly an id is dropped.</summary>
    private static readonly Regex SafeId = new(@"^[A-Za-z0-9]+$", RegexOptions.Compiled);

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
    public bool IsEnabled(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ResearchComposers;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<string, ResearchedCredits>> LookUpAsync(
        IReadOnlyList<CreditsRequest> batch,
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        var found = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);
        var chunks = batch.Chunk(BatchSize).ToList();

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        for (var index = 0; index < chunks.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var answers = await AskAsync(client, chunks[index], cancellationToken).ConfigureAwait(false);
            foreach (var pair in answers)
            {
                found[pair.Key] = pair.Value;
            }

            progress?.Report(100.0 * (index + 1) / chunks.Count);
        }

        return found;
    }

    /// <summary>Runs one query and reads the answers out of it.</summary>
    private async Task<IReadOnlyDictionary<string, ResearchedCredits>> AskAsync(
        HttpClient client,
        IReadOnlyList<CreditsRequest> chunk,
        CancellationToken cancellationToken)
    {
        var query = BuildQuery(chunk);
        if (query is null)
        {
            return new Dictionary<string, ResearchedCredits>();
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                // Posted rather than put in the URL: a hundred ids makes a query longer than
                // some proxies will carry as a query string.
                Content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("query", query) }),
            };

            request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");
            PoliteRequest.Identify(request);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "ThemeForge: Wikidata answered {Status} for a batch of {Count} titles; their composers stay unknown for now.",
                    (int)response.StatusCode,
                    chunk.Count);
                return new Dictionary<string, ResearchedCredits>();
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Attribute(chunk, Parse(body));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "ThemeForge: could not reach Wikidata; composers stay unknown for now.");
            return new Dictionary<string, ResearchedCredits>();
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "ThemeForge: Wikidata returned something that could not be read.");
            return new Dictionary<string, ResearchedCredits>();
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
        // source skip a step. The label service resolves names in the same request.
        return string.Create(
            CultureInfo.InvariantCulture,
            $@"SELECT ?imdb ?tmdbFilm ?tmdbTv ?composerLabel ?performerLabel ?mbid WHERE {{
  {string.Join("\n  UNION\n  ", branches)}
  OPTIONAL {{ ?item wdt:P86 ?composer. }}
  OPTIONAL {{
    ?item wdt:P406 ?album.
    OPTIONAL {{ ?album wdt:P175 ?performer. }}
    OPTIONAL {{ ?album wdt:P436 ?mbid. }}
  }}
  SERVICE wikibase:label {{ bd:serviceParam wikibase:language ""en"". }}
}}");
    }

    /// <summary>
    /// Turns provider-keyed answers into answers keyed the way the batch asked.
    /// </summary>
    /// <remarks>
    /// A work is asked about under every id it has and may be answered under any of them: a film
    /// whose Wikidata entity records a different IMDb id than Jellyfin holds still matches on its
    /// TMDB id, and the answer would otherwise be filed under a key nobody looks for. An answer
    /// naming somebody is preferred over one that carries only a release group, since the second
    /// is a lead for the next source rather than a result.
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
            ResearchedCredits? best = null;

            foreach (var key in work.Keys)
            {
                if (!parsed.TryGetValue(key, out var credits))
                {
                    continue;
                }

                if (credits.Any)
                {
                    best = credits;
                    break;
                }

                best ??= credits;
            }

            if (best is not null)
            {
                answers[work.Key] = best;
            }
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
    /// A row is not one work. The same work comes back once per id that matched it and again for
    /// every combination of composer and soundtrack performer it has, so Blade Runner 2049 arrives
    /// as four rows for two composers. Everything is therefore gathered per key and deduplicated.
    /// </remarks>
    /// <param name="json">The query service's JSON.</param>
    /// <returns>What was found, keyed the way the cache stores it.</returns>
    internal static IReadOnlyDictionary<string, ResearchedCredits> Parse(string json)
    {
        var composers = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var artists = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var releaseGroups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

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

            if (keys.Count == 0)
            {
                continue;
            }

            var composer = Value(row, "composerLabel");
            var performer = Value(row, "performerLabel");
            var mbid = Value(row, "mbid");

            foreach (var key in keys)
            {
                if (composer is not null)
                {
                    var names = composers.TryGetValue(key, out var existing) ? existing : composers[key] = new List<string>();
                    if (names.Count < MaxNames && !names.Contains(composer, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(composer);
                    }
                }

                if (performer is not null && !artists.ContainsKey(key))
                {
                    artists[key] = performer;
                }

                if (mbid is not null && !releaseGroups.ContainsKey(key))
                {
                    releaseGroups[key] = mbid;
                }
            }
        }

        var found = new Dictionary<string, ResearchedCredits>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in composers.Keys.Concat(artists.Keys).Concat(releaseGroups.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var names = composers.TryGetValue(key, out var list) ? list : new List<string>();
            artists.TryGetValue(key, out var artist);
            releaseGroups.TryGetValue(key, out var releaseGroup);

            // The performer is only worth keeping when nobody is credited with writing it:
            // on a soundtrack they are usually the same person, and where they differ the
            // composer is the one that identifies the work.
            var credits = new ResearchedCredits(names, names.Count > 0 ? null : artist, releaseGroup);
            if (credits.Any || releaseGroup is not null)
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
}
