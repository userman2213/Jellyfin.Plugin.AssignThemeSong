#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.xThemeSong.Services
{
    /// <summary>
    /// Downloads and manages a private Chrome installation for the plugin.
    ///
    /// Soundtrack lookups need a real browser engine, and a Jellyfin server usually has
    /// no browser installed. Chrome is far too large to put inside the plugin package
    /// (roughly 100-190 MB per platform, across six platforms), so it is fetched on
    /// demand from Google's Chrome for Testing archive into the plugin's data folder,
    /// the same way browser automation libraries provision one.
    /// </summary>
    public class ChromiumProvisioner
    {
        private const string VersionsUrl =
            "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json";

        private const string InstallDirectoryName = "chromium";

        private readonly ILogger<ChromiumProvisioner> _logger;

        /// <summary>
        /// One install at a time: two concurrent downloads would fight over the
        /// extraction directory.
        /// </summary>
        private readonly SemaphoreSlim _installLock = new SemaphoreSlim(1, 1);

        public ChromiumProvisioner(ILogger<ChromiumProvisioner> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Gets the Chrome for Testing platform key for the running machine,
        /// or null when this platform has no published build.
        /// </summary>
        public static string? GetPlatformKey()
        {
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture;

            if (OperatingSystem.IsWindows())
            {
                return arch switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "win64",
                    System.Runtime.InteropServices.Architecture.X86 => "win32",
                    // Windows on ARM runs the x64 build through emulation.
                    System.Runtime.InteropServices.Architecture.Arm64 => "win64",
                    _ => null
                };
            }

            if (OperatingSystem.IsMacOS())
            {
                return arch switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => "mac-arm64",
                    System.Runtime.InteropServices.Architecture.X64 => "mac-x64",
                    _ => null
                };
            }

            if (OperatingSystem.IsLinux())
            {
                return arch switch
                {
                    System.Runtime.InteropServices.Architecture.X64 => "linux64",
                    System.Runtime.InteropServices.Architecture.Arm64 => "linux-arm64",
                    _ => null
                };
            }

            return null;
        }

        /// <summary>
        /// Gets the root directory holding managed Chrome installs.
        /// </summary>
        private string? GetInstallRoot()
        {
            var dataPath = Plugin.Instance?.DataFolderPath;
            return string.IsNullOrEmpty(dataPath)
                ? null
                : Path.Combine(dataPath, InstallDirectoryName);
        }

        /// <summary>
        /// Finds an already-downloaded Chrome, without contacting the network.
        /// </summary>
        /// <returns>Path to the executable, or null when nothing is installed.</returns>
        public string? FindInstalledBrowser()
        {
            var root = GetInstallRoot();
            if (root == null || !Directory.Exists(root))
            {
                return null;
            }

            try
            {
                // Newest version directory first, so an upgrade takes effect immediately.
                foreach (var versionDirectory in Directory.GetDirectories(root)
                             .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var executable = FindExecutable(versionDirectory);
                    if (executable != null)
                    {
                        return executable;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not inspect the managed browser directory {Path}", root);
            }

            return null;
        }

        /// <summary>
        /// Locates the Chrome executable inside an extracted archive, whose layout
        /// differs per platform.
        /// </summary>
        private static string? FindExecutable(string directory)
        {
            var names = OperatingSystem.IsWindows()
                ? new[] { "chrome.exe" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "Google Chrome for Testing", "Google Chrome" }
                    : new[] { "chrome" };

            foreach (var name in names)
            {
                var match = Directory
                    .EnumerateFiles(directory, name, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        /// <summary>
        /// Downloads and installs Chrome for this platform, unless one is already
        /// installed.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the install attempt.</returns>
        public async Task<BrowserInstallResult> EnsureInstalledAsync(CancellationToken cancellationToken)
        {
            var existing = FindInstalledBrowser();
            if (existing != null)
            {
                return BrowserInstallResult.AlreadyInstalled(existing);
            }

            var platform = GetPlatformKey();
            if (platform == null)
            {
                return BrowserInstallResult.Failed(
                    "Chrome for Testing publishes no build for this operating system and processor "
                    + "architecture. Install Chrome or Chromium with your package manager instead, "
                    + "then set the browser path in the plugin settings.");
            }

            var root = GetInstallRoot();
            if (root == null)
            {
                return BrowserInstallResult.Failed("The plugin has no data folder to install into.");
            }

            await _installLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Another caller may have completed the install while we waited.
                existing = FindInstalledBrowser();
                if (existing != null)
                {
                    return BrowserInstallResult.AlreadyInstalled(existing);
                }

                return await DownloadAndExtractAsync(platform, root, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not install a browser");
                return BrowserInstallResult.Failed(ex.Message);
            }
            finally
            {
                _installLock.Release();
            }
        }

        private async Task<BrowserInstallResult> DownloadAndExtractAsync(
            string platform,
            string root,
            CancellationToken cancellationToken)
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
            httpClient.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent", BrowserHttpClient.GetUserAgent());

            var (version, downloadUrl) = await ResolveDownloadAsync(httpClient, platform, cancellationToken)
                .ConfigureAwait(false);

            if (version == null || downloadUrl == null)
            {
                return BrowserInstallResult.Failed(
                    $"Chrome for Testing lists no download for platform '{platform}'.");
            }

            _logger.LogInformation(
                "Downloading Chrome {Version} for {Platform}. This is a large one-off download "
                + "(roughly 150-200 MB) and may take a few minutes.",
                version,
                platform);

            var versionDirectory = Path.Combine(root, version);
            // Extract beside the target and move into place, so an interrupted install
            // never leaves a half-extracted directory that looks complete.
            var stagingDirectory = versionDirectory + ".partial";
            var archivePath = Path.Combine(root, $"chrome-{version}-{platform}.zip");

            Directory.CreateDirectory(root);
            DeleteIfExists(stagingDirectory);

            try
            {
                await using (var response = await httpClient
                                 .GetStreamAsync(downloadUrl, cancellationToken).ConfigureAwait(false))
                await using (var file = File.Create(archivePath))
                {
                    await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                }

                var downloadedBytes = new FileInfo(archivePath).Length;
                _logger.LogInformation(
                    "Downloaded {Megabytes:F0} MB, extracting", downloadedBytes / 1024d / 1024d);

                ZipFile.ExtractToDirectory(archivePath, stagingDirectory);

                var executable = FindExecutable(stagingDirectory);
                if (executable == null)
                {
                    DeleteIfExists(stagingDirectory);
                    return BrowserInstallResult.Failed(
                        "The downloaded archive did not contain a Chrome executable.");
                }

                MakeExecutable(stagingDirectory);

                DeleteIfExists(versionDirectory);
                Directory.Move(stagingDirectory, versionDirectory);

                var installedExecutable = FindExecutable(versionDirectory);
                if (installedExecutable == null)
                {
                    return BrowserInstallResult.Failed(
                        "The Chrome executable went missing after installation.");
                }

                _logger.LogInformation("Installed Chrome {Version} at {Path}", version, installedExecutable);
                return BrowserInstallResult.Installed(installedExecutable, version, downloadedBytes);
            }
            finally
            {
                DeleteIfExists(archivePath);
                DeleteIfExists(stagingDirectory);
            }
        }

        /// <summary>
        /// Reads the current stable version and its download URL for a platform.
        /// </summary>
        private async Task<(string? Version, string? Url)> ResolveDownloadAsync(
            HttpClient httpClient,
            string platform,
            CancellationToken cancellationToken)
        {
            var json = await httpClient.GetStringAsync(VersionsUrl, cancellationToken).ConfigureAwait(false);

            using var document = JsonDocument.Parse(json);

            if (!document.RootElement.TryGetProperty("channels", out var channels)
                || !channels.TryGetProperty("Stable", out var stable))
            {
                return (null, null);
            }

            var version = stable.TryGetProperty("version", out var versionElement)
                ? versionElement.GetString()
                : null;

            if (!stable.TryGetProperty("downloads", out var downloads)
                || !downloads.TryGetProperty("chrome", out var chromeDownloads))
            {
                return (version, null);
            }

            foreach (var entry in chromeDownloads.EnumerateArray())
            {
                if (entry.TryGetProperty("platform", out var entryPlatform)
                    && string.Equals(entryPlatform.GetString(), platform, StringComparison.Ordinal)
                    && entry.TryGetProperty("url", out var url))
                {
                    return (version, url.GetString());
                }
            }

            return (version, null);
        }

        /// <summary>
        /// Restores the execute bit, which a zip archive does not carry on Unix.
        /// </summary>
        private void MakeExecutable(string directory)
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(path);
                var extension = Path.GetExtension(path);

                // Chrome ships helper binaries beside the main executable; marking the
                // extensionless files and shared objects covers them without a guess list.
                if (extension.Length != 0
                    && !string.Equals(extension, ".so", StringComparison.OrdinalIgnoreCase)
                    && !extension.StartsWith(".so.", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.SetUnixFileMode(
                        path,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                        | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not set the execute bit on {Path}", path);
                }
            }
        }

        private void DeleteIfExists(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not delete {Path}", path);
            }
        }
    }

    /// <summary>
    /// The outcome of a browser install attempt.
    /// </summary>
    public class BrowserInstallResult
    {
        public bool Success { get; private set; }

        public string? ExecutablePath { get; private set; }

        public string? Version { get; private set; }

        public bool WasAlreadyInstalled { get; private set; }

        public long DownloadedBytes { get; private set; }

        public string? Error { get; private set; }

        public static BrowserInstallResult AlreadyInstalled(string path) => new BrowserInstallResult
        {
            Success = true,
            ExecutablePath = path,
            WasAlreadyInstalled = true
        };

        public static BrowserInstallResult Installed(string path, string version, long bytes) =>
            new BrowserInstallResult
            {
                Success = true,
                ExecutablePath = path,
                Version = version,
                DownloadedBytes = bytes
            };

        public static BrowserInstallResult Failed(string error) => new BrowserInstallResult
        {
            Success = false,
            Error = error
        };
    }
}
