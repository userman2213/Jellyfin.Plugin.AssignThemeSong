using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>One stage of the IMDb test, and how it went.</summary>
public class ImdbTestStep
{
    /// <summary>Gets or sets what was tried.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether it worked.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets what happened.</summary>
    public string Detail { get; set; } = string.Empty;

    /// <summary>Gets or sets how long it took.</summary>
    public long ElapsedMs { get; set; }
}

/// <summary>What the settings page's IMDb test found.</summary>
public class ImdbTestResult
{
    /// <summary>Gets or sets a value indicating whether a theme was read.</summary>
    public bool Success { get; set; }

    /// <summary>Gets or sets the IMDb id that was tried.</summary>
    public string ImdbId { get; set; } = string.Empty;

    /// <summary>Gets or sets one line an administrator can act on.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>Gets or sets where the browser came from, when one was used.</summary>
    public string? BrowserSource { get; set; }

    /// <summary>Gets or sets the browser in use.</summary>
    public string? BrowserPath { get; set; }

    /// <summary>Gets or sets the browser's version.</summary>
    public string? BrowserVersion { get; set; }

    /// <summary>Gets or sets the theme the listing named, when it named one.</summary>
    public string? Theme { get; set; }

    /// <summary>Gets or sets how long the whole test took.</summary>
    public long ElapsedMs { get; set; }

    /// <summary>Gets or sets the stages, in order.</summary>
    public IReadOnlyList<ImdbTestStep> Steps { get; set; } = Array.Empty<ImdbTestStep>();

    /// <summary>Gets or sets the listing that was read, as it was read.</summary>
    public IReadOnlyList<string> Entries { get; set; } = Array.Empty<string>();
}

/// <summary>What browser ThemeForge has, if any.</summary>
public class ImdbBrowserStatus
{
    /// <summary>Gets or sets a value indicating whether there is a browser to use.</summary>
    public bool Available { get; set; }

    /// <summary>Gets or sets where it came from.</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets where it is.</summary>
    public string? Path { get; set; }

    /// <summary>Gets or sets its version.</summary>
    public string? Version { get; set; }

    /// <summary>Gets or sets the Chrome for Testing platform key, or null when unsupported.</summary>
    public string? Platform { get; set; }

    /// <summary>Gets or sets a value indicating whether downloading one is allowed.</summary>
    public bool MayDownload { get; set; }

    /// <summary>Gets or sets a message for the page.</summary>
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// Lets an administrator find out whether this server can actually read IMDb.
/// </summary>
/// <remarks>
/// Whether IMDb serves the page or a bot check depends on where the request comes from, and without
/// a way to try it the only symptom either way is themes appearing or not. This runs the real path
/// and says which stage did what, so the answer is a report rather than a guess.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ThemeForge/Imdb")]
[Produces(MediaTypeNames.Application.Json)]
public class ImdbTestController : ControllerBase
{
    /// <summary>
    /// The title used when none is given: a long listing, so an empty result means the fetch
    /// failed rather than the title having no music.
    /// </summary>
    private const string DefaultProbe = "tt0137523";

    private readonly ImdbSoundtrackSource _imdb;
    private readonly IHeadlessBrowser _browser;
    private readonly IChromeProvisioner _provisioner;
    private readonly IThemeForgeLogger<ImdbTestController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ImdbTestController"/> class.</summary>
    /// <param name="sources">Every credits source; the IMDb one is picked out of them.</param>
    /// <param name="browser">The headless browser.</param>
    /// <param name="provisioner">Supplies a browser.</param>
    /// <param name="logger">Logger.</param>
    public ImdbTestController(
        IEnumerable<ICreditsSource> sources,
        IHeadlessBrowser browser,
        IChromeProvisioner provisioner,
        IThemeForgeLogger<ImdbTestController> logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _imdb = sources.OfType<ImdbSoundtrackSource>().Single();
        _browser = browser;
        _provisioner = provisioner;
        _logger = logger;
    }

    /// <summary>Reports what browser is available, without downloading one.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The browser's status.</returns>
    [HttpGet("Browser")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImdbBrowserStatus>> GetBrowser(CancellationToken cancellationToken)
    {
        var path = _browser.FindBrowser();
        var platform = ChromeProvisioner.PlatformKey();

        return new ImdbBrowserStatus
        {
            Available = path is not null,
            Source = _browser.DescribeSource(),
            Path = path,
            Version = path is null
                ? null
                : await _browser.GetVersionAsync(cancellationToken).ConfigureAwait(false),
            Platform = platform,
            MayDownload = Plugin.Config.ImdbDownloadBrowser,
            Message = path is not null
                ? "A browser is available."
                : platform is null
                    ? "Google publishes no Chrome build for this server's processor. Install Chrome or "
                      + "Chromium yourself and set the path below."
                    : "No browser yet. Download one below, or install Chrome or Chromium on the server.",
        };
    }

    /// <summary>Downloads Chrome into ThemeForge's tools folder, unless one is already there.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The browser's status afterwards.</returns>
    [HttpPost("Browser")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ImdbBrowserStatus>> InstallBrowser(CancellationToken cancellationToken)
    {
        _logger.LogInformation("A browser download was asked for from the settings page");

        var install = await _provisioner.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

        return new ImdbBrowserStatus
        {
            Available = install.Success,
            Source = _browser.DescribeSource(),
            Path = install.ExecutablePath,
            Version = install.Success
                ? await _browser.GetVersionAsync(cancellationToken).ConfigureAwait(false)
                : null,
            Platform = ChromeProvisioner.PlatformKey(),
            MayDownload = Plugin.Config.ImdbDownloadBrowser,
            Message = install.Success
                ? install.AlreadyPresent
                    ? "A browser was already installed."
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "Installed Chrome {0} ({1:F0} MB downloaded).",
                        install.Version,
                        install.DownloadedBytes / 1024d / 1024d)
                : install.Error ?? "The download did not work.",
        };
    }

    /// <summary>Deletes the browser ThemeForge downloaded, and its stored session.</summary>
    /// <returns>What was done.</returns>
    [HttpDelete("Browser")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ImdbBrowserStatus> RemoveBrowser()
    {
        var removed = _provisioner.Remove();

        return new ImdbBrowserStatus
        {
            Available = _browser.FindBrowser() is not null,
            Source = _browser.DescribeSource(),
            Platform = ChromeProvisioner.PlatformKey(),
            MayDownload = Plugin.Config.ImdbDownloadBrowser,
            Message = removed
                ? "Removed the downloaded browser."
                : "There was no downloaded browser to remove.",
        };
    }

    /// <summary>Reads one title's soundtrack listing and reports every stage of it.</summary>
    /// <param name="imdbId">The title to try, or none for the default probe.</param>
    /// <param name="isSeries">Whether to choose the theme as though the title were a series.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The report.</returns>
    [HttpPost("Test")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ImdbTestResult>> Test(
        [FromQuery] string? imdbId,
        [FromQuery] bool isSeries,
        CancellationToken cancellationToken)
    {
        var probe = string.IsNullOrWhiteSpace(imdbId) ? DefaultProbe : imdbId!.Trim();

        if (!LooksLikeAnImdbId(probe))
        {
            return BadRequest("That is not an IMDb id. They look like tt0137523.");
        }

        _logger.LogInformation("Testing the IMDb soundtrack lookup with {ImdbId}", probe);

        var steps = new List<ImdbTestStep>();
        var whole = Stopwatch.StartNew();

        var configuration = Plugin.Config;
        var stage = Stopwatch.StartNew();
        var listing = await _imdb.ReadListingAsync(probe, configuration, cancellationToken)
            .ConfigureAwait(false);
        stage.Stop();

        steps.Add(new ImdbTestStep
        {
            Name = listing.UsedBrowser ? "Read the page in a browser" : "Asked IMDb directly",
            Ok = listing.Entries.Count > 0,
            ElapsedMs = stage.ElapsedMilliseconds,
            Detail = listing.Refused
                ? "IMDb did not serve the listing, in a browser or otherwise"
                : listing.Entries.Count > 0
                    ? string.Format(
                        CultureInfo.InvariantCulture,
                        "Read the listing ({0} entries){1}",
                        listing.Entries.Count,
                        listing.UsedBrowser
                            ? ", after the direct request came back as a check"
                            : " from the direct request, no browser needed")
                    : "IMDb answered but listed nothing",
        });

        var theme = ImdbSoundtrackPage.ChooseTheme(listing.Entries, isSeries, null);

        if (listing.Entries.Count > 0)
        {
            steps.Add(new ImdbTestStep
            {
                Name = "Pick the theme",
                Ok = theme is not null,
                ElapsedMs = 0,
                Detail = theme is not null
                    ? $"Took “{theme.Title}”"
                    : isSeries
                        ? "Nothing in the listing looked like a theme"
                        : "No entry names itself a theme, and for a film the first entry is only "
                          + "whatever plays first, so nothing was taken",
            });
        }

        whole.Stop();

        var result = new ImdbTestResult
        {
            Success = theme is not null,
            ImdbId = probe,
            ElapsedMs = whole.ElapsedMilliseconds,
            Steps = steps,
            Theme = theme is null ? null : new ThemeSong(theme.Title, theme.Performer).ToString(),
            Entries = listing.Entries
                .Select(entry => entry.Performer is null
                    ? entry.Title
                    : entry.Title + " — " + entry.Performer)
                .ToList(),
            Summary = Describe(listing, theme, isSeries),
        };

        if (listing.UsedBrowser || listing.Refused)
        {
            result.BrowserPath = _browser.FindBrowser();
            result.BrowserSource = _browser.DescribeSource();
            if (result.BrowserPath is not null)
            {
                result.BrowserVersion = await _browser.GetVersionAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _logger.LogInformation("The IMDb test finished: {Summary}", result.Summary);
        return result;
    }

    /// <summary>Turns the outcome into one line worth reading.</summary>
    private string Describe(ImdbSoundtrackSource.Listing listing, SoundtrackEntry? theme, bool isSeries)
    {
        if (theme is not null)
        {
            return listing.UsedBrowser
                ? $"Working. IMDb named “{theme.Title}” as the theme, read through the headless browser."
                : $"Working. IMDb named “{theme.Title}” as the theme, straight from the direct "
                  + "request — this server does not need the browser at all.";
        }

        if (listing.Refused)
        {
            if (_browser.FindBrowser() is null)
            {
                return "The direct request did not get the listing, and there is no browser to fall "
                     + "back on. Download one below, or set a browser path.";
            }

            return "Neither the direct request nor the browser got the listing. This network is "
                 + "being served a bot check instead of the page; it depends on where the request "
                 + "comes from rather than on anything ThemeForge can change. Nothing else in "
                 + "ThemeForge is affected.";
        }

        if (listing.Entries.Count == 0)
        {
            return "IMDb served the page and it lists no soundtrack for this title. The lookup "
                 + "itself is working.";
        }

        return isSeries
            ? $"IMDb listed {listing.Entries.Count} entries but none of them looked like a theme."
            : $"IMDb listed {listing.Entries.Count} entries, none of which names itself a theme. For "
              + "a film that is the normal answer: the listing is the songs used in it, in the order "
              + "they play, so taking the first would assign the wrong music. The fetch is working.";
    }

    /// <summary>Checks the shape of an id before it is put into a URL.</summary>
    private static bool LooksLikeAnImdbId(string value)
    {
        if (value.Length is < 3 or > 12
            || !value.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var index = 2; index < value.Length; index++)
        {
            if (!char.IsAsciiDigit(value[index]))
            {
                return false;
            }
        }

        return true;
    }
}
