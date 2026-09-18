using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Asks MusicBrainz who is credited on a work's soundtrack, one work at a time.
/// </summary>
/// <remarks>
/// <para>
/// Asked only about works Wikidata could not answer, and only by id. MusicBrainz records the
/// relationship between a soundtrack release and the film's IMDb page, so the IMDb id leads
/// straight to the release and its credited artist, who for a score is the composer.
/// </para>
/// <para>
/// Its search is deliberately not used, and this matters: searching for "Alien" returns a J-pop
/// single, and searching for "Battlestar Galactica" returns the composer of the 1978 series for
/// the 2004 one. A wrong composer is worse than none, because it would be searched for and
/// believed. The id-keyed lookup has no such failure.
/// </para>
/// <para>
/// One request per second, which is what the service asks for. That is the reason this source is
/// second: a library of a thousand titles would take a quarter of an hour if it went first, where
/// Wikidata answers three quarters of them in a handful of requests.
/// </para>
/// </remarks>
public sealed class MusicBrainzCreditsSource : ICreditsSource
{
    /// <summary>The relationship lookup, which finds a release group from the film's IMDb page.</summary>
    public const string ByImdbUrl =
        "https://musicbrainz.org/ws/2/url?resource=https%3A%2F%2Fwww.imdb.com%2Ftitle%2F{0}%2F&inc=release-group-rels+artist-credits&fmt=json";

    /// <summary>A release group already known, which skips the relationship lookup.</summary>
    public const string ByReleaseGroup =
        "https://musicbrainz.org/ws/2/release-group/{0}?inc=artist-credits&fmt=json";

    /// <summary>What a compilation is credited to, which names nobody.</summary>
    private const string Nobody = "Various Artists";

    /// <summary>How many names are kept for one work.</summary>
    private const int MaxNames = 3;

    /// <summary>The rate the service asks callers to keep to.</summary>
    private static readonly TimeSpan Spacing = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How many times a request refused for load is tried again.</summary>
    private const int Attempts = 3;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<MusicBrainzCreditsSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="MusicBrainzCreditsSource"/> class.</summary>
    /// <param name="httpClientFactory">Makes the requests.</param>
    /// <param name="logger">Logger.</param>
    public MusicBrainzCreditsSource(IHttpClientFactory httpClientFactory, IThemeForgeLogger<MusicBrainzCreditsSource> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "MusicBrainz";

    /// <inheritdoc />
    public int Order => 10;

    /// <inheritdoc />
    public bool IsEnabled(PluginConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return configuration.ResearchComposers && configuration.UseMusicBrainzForComposers;
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

        // Nothing to ask about a work with no IMDb id and no release group from the first source.
        var askable = batch
            .Where(work => !string.IsNullOrWhiteSpace(work.ImdbId) || !string.IsNullOrWhiteSpace(work.ReleaseGroupId))
            .ToList();

        if (askable.Count == 0)
        {
            return found;
        }

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = RequestTimeout;

        for (var index = 0; index < askable.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var work = askable[index];
            var url = work.ReleaseGroupId is { Length: > 0 } group
                ? string.Format(CultureInfo.InvariantCulture, ByReleaseGroup, Uri.EscapeDataString(group))
                : string.Format(CultureInfo.InvariantCulture, ByImdbUrl, Uri.EscapeDataString(work.ImdbId!));

            var body = await GetAsync(client, url, work.Label, cancellationToken).ConfigureAwait(false);
            if (body is not null)
            {
                var credits = Parse(body);
                if (credits.Any)
                {
                    found[work.Key] = credits;
                }
            }

            progress?.Report(100.0 * (index + 1) / askable.Count);

            if (index < askable.Count - 1)
            {
                await Task.Delay(Spacing, cancellationToken).ConfigureAwait(false);
            }
        }

        return found;
    }

    /// <summary>Fetches one document, waiting and trying again when the service is busy.</summary>
    private async Task<string?> GetAsync(HttpClient client, string url, string label, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                PoliteRequest.Identify(request);

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

                // 503 is how the service says "too fast", not "broken", so it is worth waiting out.
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable && attempt < Attempts)
                {
                    await Task.Delay(Spacing * attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.NotFound || !response.IsSuccessStatusCode)
                {
                    return null;
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "ThemeForge: could not ask MusicBrainz about \"{Item}\".", label);
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the credited artists out of either shape of answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The relationship lookup wraps each release group in a relation, under a key spelled with an
    /// underscore; asking about a release group directly returns one unwrapped. Both carry the
    /// same artist credit, so both are read here.
    /// </para>
    /// <para>
    /// A title's linked releases are not all its score. Battlestar Galactica's IMDb page links
    /// four: three season soundtracks credited to Bear McCreary and a solo piano album credited to
    /// him and its pianist. Taking every credit would hand the pianist to the query planner as a
    /// composer and search for a theme under their name. Only the names credited on the most
    /// releases are kept, which keeps co-composers credited throughout and drops a guest on one.
    /// </para>
    /// </remarks>
    /// <param name="json">The service's JSON.</param>
    /// <returns>What was found, which may be nothing.</returns>
    internal static ResearchedCredits Parse(string json)
    {
        var credited = new List<Credit>();
        string? releaseGroup = null;

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.TryGetProperty("relations", out var relations) && relations.ValueKind == JsonValueKind.Array)
        {
            foreach (var relation in relations.EnumerateArray())
            {
                if (relation.TryGetProperty("release_group", out var group))
                {
                    releaseGroup ??= Id(group);
                    ReadCredits(group, credited);
                }
            }
        }
        else
        {
            releaseGroup = Id(root);
            ReadCredits(root, credited);
        }

        var names = Prevailing(credited);

        return names.Count == 0 && releaseGroup is null
            ? ResearchedCredits.None
            : new ResearchedCredits(names, null, releaseGroup);
    }

    /// <summary>One name and how many of the title's releases credit it.</summary>
    private sealed class Credit
    {
        /// <summary>Gets or sets the name as first spelled.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Gets or sets how many releases carry it.</summary>
        public int Releases { get; set; }
    }

    /// <summary>Keeps the names credited on the most of the title's releases, in the order met.</summary>
    private static IReadOnlyList<string> Prevailing(List<Credit> credited)
    {
        if (credited.Count == 0)
        {
            return Array.Empty<string>();
        }

        var most = credited.Max(credit => credit.Releases);

        return credited
            .Where(credit => credit.Releases == most)
            .Take(MaxNames)
            .Select(credit => credit.Name)
            .ToList();
    }

    private static string? Id(JsonElement element) =>
        element.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value ? value : null;

    private static void ReadCredits(JsonElement group, List<Credit> credited)
    {
        if (!group.TryGetProperty("artist-credit", out var credits) || credits.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var credit in credits.EnumerateArray())
        {
            var name = Text(credit, "name")
                ?? (credit.TryGetProperty("artist", out var artist) ? Text(artist, "name") : null);

            // A compilation is credited to nobody in particular, and searching for that phrase
            // would find every compilation ever made.
            if (name is null || string.Equals(name, Nobody, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var known = credited.Find(entry => string.Equals(entry.Name, name, StringComparison.OrdinalIgnoreCase));
            if (known is null)
            {
                credited.Add(new Credit { Name = name, Releases = 1 });
            }
            else
            {
                known.Releases++;
            }
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.GetString() is { Length: > 0 } text ? text : null;
}
