#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Renders a page in headless Chrome and returns the resulting DOM.
    ///
    /// IMDb sits behind a bot filter that answers a plain HTTP request with either a
    /// 403 or a JavaScript challenge page carrying a 2xx status. The challenge solves
    /// itself in a real browser engine and the page then reloads with the content, so
    /// the only dependable way to read a soundtrack listing is to let a browser do it.
    ///
    /// This spawns the browser as a child process rather than taking on a driver
    /// library: the plugin already shells out to FFmpeg, and a NuGet browser driver
    /// would pull a second copy of Chromium onto the server.
    /// </summary>
    public class BrowserPageFetcher
    {
        /// <summary>
        /// Chrome's own headless user agent identifies itself as "HeadlessChrome",
        /// which IMDb rejects outright with 403, so the user agent is always overridden.
        /// </summary>
        private const string ProfileDirectoryName = "browser-profile";

        /// <summary>
        /// A first render can come back as a bot challenge that has not resolved yet;
        /// the retry runs with the clearance cookie the first attempt stored.
        /// </summary>
        private const int MaxRenderAttempts = 2;

        /// <summary>
        /// How long to let Chrome release its profile lock between attempts.
        /// </summary>
        private static readonly TimeSpan ProfileReleaseDelay = TimeSpan.FromSeconds(2);

        private readonly ILogger<BrowserPageFetcher> _logger;
        private readonly ChromiumProvisioner _provisioner;

        /// <summary>
        /// Chrome refuses to run two instances against one profile directory, and the
        /// profile is what carries the bot filter's clearance cookie between lookups.
        /// </summary>
        private readonly SemaphoreSlim _browserLock = new SemaphoreSlim(1, 1);

        private string? _resolvedBrowserPath;
        private bool _browserSearchFailed;

        public BrowserPageFetcher(ILogger<BrowserPageFetcher> logger, ChromiumProvisioner provisioner)
        {
            _logger = logger;
            _provisioner = provisioner;
        }

        /// <summary>
        /// Gets a value indicating whether a usable browser was found on this server.
        /// </summary>
        public bool IsAvailable => ResolveBrowserPath() != null;

        /// <summary>
        /// Locates a browser, in order: the configured path, the copy the plugin
        /// downloaded for itself, then a system installation.
        /// </summary>
        public string? ResolveBrowserPath()
        {
            var configuredPath = Plugin.Instance?.Configuration?.BrowserPath;
            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                if (File.Exists(configuredPath))
                {
                    return configuredPath;
                }

                _logger.LogWarning(
                    "Configured browser path does not exist: {Path}. Falling back to auto-detection.",
                    configuredPath);
            }

            // The plugin's own install is preferred over a system one: it is a known
            // full Chrome build, where a distribution package may be a cut-down variant.
            var managed = _provisioner.FindInstalledBrowser();
            if (managed != null)
            {
                return managed;
            }

            if (_resolvedBrowserPath != null)
            {
                return _resolvedBrowserPath;
            }

            if (_browserSearchFailed)
            {
                return null;
            }

            foreach (var candidate in GetCandidatePaths())
            {
                if (File.Exists(candidate))
                {
                    _logger.LogInformation("Using browser at {Path} for soundtrack lookups", candidate);
                    _resolvedBrowserPath = candidate;
                    return candidate;
                }
            }

            _browserSearchFailed = true;
            return null;
        }

        /// <summary>
        /// Says where the browser in use came from, for the settings page.
        /// </summary>
        public string DescribeBrowserSource()
        {
            var configuredPath = Plugin.Instance?.Configuration?.BrowserPath;
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                return "configured path";
            }

            if (_provisioner.FindInstalledBrowser() != null)
            {
                return "downloaded by the plugin";
            }

            return ResolveBrowserPath() != null ? "installed on the server" : "none";
        }

        /// <summary>
        /// Gets a browser, downloading one first when none is present and the plugin is
        /// allowed to. Returns null when none could be provided.
        /// </summary>
        public async Task<string?> EnsureBrowserAsync(CancellationToken cancellationToken)
        {
            var existing = ResolveBrowserPath();
            if (existing != null)
            {
                return existing;
            }

            if (!(Plugin.Instance?.Configuration?.AutoDownloadBrowser ?? true))
            {
                _logger.LogWarning(
                    "No browser is available and automatic download is switched off. Install Chrome "
                    + "or Chromium, set a browser path, or enable the download in the plugin settings.");
                return null;
            }

            var result = await _provisioner.EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.LogWarning("Could not provide a browser: {Error}", result.Error);
                return null;
            }

            // A fresh install invalidates the "nothing found" short circuit.
            _browserSearchFailed = false;
            return result.ExecutablePath;
        }

        /// <summary>
        /// Common install locations for Chrome and Chromium across platforms.
        /// </summary>
        private static IEnumerable<string> GetCandidatePaths()
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
                yield return Path.Combine(programFiles, "Chromium", "Application", "chrome.exe");
                yield break;
            }

            if (OperatingSystem.IsMacOS())
            {
                yield return "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
                yield return "/Applications/Chromium.app/Contents/MacOS/Chromium";
                yield return "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge";
                yield break;
            }

            // Linux, including the paths used by the common Jellyfin container images.
            yield return "/usr/bin/chromium";
            yield return "/usr/bin/chromium-browser";
            yield return "/usr/bin/google-chrome";
            yield return "/usr/bin/google-chrome-stable";
            yield return "/usr/bin/microsoft-edge";
            yield return "/snap/bin/chromium";
            yield return "/usr/lib/chromium/chromium";
            yield return "/usr/lib/chromium-browser/chromium-browser";
        }

        /// <summary>
        /// Loads a URL in headless Chrome and returns the rendered DOM.
        /// </summary>
        /// <param name="url">Absolute URL to load.</param>
        /// <param name="userAgent">User agent to present.</param>
        /// <param name="looksComplete">
        /// Decides whether a render actually produced the page rather than a bot
        /// challenge. When it rejects the first attempt the render is retried once,
        /// which also covers the browser exiting early while the previous instance
        /// still holds the profile.
        /// </param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The rendered HTML, or null when no browser is available or it failed.</returns>
        public async Task<string?> GetRenderedHtmlAsync(
            string url,
            string userAgent,
            Func<string, bool>? looksComplete,
            CancellationToken cancellationToken)
        {
            var browserPath = await EnsureBrowserAsync(cancellationToken).ConfigureAwait(false);
            if (browserPath == null)
            {
                return null;
            }

            var config = Plugin.Instance?.Configuration;
            var timeoutSeconds = Math.Clamp(config?.BrowserTimeoutSeconds ?? 60, 10, 300);

            await _browserLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                for (var attempt = 1; attempt <= MaxRenderAttempts; attempt++)
                {
                    var html = await RunBrowserAsync(
                        browserPath, url, userAgent, timeoutSeconds, config, cancellationToken)
                        .ConfigureAwait(false);

                    if (html != null && (looksComplete == null || looksComplete(html)))
                    {
                        return html;
                    }

                    if (attempt < MaxRenderAttempts)
                    {
                        _logger.LogDebug(
                            "Render of {Url} came back incomplete, retrying ({Attempt}/{Max})",
                            url,
                            attempt,
                            MaxRenderAttempts);

                        // Chrome holds its profile lock briefly after exiting; a new
                        // instance started inside that window hands off and quits with
                        // only a partial document.
                        await Task.Delay(ProfileReleaseDelay, cancellationToken).ConfigureAwait(false);
                    }
                }

                return null;
            }
            finally
            {
                _browserLock.Release();
            }
        }

        private async Task<string?> RunBrowserAsync(
            string browserPath,
            string url,
            string userAgent,
            int timeoutSeconds,
            PluginConfiguration? config,
            CancellationToken cancellationToken)
        {
            // The profile directory persists the bot filter's clearance cookie, so the
            // challenge only has to be solved once rather than on every lookup.
            var profilePath = GetProfilePath();

            var arguments = new List<string>
            {
                "--headless",
                "--disable-gpu",
                "--hide-scrollbars",
                "--mute-audio",
                "--disable-extensions",
                "--disable-background-networking",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-sync",
                "--disable-blink-features=AutomationControlled",
                $"--user-agent={userAgent}",
                // Fast-forwards timers so the challenge's own delays do not become
                // wall-clock waits, then dumps the DOM once the page settles.
                $"--virtual-time-budget={(timeoutSeconds - 5) * 1000}",
                "--dump-dom"
            };

            if (profilePath != null)
            {
                arguments.Add($"--user-data-dir={profilePath}");
            }

            // Chrome's own sandbox cannot start inside most container images, which is
            // how Jellyfin is usually deployed. The browser only ever loads the lookup
            // URL here, but the flag is exposed so it can be turned off.
            if (config?.BrowserDisableSandbox ?? true)
            {
                arguments.Add("--no-sandbox");
                arguments.Add("--disable-dev-shm-usage");
            }

            arguments.Add(url);

            var startInfo = new ProcessStartInfo
            {
                FileName = browserPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            _logger.LogDebug("Rendering {Url} with {Browser}", url, browserPath);

            using var process = new Process { StartInfo = startInfo };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            var stopwatch = Stopwatch.StartNew();

            try
            {
                process.Start();

                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);

                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var html = await stdoutTask.ConfigureAwait(false);
                var error = await stderrTask.ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "Browser exited with code {ExitCode} while loading {Url}: {Error}",
                        process.ExitCode,
                        url,
                        Truncate(error));
                    return null;
                }

                if (string.IsNullOrWhiteSpace(html))
                {
                    _logger.LogWarning("Browser returned an empty document for {Url}", url);
                    return null;
                }

                _logger.LogDebug(
                    "Rendered {Url} in {Elapsed}ms ({Length} bytes)",
                    url,
                    stopwatch.ElapsedMilliseconds,
                    html.Length);

                return html;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                throw;
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                _logger.LogWarning(
                    "Browser timed out after {Timeout}s loading {Url}", timeoutSeconds, url);
                return null;
            }
            catch (Exception ex)
            {
                TryKill(process);
                _logger.LogWarning(ex, "Could not render {Url} in a browser", url);
                return null;
            }
        }

        /// <summary>
        /// Gets the browser profile directory inside the plugin's data folder,
        /// or null when the plugin has no data folder to write to.
        /// </summary>
        private string? GetProfilePath()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrEmpty(dataPath))
            {
                return null;
            }

            try
            {
                var profilePath = Path.Combine(dataPath, ProfileDirectoryName);
                Directory.CreateDirectory(profilePath);
                return profilePath;
            }
            catch (Exception ex)
            {
                // Without a profile every lookup re-solves the challenge, which is
                // slower but still works.
                _logger.LogWarning(ex, "Could not create the browser profile directory");
                return null;
            }
        }

        private void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not stop the browser process");
            }
        }

        /// <summary>
        /// Reads the browser's version string, for the settings page.
        /// </summary>
        public async Task<string?> GetBrowserVersionAsync(CancellationToken cancellationToken)
        {
            var browserPath = ResolveBrowserPath();
            if (browserPath == null)
            {
                return null;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = browserPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("--version");

                using var process = new Process { StartInfo = startInfo };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));

                process.Start();
                var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

                var version = output.Trim();
                return string.IsNullOrEmpty(version) ? null : version;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read the browser version");
                return null;
            }
        }

        private static string Truncate(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "(no output)";
            }

            value = value.Trim();
            return value.Length <= 500 ? value : value.Substring(0, 500) + "...";
        }
    }
}
