using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>Finds the ffmpeg and ffprobe binaries to use for transcoding and verification.</summary>
public interface IFfmpegLocator
{
    /// <summary>Gets the ffmpeg path, preferring an explicit configuration then Jellyfin's own build.</summary>
    /// <returns>An executable path, or a bare command name to be resolved via PATH.</returns>
    string ResolveFfmpeg();

    /// <summary>Gets the ffprobe path that pairs with <see cref="ResolveFfmpeg"/>.</summary>
    /// <returns>An executable path, or a bare command name to be resolved via PATH.</returns>
    string ResolveFfprobe();
}

/// <summary>
/// Locates ffmpeg by checking, in order: the plugin configuration, the <c>JELLYFIN_FFMPEG</c>
/// environment variable that Jellyfin's own Docker images set, the well-known install locations
/// per platform, and finally the PATH.
/// </summary>
/// <remarks>
/// Jellyfin ships its own ffmpeg build, which is preferred over a distribution one because it
/// is guaranteed to have the encoders and filters Jellyfin relies on.
/// </remarks>
public sealed class FfmpegLocator : IFfmpegLocator
{
    private static readonly string[] KnownFfmpegPaths =
    {
        "/usr/lib/jellyfin-ffmpeg/ffmpeg",
        "/usr/share/jellyfin-ffmpeg/ffmpeg",
        "/usr/bin/ffmpeg",
        "/usr/local/bin/ffmpeg",
        "/opt/homebrew/bin/ffmpeg",
        @"C:\Program Files\Jellyfin\Server\ffmpeg.exe",
        @"C:\ProgramData\chocolatey\bin\ffmpeg.exe",
        @"C:\ffmpeg\bin\ffmpeg.exe",
    };

    private readonly ILogger<FfmpegLocator> _logger;

    /// <summary>Initializes a new instance of the <see cref="FfmpegLocator"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public FfmpegLocator(ILogger<FfmpegLocator> logger) => _logger = logger;

    /// <inheritdoc />
    public string ResolveFfmpeg()
    {
        var configured = Plugin.Config.FfmpegPath;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (File.Exists(configured))
            {
                return configured;
            }

            _logger.LogWarning("ThemeForge: the configured ffmpeg path {Path} does not exist; falling back to auto-detection.", configured);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        if (!string.IsNullOrEmpty(fromEnvironment) && File.Exists(fromEnvironment))
        {
            return fromEnvironment;
        }

        foreach (var candidate in KnownFfmpegPaths)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        _logger.LogDebug("ThemeForge: no ffmpeg found in the known locations; relying on PATH.");
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    /// <inheritdoc />
    public string ResolveFfprobe()
    {
        var ffmpeg = ResolveFfmpeg();

        // ffprobe always ships beside ffmpeg, so derive it rather than probing separately.
        var directory = Path.GetDirectoryName(ffmpeg);
        if (!string.IsNullOrEmpty(directory))
        {
            var probe = Path.Combine(
                directory,
                OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

            if (File.Exists(probe))
            {
                return probe;
            }
        }

        return OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe";
    }
}
