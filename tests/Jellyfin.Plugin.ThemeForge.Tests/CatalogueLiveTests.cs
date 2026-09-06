using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Checks the catalogues still answer the way the code assumes.
/// </summary>
/// <remarks>
/// These are the only assumptions in the plugin that live on somebody else's server: a URL shape
/// and a JSON field name, both of which can change without warning and neither of which any
/// amount of unit testing would catch. Off by default because a test suite that needs the
/// internet is a test suite that fails for the wrong reasons.
/// </remarks>
public class CatalogueLiveTests
{
    /// <summary>Firefly. Chosen because the archive has it and it is unlikely to be removed.</summary>
    private const string KnownTvdbId = "78874";

    /// <summary>Blade Runner.</summary>
    private const string KnownTmdbId = "78";

    [NetworkFact]
    public async Task ThePlexArchiveStillServesAudioForAKnownSeries()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Head, $"https://tvthemes.plexapp.com/{KnownTvdbId}.mp3");
        using var response = await client.SendAsync(request, CancellationToken.None);

        Assert.True(response.IsSuccessStatusCode, $"expected a theme for TVDB {KnownTvdbId}, got {(int)response.StatusCode}");
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
    }

    [NetworkFact]
    public async Task ThePlexArchiveStillAnswersNotFoundForANonexistentSeries()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Head, "https://tvthemes.plexapp.com/99999999.mp3");
        using var response = await client.SendAsync(request, CancellationToken.None);

        // A miss has to be distinguishable from a hit, or every series would appear to have a theme.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [NetworkFact]
    public async Task ThemerrDbStillUsesTheFieldNameTheCodeReads()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var json = await client.GetStringAsync(
            $"https://app.lizardbyte.dev/ThemerrDB/movies/themoviedb/{KnownTmdbId}.json",
            CancellationToken.None);

        var url = Engines.Discovery.ThemerrDbSource.ReadYoutubeUrl(json);

        Assert.NotNull(url);
        Assert.StartsWith("https://", url, StringComparison.Ordinal);
    }
}
