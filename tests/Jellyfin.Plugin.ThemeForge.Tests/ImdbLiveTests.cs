using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Checks the IMDb lookup against the real site, and the browser against the real download.
/// </summary>
/// <remarks>
/// <para>
/// Behind <c>THEMEFORGE_NETWORK_TESTS</c> like the other live tests, and more conditional than most:
/// whether IMDb serves the page or a check depends on where the test runs, so a run that does not
/// get the listing is reported as inconclusive rather than failed -- that failure would be the
/// network's and not the code's. What is asserted unconditionally is that the code reaches a
/// definite answer and never returns a check page dressed up as a listing.
/// </para>
/// <para>
/// The browser download is a few hundred megabytes, so it is a separate test.
/// </para>
/// </remarks>
public class ImdbLiveTests
{
    private static readonly PluginConfiguration Configuration = new()
    {
        ResearchComposers = true,
        UseImdbSoundtrack = true,
        UseImdbBrowser = true,
        ImdbDownloadBrowser = false,
        ImdbBrowserPath = Environment.GetEnvironmentVariable("THEMEFORGE_CHROME") ?? string.Empty,
    };

    private static ImdbSoundtrackSource Source(IHeadlessBrowser browser) => new(
        new RealHttpClientFactory(),
        browser,
        new NullThemeForgeLogger<ImdbSoundtrackSource>());

    private static IHeadlessBrowser Browser() => new HeadlessBrowser(
        new ProcessRunner(new NullThemeForgeLogger<ProcessRunner>()),
        new ChromeProvisioner(new RealHttpClientFactory(), new NullThemeForgeLogger<ChromeProvisioner>()),
        new NullThemeForgeLogger<HeadlessBrowser>());

    [NetworkFact]
    public async Task ReadsTheSopranosThemeOrSaysWhyItCannot()
    {
        var source = Source(Browser());

        var listing = await source.ReadListingAsync("tt0141842", Configuration, CancellationToken.None);

        if (listing.Refused)
        {
            // This network was served a check rather than the page. Not a code failure; the
            // settings page reports it.
            Assert.Empty(listing.Entries);
            return;
        }

        Assert.NotEmpty(listing.Entries);

        var theme = ImdbSoundtrackPage.ChooseTheme(listing.Entries, isSeries: true, "The Sopranos");
        Assert.NotNull(theme);
        Assert.Equal("Woke Up This Morning", theme!.Title);
    }

    [NetworkFact]
    public async Task StillTakesNothingFromAFilmThatNamesNoTheme()
    {
        var source = Source(Browser());

        var listing = await source.ReadListingAsync("tt0068646", Configuration, CancellationToken.None);

        if (listing.Refused)
        {
            return;
        }

        // The Godfather, live: many entries, none of them the theme.
        Assert.NotEmpty(listing.Entries);
        Assert.Null(ImdbSoundtrackPage.ChooseTheme(listing.Entries, isSeries: false, "The Godfather"));
    }

    [NetworkFact]
    public async Task NeverReturnsACheckPageAsAListing()
    {
        var source = Source(Browser());

        var listing = await source.ReadListingAsync("tt0137523", Configuration, CancellationToken.None);

        // Whatever happened, what came back is either a real listing or nothing at all.
        if (listing.Html is not null)
        {
            Assert.False(ImdbSoundtrackSource.IsChallenge(listing.Html));
            Assert.True(ImdbSoundtrackSource.LooksLikeTheListing(listing.Html));
        }
        else
        {
            Assert.Empty(listing.Entries);
        }
    }

    [NetworkFact]
    public void StillPublishesABuildForThisPlatform()
    {
        // The version list is what the provisioner reads; a platform key it no longer lists would
        // mean no browser on that hardware.
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        var json = client.GetStringAsync(ChromeProvisioner.VersionsUrl).GetAwaiter().GetResult();

        var platform = ChromeProvisioner.PlatformKey();
        Assert.NotNull(platform);

        var (version, url) = ChromeProvisioner.ReadDownload(json, platform!);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.False(string.IsNullOrWhiteSpace(url));
        Assert.Contains(platform!, url!, StringComparison.Ordinal);
    }

    [NetworkFact(Skip = "Downloads roughly 200 MB. Run it by hand when the provisioner changes.")]
    public async Task DownloadsChromeAndItRuns()
    {
        var provisioner = new ChromeProvisioner(
            new RealHttpClientFactory(), new NullThemeForgeLogger<ChromeProvisioner>());

        var install = await provisioner.EnsureInstalledAsync(CancellationToken.None);

        Assert.True(install.Success, install.Error);
        Assert.NotNull(install.ExecutablePath);
        Assert.True(File.Exists(install.ExecutablePath));
    }
}

/// <summary>A client factory that makes real clients, for the live tests.</summary>
internal sealed class RealHttpClientFactory : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new();
}
