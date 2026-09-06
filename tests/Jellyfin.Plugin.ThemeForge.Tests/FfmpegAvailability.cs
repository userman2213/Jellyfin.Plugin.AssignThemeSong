using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// A fact that skips itself when ffmpeg is not installed, so the suite stays green on a machine
/// without it while still actually exercising the encoder wherever one is present.
/// </summary>
public sealed class FfmpegFactAttribute : FactAttribute
{
    public FfmpegFactAttribute()
    {
        if (FfmpegAvailability.Path is null)
        {
            Skip = "ffmpeg is not installed on this machine.";
        }
    }
}

/// <summary>Locates a system ffmpeg for the integration tests.</summary>
internal static class FfmpegAvailability
{
    public static readonly string? Path = Find("ffmpeg");
    public static readonly string? ProbePath = Find("ffprobe");

    private static string? Find(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var directory in path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = System.IO.Path.Combine(directory, executable);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>Hands the engines the system ffmpeg instead of a provisioned yt-dlp toolchain.</summary>
internal sealed class SystemToolProvisioner : IToolProvisioner
{
    public Task<ToolPaths> EnsureToolsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new ToolPaths(
            "yt-dlp",
            FfmpegAvailability.Path ?? "ffmpeg",
            FfmpegAvailability.ProbePath ?? "ffprobe",
            "test"));

    public Task<string?> UpdateAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}

internal static class TestEngines
{
    public static ProcessRunner Runner() => new(NullThemeForgeLogger<ProcessRunner>.Instance);

    public static LoudnessNormalizer Normalizer() =>
        new(new SystemToolProvisioner(), Runner(), NullThemeForgeLogger<LoudnessNormalizer>.Instance);

    public static AudioProbe Probe() =>
        new(new SystemToolProvisioner(), Runner(), NullThemeForgeLogger<AudioProbe>.Instance);
}
