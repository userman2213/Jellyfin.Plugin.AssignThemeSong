using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using MediaBrowser.Common.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>Makes the external tools available, downloading yt-dlp if it has to.</summary>
public interface IToolProvisioner
{
    /// <summary>
    /// Resolves every tool, provisioning yt-dlp if necessary.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resolved tool paths.</returns>
    /// <exception cref="InvalidOperationException">Thrown when yt-dlp cannot be found or provisioned.</exception>
    Task<ToolPaths> EnsureToolsAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the current yt-dlp release over the provisioned copy.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version now installed, or null if the update did not happen.</returns>
    Task<string?> UpdateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Resolves yt-dlp from configuration, then the PATH, then by downloading the current
/// release into the plugin's own data directory.
/// </summary>
/// <remarks>
/// yt-dlp is the moving part of this plugin: YouTube changes, yt-dlp adapts, and a copy that
/// cannot update itself goes stale and starts failing. Keeping a private copy under the plugin
/// data directory means ThemeForge can update it on its own schedule without needing the host
/// image rebuilt or a package manager involved.
/// </remarks>
public sealed class YtDlpProvisioner : IToolProvisioner
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProcessRunner _processRunner;
    private readonly IFfmpegLocator _ffmpegLocator;
    private readonly IThemeForgeLogger<YtDlpProvisioner> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ToolPaths? _cached;

    /// <summary>Initializes a new instance of the <see cref="YtDlpProvisioner"/> class.</summary>
    /// <param name="httpClientFactory">Used to download the yt-dlp release asset.</param>
    /// <param name="processRunner">Used to verify the binary runs.</param>
    /// <param name="ffmpegLocator">Resolves ffmpeg and ffprobe.</param>
    /// <param name="logger">Logger.</param>
    public YtDlpProvisioner(
        IHttpClientFactory httpClientFactory,
        IProcessRunner processRunner,
        IFfmpegLocator ffmpegLocator,
        IThemeForgeLogger<YtDlpProvisioner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _processRunner = processRunner;
        _ffmpegLocator = ffmpegLocator;
        _logger = logger;
    }

    /// <summary>Gets the release asset name for the current platform.</summary>
    internal static string AssetName
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                return "yt-dlp.exe";
            }

            if (OperatingSystem.IsMacOS())
            {
                return "yt-dlp_macos";
            }

            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux",
            };
        }
    }

    /// <inheritdoc />
    public async Task<ToolPaths> EnsureToolsAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null)
        {
            return _cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null)
            {
                return _cached;
            }

            var ytDlp = await ResolveYtDlpAsync(cancellationToken).ConfigureAwait(false);
            var version = await GetVersionAsync(ytDlp, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException(
                    $"yt-dlp was found at '{ytDlp}' but would not run. Check that the file is executable and not blocked.");

            version = await RefreshIfStaleAsync(ytDlp, version, cancellationToken).ConfigureAwait(false);

            _cached = new ToolPaths(ytDlp, _ffmpegLocator.ResolveFfmpeg(), _ffmpegLocator.ResolveFfprobe(), version);
            _logger.LogInformation("ThemeForge: using yt-dlp {Version} at {Path}.", version, ytDlp);
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string?> UpdateAsync(CancellationToken cancellationToken)
    {
        if (!Plugin.Config.AutoProvisionYtDlp || !string.IsNullOrWhiteSpace(Plugin.Config.YtDlpPath))
        {
            _logger.LogInformation("ThemeForge: yt-dlp is externally managed, so the update task has nothing to do.");
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var target = ProvisionedPath;
            await DownloadAsync(target, cancellationToken).ConfigureAwait(false);

            var version = await GetVersionAsync(target, cancellationToken).ConfigureAwait(false);
            if (version is null)
            {
                _logger.LogError("ThemeForge: the downloaded yt-dlp would not run; the previous copy has been replaced and may need reinstalling.");
                return null;
            }

            // Drop the cache so the next run picks up the new version string.
            _cached = null;
            _logger.LogInformation("ThemeForge: yt-dlp updated to {Version}.", version);
            return version;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Gets the path the self-provisioned binary lives at.</summary>
    private static string ProvisionedPath =>
        Path.Combine(
            Plugin.Instance?.ToolsPath ?? Path.Combine(Path.GetTempPath(), "themeforge-tools"),
            OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");

    private async Task<string> ResolveYtDlpAsync(CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;
        var configured = configuration.YtDlpPath;

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            throw new InvalidOperationException(
                $"The configured yt-dlp path '{configured}' does not exist. Correct it in the ThemeForge settings, or clear it to let the plugin provision yt-dlp itself.");
        }

        var provisioned = ProvisionedPath;

        if (configuration.AutoProvisionYtDlp)
        {
            // The plugin's own copy is preferred over one found on PATH, for two reasons. A copy
            // that came with a distribution or a container image is routinely months old, and
            // YouTube stops serving media to older yt-dlp releases — searching keeps working
            // while every download fails with HTTP 403. More importantly, the update task can
            // only replace this copy: preferring PATH meant the task downloaded a current
            // yt-dlp, reported success, and the stale one carried on being used.
            if (!File.Exists(provisioned))
            {
                _logger.LogInformation("ThemeForge: downloading {Asset} into {Path}.", AssetName, provisioned);

                try
                {
                    await DownloadAsync(provisioned, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var fallback = FindOnPath(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
                    if (fallback is null)
                    {
                        throw;
                    }

                    _logger.LogWarning(
                        ex,
                        "ThemeForge: could not download yt-dlp; falling back to {Path}. If downloads fail with HTTP 403, that copy is probably too old.",
                        fallback);
                    return fallback;
                }
            }

            return provisioned;
        }

        var onPath = FindOnPath(OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp");
        if (onPath is not null)
        {
            _logger.LogDebug("ThemeForge: using yt-dlp from the PATH at {Path}.", onPath);
            return onPath;
        }

        if (File.Exists(provisioned))
        {
            return provisioned;
        }

        throw new InvalidOperationException(
            "yt-dlp was not found and automatic provisioning is disabled. Install yt-dlp, set its path in the ThemeForge settings, or re-enable automatic provisioning.");
    }

    /// <summary>
    /// Reports whether a yt-dlp version is old enough to be a problem.
    /// </summary>
    /// <remarks>
    /// yt-dlp versions are release dates, so age is readable straight off the version string.
    /// This matters because a stale copy fails in a way that looks like something else entirely:
    /// searching and metadata keep working, and only the media download returns HTTP 403.
    /// </remarks>
    /// <param name="version">The version yt-dlp reported, for example "2026.08.19".</param>
    /// <param name="maxAgeDays">How old is acceptable.</param>
    /// <param name="now">The current date, injectable for tests.</param>
    /// <returns><c>true</c> when the release is older than <paramref name="maxAgeDays"/>.</returns>
    public static bool IsStale(string? version, int maxAgeDays, DateTime? now = null)
    {
        if (string.IsNullOrWhiteSpace(version) || maxAgeDays <= 0)
        {
            return false;
        }

        // Nightly builds carry a fourth component; only the date matters.
        var parts = version.Trim().Split('.');
        if (parts.Length < 3)
        {
            return false;
        }

        var date = string.Join('.', parts[0], parts[1], parts[2]);
        if (!DateTime.TryParseExact(date, "yyyy.MM.dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var released))
        {
            return false;
        }

        return ((now ?? DateTime.UtcNow) - released).TotalDays > maxAgeDays;
    }

    /// <summary>
    /// Replaces the managed copy when it has aged out, so a long-running server does not quietly
    /// drift into the state where every download fails.
    /// </summary>
    private async Task<string> RefreshIfStaleAsync(string path, string version, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config;

        if (!configuration.AutoProvisionYtDlp
            || !string.IsNullOrWhiteSpace(configuration.YtDlpPath)
            || !string.Equals(path, ProvisionedPath, StringComparison.Ordinal)
            || !IsStale(version, configuration.MaxYtDlpAgeDays))
        {
            return version;
        }

        _logger.LogWarning(
            "ThemeForge: yt-dlp {Version} is more than {Days} days old, which is the usual cause of downloads failing with HTTP 403. Fetching the current release.",
            version,
            configuration.MaxYtDlpAgeDays);

        try
        {
            await DownloadAsync(path, cancellationToken).ConfigureAwait(false);
            var updated = await GetVersionAsync(path, cancellationToken).ConfigureAwait(false);

            if (updated is not null)
            {
                _logger.LogInformation("ThemeForge: yt-dlp refreshed to {Version}.", updated);
                return updated;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ThemeForge: could not refresh yt-dlp; continuing with {Version}.", version);
        }

        return version;
    }

    /// <summary>
    /// Downloads the release asset to a temporary file and moves it into place only once it is
    /// complete, so an interrupted download cannot leave a truncated binary behind.
    /// </summary>
    private async Task DownloadAsync(string targetPath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = targetPath + ".download";
        var url = ReleaseBase + AssetName;

        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        client.Timeout = TimeSpan.FromMinutes(10);

        using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var destination = File.Create(temporaryPath);
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporaryPath, targetPath, overwrite: true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                targetPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    private async Task<string?> GetVersionAsync(string ytDlpPath, CancellationToken cancellationToken)
    {
        var result = await _processRunner
            .RunAsync(ytDlpPath, new[] { "--version" }, TimeSpan.FromSeconds(60), cancellationToken)
            .ConfigureAwait(false);

        return result.Success ? result.StandardOutput.Trim() : null;
    }

    /// <summary>Searches the PATH for an executable.</summary>
    private static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth failing over.
            }
        }

        return null;
    }
}
