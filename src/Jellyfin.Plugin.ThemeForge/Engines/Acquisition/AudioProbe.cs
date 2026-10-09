using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>What ffprobe found in an audio file.</summary>
/// <param name="IsValid">Whether the file has a readable audio stream.</param>
/// <param name="DurationSeconds">Duration in seconds, zero when unknown.</param>
/// <param name="CodecName">The audio codec, when known.</param>
/// <param name="HasVideoStream">Whether a video stream is present — cover art counts.</param>
/// <param name="Problem">Why the file is unusable, when <paramref name="IsValid"/> is false.</param>
public sealed record AudioProbeResult(
    bool IsValid,
    double DurationSeconds,
    string? CodecName,
    bool HasVideoStream,
    string? Problem);

/// <summary>Inspects audio files.</summary>
public interface IAudioProbe
{
    /// <summary>Reads a file's streams and duration.</summary>
    /// <param name="path">File to inspect.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found.</returns>
    Task<AudioProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}

/// <summary>
/// Reads audio file properties with ffprobe.
/// </summary>
/// <remarks>
/// This is the verification gate that keeps broken files out of the library. A transcode can
/// fail in ways that still produce a file — a zero-length write, a container with no frame
/// header — and Jellyfin's own metadata scan then throws on it and the theme silently never
/// plays, differently on different clients. Checking here means a bad encode is a failed item
/// with a logged reason instead of a mystery.
/// </remarks>
public sealed class AudioProbe : IAudioProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly IThemeForgeLogger<AudioProbe> _logger;

    /// <summary>Initializes a new instance of the <see cref="AudioProbe"/> class.</summary>
    /// <param name="toolProvisioner">Supplies the ffprobe path.</param>
    /// <param name="processRunner">Runs ffprobe.</param>
    /// <param name="logger">Logger.</param>
    public AudioProbe(IToolProvisioner toolProvisioner, IProcessRunner processRunner, IThemeForgeLogger<AudioProbe> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<AudioProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        if (!System.IO.File.Exists(path))
        {
            return new AudioProbeResult(false, 0, null, false, "the file does not exist");
        }

        if (new System.IO.FileInfo(path).Length == 0)
        {
            return new AudioProbeResult(false, 0, null, false, "the file is empty");
        }

        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);

        var result = await _processRunner.RunAsync(
            tools.Ffprobe,
            new[]
            {
                "-v", "error",
                "-show_entries", "format=duration",
                "-show_entries", "stream=codec_type,codec_name",
                "-of", "json",
                path,
            },
            Timeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new AudioProbeResult(false, 0, null, false, $"ffprobe could not read the file ({result.ErrorSummary})");
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;

            var duration = 0.0;
            if (root.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationElement)
                && double.TryParse(durationElement.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            string? audioCodec = null;
            var hasVideo = false;

            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (string.Equals(type, "audio", StringComparison.Ordinal))
                    {
                        audioCodec ??= stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                    }
                    else if (string.Equals(type, "video", StringComparison.Ordinal))
                    {
                        hasVideo = true;
                    }
                }
            }

            if (audioCodec is null)
            {
                return new AudioProbeResult(false, duration, null, hasVideo, "the file has no audio stream");
            }

            if (duration <= 0)
            {
                return new AudioProbeResult(false, duration, audioCodec, hasVideo, "the file has no readable duration");
            }

            return new AudioProbeResult(true, duration, audioCodec, hasVideo, null);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not parse ffprobe output for {Path}.", path);
            return new AudioProbeResult(false, 0, null, false, "ffprobe returned output that could not be parsed");
        }
    }
}
