namespace Jellyfin.Plugin.ThemeForge.Engines.Tooling;

/// <summary>
/// The resolved locations of the external tools ThemeForge drives.
/// </summary>
/// <param name="YtDlp">Path to the yt-dlp executable.</param>
/// <param name="Ffmpeg">Path to the ffmpeg executable.</param>
/// <param name="Ffprobe">Path to the ffprobe executable, used to verify finished files.</param>
/// <param name="YtDlpVersion">The version yt-dlp reported, for the diagnostics panel.</param>
public sealed record ToolPaths(string YtDlp, string Ffmpeg, string Ffprobe, string YtDlpVersion);
