using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>What came of trying to provide a browser.</summary>
/// <param name="Success">Whether there is now a browser to use.</param>
/// <param name="ExecutablePath">Where it is.</param>
/// <param name="Version">Which version was installed, when one was.</param>
/// <param name="AlreadyPresent">Whether it was already there and nothing was downloaded.</param>
/// <param name="DownloadedBytes">How much was downloaded.</param>
/// <param name="Error">Why there is no browser, when there is none.</param>
public sealed record ChromeInstall(
    bool Success,
    string? ExecutablePath,
    string? Version,
    bool AlreadyPresent,
    long DownloadedBytes,
    string? Error)
{
    /// <summary>A browser was already installed.</summary>
    /// <param name="path">Where it is.</param>
    /// <returns>The result.</returns>
    public static ChromeInstall Present(string path) => new(true, path, null, true, 0, null);

    /// <summary>A browser was downloaded.</summary>
    /// <param name="path">Where it is.</param>
    /// <param name="version">Which version.</param>
    /// <param name="bytes">How much was downloaded.</param>
    /// <returns>The result.</returns>
    public static ChromeInstall Downloaded(string path, string version, long bytes) =>
        new(true, path, version, false, bytes, null);

    /// <summary>No browser could be provided.</summary>
    /// <param name="error">Why not.</param>
    /// <returns>The result.</returns>
    public static ChromeInstall Failed(string error) => new(false, null, null, false, 0, error);
}

/// <summary>Provides a Chrome build for the server ThemeForge is running on.</summary>
public interface IChromeProvisioner
{
    /// <summary>Gets the browser profile directory, or null when there is nowhere to keep one.</summary>
    string? ProfileDirectory { get; }

    /// <summary>Finds an already-downloaded Chrome, without asking the network.</summary>
    /// <returns>The executable path, or null.</returns>
    string? FindInstalled();

    /// <summary>Downloads Chrome unless one is already installed.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What came of it.</returns>
    Task<ChromeInstall> EnsureInstalledAsync(CancellationToken cancellationToken);

    /// <summary>Deletes the downloaded browser and its profile, freeing the disk space.</summary>
    /// <returns><see langword="true"/> when there was something to delete.</returns>
    bool Remove();
}

/// <summary>
/// Downloads Chrome from Google's Chrome for Testing archive into ThemeForge's tools folder.
/// </summary>
/// <remarks>
/// <para>
/// A Jellyfin server almost never has a browser on it, and the plugin package cannot carry one:
/// the builds run 100-190 MB each across six platforms, and a Jellyfin manifest offers one
/// artifact per version with no way to pick by platform. So it is fetched on demand, the way
/// yt-dlp already is.
/// </para>
/// <para>
/// The full <c>chrome</c> build is taken rather than <c>chrome-headless-shell</c>. The shell is the
/// older headless implementation and the cheaper download, but it is the less complete browser, and
/// the point of using one at all is to be a complete browser.
/// </para>
/// </remarks>
public sealed class ChromeProvisioner : IChromeProvisioner
{
    /// <summary>Where the current builds of each platform are listed.</summary>
    public const string VersionsUrl =
        "https://googlechromelabs.github.io/chrome-for-testing/last-known-good-versions-with-downloads.json";

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(20);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IThemeForgeLogger<ChromeProvisioner> _logger;
    private readonly SemaphoreSlim _oneInstallAtATime = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="ChromeProvisioner"/> class.</summary>
    /// <param name="httpClientFactory">Makes the request.</param>
    /// <param name="logger">Logger.</param>
    public ChromeProvisioner(IHttpClientFactory httpClientFactory, IThemeForgeLogger<ChromeProvisioner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? ProfileDirectory
    {
        get
        {
            var root = Root;
            if (root is null)
            {
                return null;
            }

            try
            {
                var profile = Path.Combine(root, "profile");
                Directory.CreateDirectory(profile);
                return profile;
            }
            catch (IOException ex)
            {
                // Without a profile every render starts from nothing: slower, still works.
                _logger.LogWarning(ex, "Could not make the browser profile directory");
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Not allowed to make the browser profile directory");
                return null;
            }
        }
    }

    /// <summary>Gets where downloaded browsers live, or null when the plugin has no tools folder.</summary>
    private static string? Root
    {
        get
        {
            var tools = Plugin.Instance?.ToolsPath;
            return string.IsNullOrEmpty(tools) ? null : Path.Combine(tools, "chrome");
        }
    }

    /// <summary>
    /// Gets the Chrome for Testing platform key for this machine, or null when Google publishes no
    /// build for it.
    /// </summary>
    /// <returns>The key, such as <c>linux64</c>.</returns>
    public static string? PlatformKey()
    {
        var architecture = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return architecture switch
            {
                Architecture.X64 => "win64",
                Architecture.X86 => "win32",

                // Windows on ARM runs the x64 build under emulation.
                Architecture.Arm64 => "win64",
                _ => null,
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return architecture switch
            {
                Architecture.Arm64 => "mac-arm64",
                Architecture.X64 => "mac-x64",
                _ => null,
            };
        }

        if (OperatingSystem.IsLinux())
        {
            return architecture switch
            {
                Architecture.X64 => "linux64",
                Architecture.Arm64 => "linux-arm64",
                _ => null,
            };
        }

        return null;
    }

    /// <inheritdoc />
    public string? FindInstalled()
    {
        var root = Root;
        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        try
        {
            // Newest version directory first, so an upgrade takes effect at once.
            foreach (var version in Directory.GetDirectories(root)
                         .Where(path => !string.Equals(Path.GetFileName(path), "profile", StringComparison.Ordinal))
                         .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var executable = FindExecutable(version);
                if (executable is not null)
                {
                    return executable;
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not look through {Path} for a browser", root);
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<ChromeInstall> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        var present = FindInstalled();
        if (present is not null)
        {
            return ChromeInstall.Present(present);
        }

        var platform = PlatformKey();
        if (platform is null)
        {
            return ChromeInstall.Failed(
                "Google publishes no Chrome build for this operating system and processor. Install "
                + "Chrome or Chromium with your package manager and set the browser path in the settings.");
        }

        var root = Root;
        if (root is null)
        {
            return ChromeInstall.Failed("ThemeForge has no tools folder to install into.");
        }

        await _oneInstallAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Somebody else may have finished while this call waited.
            present = FindInstalled();
            if (present is not null)
            {
                return ChromeInstall.Present(present);
            }

            return await DownloadAsync(platform, root, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException
                                       or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not install a browser");
            return ChromeInstall.Failed(ex.Message);
        }
        finally
        {
            _oneInstallAtATime.Release();
        }
    }

    /// <inheritdoc />
    public bool Remove()
    {
        var root = Root;
        if (root is null || !Directory.Exists(root))
        {
            return false;
        }

        try
        {
            Directory.Delete(root, recursive: true);
            _logger.LogInformation("Removed the downloaded browser from {Path}", root);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not remove the downloaded browser from {Path}", root);
            return false;
        }
    }

    /// <summary>Finds the Chrome executable inside an extracted archive.</summary>
    private static string? FindExecutable(string directory)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "chrome.exe" }
            : OperatingSystem.IsMacOS()
                ? new[] { "Google Chrome for Testing", "Google Chrome" }
                : new[] { "chrome" };

        foreach (var name in names)
        {
            var match = Directory.EnumerateFiles(directory, name, SearchOption.AllDirectories)
                .FirstOrDefault();
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    private async Task<ChromeInstall> DownloadAsync(
        string platform,
        string root,
        CancellationToken cancellationToken)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = DownloadTimeout;

        var (version, url) = await ResolveAsync(client, platform, cancellationToken).ConfigureAwait(false);
        if (version is null || url is null)
        {
            return ChromeInstall.Failed($"Chrome for Testing lists no build for '{platform}'.");
        }

        _logger.LogInformation(
            "Downloading Chrome {Version} for {Platform}. This is a one-off download of roughly "
            + "150-200 MB and can take a few minutes.",
            version,
            platform);

        var target = Path.Combine(root, version);

        // Extract beside the target and move it into place, so an interrupted install cannot leave
        // a half-extracted directory that later looks complete.
        var staging = target + ".partial";
        var archive = Path.Combine(root, $"chrome-{version}-{platform}.zip");

        Directory.CreateDirectory(root);
        Delete(staging);

        try
        {
            using (var response = await client
                       .GetStreamAsync(url, cancellationToken).ConfigureAwait(false))
            using (var file = File.Create(archive))
            {
                await response.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }

            var bytes = new FileInfo(archive).Length;
            _logger.LogInformation("Downloaded {Megabytes:F0} MB; extracting", bytes / 1024d / 1024d);

            ZipFile.ExtractToDirectory(archive, staging);

            if (FindExecutable(staging) is null)
            {
                Delete(staging);
                return ChromeInstall.Failed("The downloaded archive held no Chrome executable.");
            }

            MakeExecutable(staging);

            Delete(target);
            Directory.Move(staging, target);

            var executable = FindExecutable(target);
            if (executable is null)
            {
                return ChromeInstall.Failed("The Chrome executable went missing after installation.");
            }

            _logger.LogInformation("Installed Chrome {Version} at {Path}", version, executable);
            return ChromeInstall.Downloaded(executable, version, bytes);
        }
        finally
        {
            Delete(archive);
            Delete(staging);
        }
    }

    /// <summary>Reads the current stable version and its download URL for a platform.</summary>
    private static async Task<(string? Version, string? Url)> ResolveAsync(
        HttpClient client,
        string platform,
        CancellationToken cancellationToken)
    {
        var json = await client.GetStringAsync(VersionsUrl, cancellationToken).ConfigureAwait(false);
        return ReadDownload(json, platform);
    }

    /// <summary>
    /// Picks a platform's build out of the published version list.
    /// </summary>
    /// <param name="json">The list, as served.</param>
    /// <param name="platform">The platform key.</param>
    /// <returns>The version and its URL, either of which may be null.</returns>
    internal static (string? Version, string? Url) ReadDownload(string json, string platform)
    {
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
            || !downloads.TryGetProperty("chrome", out var builds))
        {
            return (version, null);
        }

        foreach (var build in builds.EnumerateArray())
        {
            if (build.TryGetProperty("platform", out var key)
                && string.Equals(key.GetString(), platform, StringComparison.Ordinal)
                && build.TryGetProperty("url", out var url))
            {
                return (version, url.GetString());
            }
        }

        return (version, null);
    }

    /// <summary>Restores the execute bit, which a zip archive does not carry on Unix.</summary>
    private void MakeExecutable(string directory)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var extension = Path.GetExtension(path);

            // Chrome ships helper binaries beside the main executable. Marking the extensionless
            // files and the shared objects covers them without keeping a list to go stale.
            if (extension.Length != 0
                && !extension.Equals(".so", StringComparison.OrdinalIgnoreCase)
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not make {Path} executable", path);
            }
        }
    }

    private void Delete(string path)
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete {Path}", path);
        }
    }
}
