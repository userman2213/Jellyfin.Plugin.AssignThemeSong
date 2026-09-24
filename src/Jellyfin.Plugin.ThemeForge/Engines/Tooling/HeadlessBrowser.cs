using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>Loads a page in a real browser engine and returns the DOM it produced.</summary>
public interface IHeadlessBrowser
{
    /// <summary>Gets the browser that would be used, or null when there is none.</summary>
    /// <returns>The executable path, or null.</returns>
    string? FindBrowser();

    /// <summary>Says where the browser in use came from, for the settings page.</summary>
    /// <returns>A short description.</returns>
    string DescribeSource();

    /// <summary>Reads the browser's version string.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or null when no browser could be asked.</returns>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Loads a URL and returns the rendered DOM.
    /// </summary>
    /// <param name="url">The absolute URL to load.</param>
    /// <param name="isComplete">
    /// Decides whether a render produced the page rather than a bot challenge standing in for it.
    /// A render it rejects is retried once.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The rendered HTML, or null when it could not be had.</returns>
    Task<string?> RenderAsync(string url, Func<string, bool> isComplete, CancellationToken cancellationToken);
}

/// <summary>
/// Drives headless Chrome as a child process to render a page.
/// </summary>
/// <remarks>
/// <para>
/// Used for one thing only: IMDb, which answers a server's plain request with a bot challenge
/// that carries a <c>2xx</c> status and clears only when a browser engine runs its script. The
/// browser is started the way yt-dlp and FFmpeg are, through <see cref="IProcessRunner"/>, rather
/// than through a driver library that would put a second copy of Chromium on the server.
/// </para>
/// <para>
/// The profile directory is what makes this affordable. It keeps the clearance the challenge
/// grants, so the challenge is solved once rather than per lookup: a cold render has been measured
/// at over a minute and a warm one at a few seconds. Chrome will not run two instances against one
/// profile, so renders are serialised.
/// </para>
/// </remarks>
public sealed class HeadlessBrowser : IHeadlessBrowser
{
    /// <summary>How many times a render is attempted before giving up.</summary>
    /// <remarks>
    /// A first render can come back as a challenge that has not resolved; the retry runs with the
    /// clearance the first attempt stored. It also covers Chrome exiting early because the previous
    /// instance still held the profile lock, which produces a partial document.
    /// </remarks>
    private const int Attempts = 2;

    private static readonly TimeSpan ProfileRelease = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);

    private readonly IProcessRunner _processes;
    private readonly IChromeProvisioner _provisioner;
    private readonly IThemeForgeLogger<HeadlessBrowser> _logger;
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    private string? _systemBrowser;
    private bool _searchedForSystemBrowser;

    /// <summary>Initializes a new instance of the <see cref="HeadlessBrowser"/> class.</summary>
    /// <param name="processes">Starts the browser.</param>
    /// <param name="provisioner">Supplies a browser when the server has none.</param>
    /// <param name="logger">Logger.</param>
    public HeadlessBrowser(
        IProcessRunner processes,
        IChromeProvisioner provisioner,
        IThemeForgeLogger<HeadlessBrowser> logger)
    {
        _processes = processes;
        _provisioner = provisioner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? FindBrowser()
    {
        var configured = Plugin.Instance?.Configuration?.ImdbBrowserPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            _logger.LogWarning(
                "The browser path in the settings does not exist: {Path}. Looking for one instead.",
                configured);
        }

        // The plugin's own copy first: it is a known full Chrome build, where a distribution
        // package may be a cut-down variant that does not clear the challenge.
        var provisioned = _provisioner.FindInstalled();
        if (provisioned is not null)
        {
            return provisioned;
        }

        if (_searchedForSystemBrowser)
        {
            return _systemBrowser;
        }

        _searchedForSystemBrowser = true;
        foreach (var candidate in SystemLocations())
        {
            if (File.Exists(candidate))
            {
                _logger.LogInformation("Using the browser installed at {Path}", candidate);
                _systemBrowser = candidate;
                break;
            }
        }

        return _systemBrowser;
    }

    /// <inheritdoc />
    public string DescribeSource()
    {
        var configured = Plugin.Instance?.Configuration?.ImdbBrowserPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return "the path in the settings";
        }

        if (_provisioner.FindInstalled() is not null)
        {
            return "downloaded by ThemeForge";
        }

        return FindBrowser() is not null ? "installed on the server" : "none";
    }

    /// <inheritdoc />
    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken)
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            return null;
        }

        var result = await _processes
            .RunAsync(browser, new[] { "--version" }, VersionTimeout, cancellationToken)
            .ConfigureAwait(false);

        var version = result.StandardOutput.Trim();
        return version.Length == 0 ? null : version;
    }

    /// <inheritdoc />
    public async Task<string?> RenderAsync(
        string url,
        Func<string, bool> isComplete,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(isComplete);

        var browser = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
        if (browser is null)
        {
            return null;
        }

        var configuration = Plugin.Instance?.Configuration;
        var timeout = TimeSpan.FromSeconds(Math.Clamp(configuration?.ImdbBrowserTimeoutSeconds ?? 120, 20, 600));

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; attempt <= Attempts; attempt++)
            {
                var html = await RenderOnceAsync(browser, url, timeout, configuration, cancellationToken)
                    .ConfigureAwait(false);

                if (html is not null && isComplete(html))
                {
                    return html;
                }

                if (attempt < Attempts)
                {
                    _logger.LogDebug(
                        "The render of {Url} came back incomplete; trying once more", url);
                    await Task.Delay(ProfileRelease, cancellationToken).ConfigureAwait(false);
                }
            }

            return null;
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Gets a browser, downloading one if that is allowed and none is present.</summary>
    private async Task<string?> EnsureBrowserAsync(CancellationToken cancellationToken)
    {
        var browser = FindBrowser();
        if (browser is not null)
        {
            return browser;
        }

        if (!(Plugin.Instance?.Configuration?.ImdbDownloadBrowser ?? true))
        {
            _logger.LogWarning(
                "IMDb needs a browser, none is installed, and downloading one is switched off. "
                + "Install Chrome or Chromium, or set a browser path in the settings.");
            return null;
        }

        var installed = await _provisioner.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        if (!installed.Success)
        {
            _logger.LogWarning("No browser could be provided: {Reason}", installed.Error);
            return null;
        }

        return installed.ExecutablePath;
    }

    private async Task<string?> RenderOnceAsync(
        string browser,
        string url,
        TimeSpan timeout,
        PluginConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var arguments = new List<string>
        {
            "--headless",
            "--disable-gpu",
            "--hide-scrollbars",
            "--mute-audio",
            "--disable-extensions",
            "--disable-background-networking",
            "--disable-breakpad",
            "--disable-sync",
            "--no-first-run",
            "--no-default-browser-check",
            "--no-service-autorun",
            "--password-store=basic",
            "--disable-blink-features=AutomationControlled",

            // Chrome's own headless user agent says "HeadlessChrome", which IMDb answers with 403
            // outright, so it is always replaced.
            "--user-agent=" + BrowserIdentity.UserAgent,

            // Advances timers as fast as it can, so the challenge's own delays are not spent in
            // wall-clock time, then prints the DOM once the page settles.
            "--virtual-time-budget=" + ((int)timeout.TotalMilliseconds - 5000).ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--dump-dom",
        };

        var profile = _provisioner.ProfileDirectory;
        if (profile is not null)
        {
            arguments.Add("--user-data-dir=" + profile);
        }

        // Chrome's sandbox cannot start inside most container images, which is how Jellyfin is
        // usually deployed. The browser only ever loads the lookup URL, and the flag is exposed
        // so a server running outside a container can keep the sandbox.
        if (configuration?.ImdbBrowserNoSandbox ?? true)
        {
            arguments.Add("--no-sandbox");
            arguments.Add("--disable-dev-shm-usage");
        }

        arguments.Add(url);

        var result = await _processes
            .RunAsync(browser, arguments, timeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            _logger.LogWarning(
                "The browser failed to load {Url}: {Error}", url, result.ErrorSummary);
            return null;
        }

        if (string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            _logger.LogWarning("The browser returned an empty document for {Url}", url);
            return null;
        }

        return result.StandardOutput;
    }

    /// <summary>Where Chrome, Chromium and Edge are usually installed.</summary>
    private static IEnumerable<string> SystemLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            yield return Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe");
            yield return Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe");
            yield return Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
            yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
            yield break;
        }

        yield return "/usr/bin/chromium";
        yield return "/usr/bin/chromium-browser";
        yield return "/usr/bin/google-chrome";
        yield return "/usr/bin/google-chrome-stable";
        yield return "/usr/bin/microsoft-edge";
        yield return "/snap/bin/chromium";
        yield return "/usr/lib/chromium/chromium";
        yield return "/usr/lib/chromium-browser/chromium-browser";
    }
}
