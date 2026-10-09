using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

namespace Jellyfin.Plugin.ThemeForge.Engines.Catalogue;

/// <summary>The local copy of ThemerrDB's index.</summary>
public interface IThemerrDbCatalogue
{
    /// <summary>Gets the snapshot in memory, loading it from disk on first use.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The snapshot, which may hold nothing if it has never been synced.</returns>
    Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Reads the catalogue index from ThemerrDB and replaces the local copy.
    /// </summary>
    /// <param name="progress">Progress reporter, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new snapshot, or the previous one if the sync could not complete.</returns>
    Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>
/// Keeps a local copy of which works ThemerrDB has a theme for.
/// </summary>
/// <remarks>
/// <para>
/// ThemerrDB publishes a static site once a day, at 12:00 UTC. It exposes a paged index per
/// category — <c>{type}/pages.json</c> says how many pages there are, and
/// <c>{type}/all_page_{n}.json</c> lists ten entries each — and a record per work at
/// <c>{type}/{database}/{id}.json</c> carrying the chosen theme.
/// </para>
/// <para>
/// Only the index is mirrored. That is what turns "ask the internet about every title in the
/// library" into "ask only about the ones that are actually in there", which is the difference
/// between hundreds of wasted requests per scan and none. Collections are the exception: their
/// records are read during the sync so that a film with no theme of its own can inherit its
/// collection's without a further request.
/// </para>
/// </remarks>
public sealed class ThemerrDbCatalogue : IThemerrDbCatalogue
{
    /// <summary>The published database. The same tree is served from the project's gh-pages branch.</summary>
    public const string BaseUrl = "https://app.lizardbyte.dev/ThemerrDB";

    /// <summary>Film records, keyed on a TMDB id.</summary>
    public const string MoviesByTmdb = BaseUrl + "/movies/themoviedb/{0}.json";

    /// <summary>Film records, keyed on an IMDb id.</summary>
    public const string MoviesByImdb = BaseUrl + "/movies/imdb/{0}.json";

    /// <summary>Show records, keyed on a TMDB id. ThemerrDB has no TheTVDB path.</summary>
    public const string ShowsByTmdb = BaseUrl + "/tv_shows/themoviedb/{0}.json";

    /// <summary>How long to wait between requests, so a daily sync stays a polite guest.</summary>
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromMilliseconds(40);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<ThemerrDbCatalogue> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ThemerrDbSnapshot? _snapshot;

    /// <summary>Initializes a new instance of the <see cref="ThemerrDbCatalogue"/> class.</summary>
    /// <param name="httpClientFactory">Supplies the HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ThemerrDbCatalogue(IHttpClientFactory httpClientFactory, IThemeForgeLogger<ThemerrDbCatalogue> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>Gets where the snapshot is kept, beside the index so it survives plugin upgrades.</summary>
    public static string SnapshotPath =>
        Path.Combine(Plugin.Instance?.DataPath ?? Path.GetTempPath(), "themerrdb.json");

    /// <inheritdoc />
    public async Task<ThemerrDbSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        if (_snapshot is not null)
        {
            return _snapshot;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _snapshot ??= Read() ?? new ThemerrDbSnapshot();
            return _snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<ThemerrDbSnapshot> SyncAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var client = _httpClientFactory.CreateClient(NamedClient.Default);
            client.Timeout = RequestTimeout;

            // Built into a new snapshot and only swapped in at the end, so a sync that fails
            // partway leaves yesterday's copy intact rather than a half-written one.
            var fresh = new ThemerrDbSnapshot { UpdatedUtc = DateTime.UtcNow };

            var movies = await ReadIndexAsync(client, "movies", progress, 0, 60, cancellationToken).ConfigureAwait(false);
            fresh.MovieTmdbIds = movies.Select(entry => entry.Id).ToList();
            fresh.MovieImdbIds = movies
                .Select(entry => entry.ImdbId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!)
                .ToList();

            var shows = await ReadIndexAsync(client, "tv_shows", progress, 60, 20, cancellationToken).ConfigureAwait(false);
            fresh.TvShows = shows.Select(entry => new CatalogueTitle(entry.Id, entry.Title)).ToList();

            var collections = await ReadIndexAsync(client, "movie_collections", progress, 80, 5, cancellationToken).ConfigureAwait(false);
            fresh.Collections = await ReadCollectionsAsync(client, collections, progress, 85, 15, cancellationToken).ConfigureAwait(false);

            if (!fresh.IsUsable)
            {
                _logger.LogWarning("ThemeForge: the ThemerrDB sync returned nothing; the previous copy has been kept.");
                return _snapshot ??= Read() ?? new ThemerrDbSnapshot();
            }

            Write(fresh);
            _snapshot = fresh;

            _logger.LogInformation(
                "ThemeForge: ThemerrDB now lists {Movies} films, {Shows} shows and {Collections} collections.",
                fresh.MovieTmdbIds.Count,
                fresh.TvShows.Count,
                fresh.Collections.Count);

            progress?.Report(100);
            return fresh;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The catalogue is an optimisation, not a dependency. Failing to refresh it means
            // falling back to asking about each item directly, which is what happened before.
            _logger.LogWarning(ex, "ThemeForge: could not refresh the ThemerrDB catalogue.");
            return _snapshot ??= Read() ?? new ThemerrDbSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>One entry as the paged index lists it.</summary>
    /// <param name="Id">The TMDB id.</param>
    /// <param name="Title">The title.</param>
    /// <param name="ImdbId">The IMDb id, present only for films.</param>
    internal sealed record IndexEntry(string Id, string Title, string? ImdbId);

    /// <summary>
    /// Reads one page of the index.
    /// </summary>
    /// <remarks>
    /// A missing entry is served as an HTML 404 page rather than an error, so the status code is
    /// the only reliable signal and the body is never parsed unless the request succeeded.
    /// </remarks>
    /// <param name="json">The page body.</param>
    /// <returns>The entries it lists.</returns>
    internal static IReadOnlyList<IndexEntry> ParsePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IndexEntry>();
        }

        var entries = new List<IndexEntry>();

        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object
                || !element.TryGetProperty("id", out var id))
            {
                continue;
            }

            var identifier = id.ValueKind switch
            {
                JsonValueKind.Number => id.GetRawText(),
                JsonValueKind.String => id.GetString(),
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(identifier))
            {
                continue;
            }

            var title = element.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? string.Empty
                : string.Empty;

            var imdb = element.TryGetProperty("imdb_id", out var i) && i.ValueKind == JsonValueKind.String
                ? i.GetString()
                : null;

            entries.Add(new IndexEntry(identifier, title, string.IsNullOrWhiteSpace(imdb) ? null : imdb));
        }

        return entries;
    }

    /// <summary>Reads the page count out of a <c>pages.json</c> document.</summary>
    /// <param name="json">The document body.</param>
    /// <returns>How many pages there are, or zero when the document does not say.</returns>
    internal static int ParsePageCount(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object
               && document.RootElement.TryGetProperty("pages", out var pages)
               && pages.TryGetInt32(out var count)
            ? Math.Max(0, count)
            : 0;
    }

    /// <summary>
    /// Accepts a theme link only if it is an https YouTube address.
    /// </summary>
    /// <remarks>
    /// The field is community-supplied. Handing whatever it contains to a downloader would let an
    /// entry in someone else's database decide what this server fetches. The host is compared
    /// whole: trimming a "www." prefix by characters rather than as a string would also accept
    /// "wyoutube.com", which is exactly the sort of thing this check exists to stop.
    /// </remarks>
    /// <param name="value">The link as the record gave it.</param>
    /// <returns>The link, or <see langword="null"/> if it cannot be used.</returns>
    public static string? AcceptableThemeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;

        return host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)
            || host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase)
                ? value
                : null;
    }

    /// <summary>Reads the theme link out of any ThemerrDB record.</summary>
    /// <param name="json">The record body.</param>
    /// <returns>The link, or null when the record has none that can be used.</returns>
    public static string? ReadThemeUrl(string json)
    {
        using var document = JsonDocument.Parse(json);
        return ReadThemeUrl(document.RootElement);
    }

    private static string? ReadThemeUrl(JsonElement record) =>
        record.ValueKind == JsonValueKind.Object
        && record.TryGetProperty("youtube_theme_url", out var element)
        && element.ValueKind == JsonValueKind.String
            ? AcceptableThemeUrl(element.GetString())
            : null;

    /// <summary>Reads the member film ids and theme out of a collection record.</summary>
    /// <param name="json">The record body.</param>
    /// <returns>The theme URL and the member ids, either of which may be empty.</returns>
    internal static (string? ThemeUrl, IReadOnlyList<string> MemberIds) ParseCollection(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return (null, Array.Empty<string>());
        }

        var url = ReadThemeUrl(document.RootElement);

        var members = new List<string>();
        if (document.RootElement.TryGetProperty("parts", out var parts) && parts.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object
                    && part.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.Number)
                {
                    members.Add(id.GetRawText());
                }
            }
        }

        return (url, members);
    }

    private async Task<IReadOnlyList<IndexEntry>> ReadIndexAsync(
        HttpClient client,
        string type,
        IProgress<double>? progress,
        double progressFrom,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        var pageCount = ParsePageCount(await GetStringAsync(client, $"{BaseUrl}/{type}/pages.json", cancellationToken).ConfigureAwait(false)
                                      ?? "{}");

        if (pageCount == 0)
        {
            throw new InvalidOperationException($"the {type} index reported no pages");
        }

        var entries = new List<IndexEntry>(pageCount * 10);

        for (var page = 1; page <= pageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var body = await GetStringAsync(client, $"{BaseUrl}/{type}/all_page_{page.ToString(CultureInfo.InvariantCulture)}.json", cancellationToken)
                .ConfigureAwait(false);

            if (body is not null)
            {
                entries.AddRange(ParsePage(body));
            }

            progress?.Report(progressFrom + (progressSpan * page / pageCount));
            await Task.Delay(RequestSpacing, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("ThemeForge: read {Count} {Type} from ThemerrDB.", entries.Count, type);
        return entries;
    }

    private async Task<List<CatalogueCollection>> ReadCollectionsAsync(
        HttpClient client,
        IReadOnlyList<IndexEntry> index,
        IProgress<double>? progress,
        double progressFrom,
        double progressSpan,
        CancellationToken cancellationToken)
    {
        var collections = new List<CatalogueCollection>(index.Count);

        for (var i = 0; i < index.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = index[i];
            var body = await GetStringAsync(
                client,
                $"{BaseUrl}/movie_collections/themoviedb/{entry.Id}.json",
                cancellationToken).ConfigureAwait(false);

            if (body is not null)
            {
                var (url, members) = ParseCollection(body);
                if (url is not null && members.Count > 0)
                {
                    collections.Add(new CatalogueCollection(entry.Id, entry.Title, url, members));
                }
            }

            progress?.Report(progressFrom + (progressSpan * (i + 1) / index.Count));
            await Task.Delay(RequestSpacing, cancellationToken).ConfigureAwait(false);
        }

        return collections;
    }

    private async Task<string?> GetStringAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // A miss is an HTML 404 page, not an error document, so the body is only read when
            // the request actually succeeded.
            if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "ThemeForge: could not read {Url}.", url);
            return null;
        }
    }

    private ThemerrDbSnapshot? Read()
    {
        try
        {
            var path = SnapshotPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<ThemerrDbSnapshot>(File.ReadAllText(path));
            if (snapshot is not null)
            {
                _logger.LogInformation(
                    "ThemeForge: the ThemerrDB catalogue holds {Movies} films, {Shows} shows and {Collections} collections, read {Age:0} hours ago.",
                    snapshot.MovieTmdbIds.Count,
                    snapshot.TvShows.Count,
                    snapshot.Collections.Count,
                    snapshot.Age.TotalHours);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "ThemeForge: the ThemerrDB catalogue could not be read and will be rebuilt.");
            return null;
        }
    }

    private void Write(ThemerrDbSnapshot snapshot)
    {
        try
        {
            var path = SnapshotPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory regardless, so the current session still benefits.
            _logger.LogWarning(ex, "ThemeForge: could not save the ThemerrDB catalogue.");
        }
    }
}
