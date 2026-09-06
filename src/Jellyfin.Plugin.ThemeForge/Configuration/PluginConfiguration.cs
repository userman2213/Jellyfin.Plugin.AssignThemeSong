using System.Collections.Generic;
using MediaBrowser.Model.Plugins;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// ThemeForge settings. Serialized by Jellyfin with <see cref="System.Xml.Serialization.XmlSerializer"/>,
/// so every member here must be a public settable property of a simple type or a list thereof.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ---- Tooling -------------------------------------------------------------------

    /// <summary>Gets or sets an explicit yt-dlp path. Empty means probe the PATH, then self-provision.</summary>
    public string YtDlpPath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether yt-dlp may be downloaded and kept up to date automatically.</summary>
    public bool AutoProvisionYtDlp { get; set; } = true;

    /// <summary>Gets or sets an explicit ffmpeg path. Empty means auto-detect, preferring Jellyfin's own build.</summary>
    public string FfmpegPath { get; set; } = string.Empty;

    // ---- Discovery -----------------------------------------------------------------

    /// <summary>Gets or sets how many results to pull per search query.</summary>
    public int SearchResultsPerQuery { get; set; } = 8;

    /// <summary>Gets or sets how many of the best results get a full metadata fetch. Hydration is the expensive part.</summary>
    public int HydrateTopCandidates { get; set; } = 5;

    /// <summary>Gets or sets the search ladder for series, most specific first. <c>{title}</c> and <c>{year}</c> are substituted.</summary>
    public List<string> SeriesQueryTemplates { get; set; } = new()
    {
        "{title} opening theme song",
        "{title} main title theme",
        "{title} theme song",
        "{title} intro",
        "{title} soundtrack main theme",
    };

    /// <summary>Gets or sets the search ladder for films, most specific first.</summary>
    public List<string> MovieQueryTemplates { get; set; } = new()
    {
        "{title} {year} main theme soundtrack",
        "{title} main title theme",
        "{title} theme song",
        "{title} soundtrack suite",
    };

    // ---- Decision ------------------------------------------------------------------

    /// <summary>Gets or sets the score at or above which a candidate is assigned without asking.</summary>
    public double AutoAssignThreshold { get; set; } = 72;

    /// <summary>Gets or sets the score at or above which a candidate is offered for review.</summary>
    public double ReviewThreshold { get; set; } = 45;

    /// <summary>Gets or sets a value indicating whether the ladder stops early once a candidate clears <see cref="AutoAssignThreshold"/>.</summary>
    public bool StopLadderOnConfidentHit { get; set; } = true;

    // ---- Scoring -------------------------------------------------------------------

    /// <summary>Gets or sets the per-rule weights.</summary>
    public ScoringWeights Weights { get; set; } = new();

    /// <summary>
    /// Gets or sets the title match below which a candidate is disqualified outright.
    /// Assigning a well-produced theme from the wrong show is worse than assigning nothing,
    /// because it lands in the library and nobody notices.
    /// </summary>
    public double MinimumTitleSimilarity { get; set; } = 0.34;

    /// <summary>Gets or sets words that suggest a genuine theme.</summary>
    public List<string> PositiveKeywords { get; set; } = new()
    {
        "theme", "opening", "main title", "intro", "ost", "soundtrack",
        "title sequence", "end credits", "titles", "generique",
    };

    /// <summary>Gets or sets words that suggest the candidate is not a theme. These carry the heaviest negative weight.</summary>
    public List<string> NegativeKeywords { get; set; } = new()
    {
        "reaction", "cover", "remix", "tutorial", "lesson", "how to play",
        "1 hour", "10 hours", "hour loop", "loop", "extended", "amv",
        "trailer", "review", "explained", "recap", "full episode", "episode",
        "karaoke", "sheet music", "guitar", "piano tutorial", "8 bit", "8-bit",
        "nightcore", "slowed", "reverb", "fan made", "fanmade", "parody",
        "behind the scenes", "interview", "compilation", "every",
        "joke", "bloopers", "deleted scene", "best of", "funniest", "scene",
    };

    /// <summary>Gets or sets channel names or ids that are trusted sources of themes.</summary>
    public List<string> PreferredChannels { get; set; } = new();

    /// <summary>Gets or sets channel names or ids that are never acceptable.</summary>
    public List<string> BlockedChannels { get; set; } = new();

    /// <summary>Gets or sets video ids a human has rejected globally; they are never offered again.</summary>
    public List<string> BlockedVideoIds { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether YouTube's auto-generated "- Topic" music channels get a reputation bonus.</summary>
    public bool TrustTopicChannels { get; set; } = true;

    // ---- Plausible durations -------------------------------------------------------

    /// <summary>Gets or sets the shortest length a series theme is expected to be.</summary>
    public int SeriesIdealMinSeconds { get; set; } = 30;

    /// <summary>Gets or sets the longest length a series theme is expected to be.</summary>
    public int SeriesIdealMaxSeconds { get; set; } = 150;

    /// <summary>Gets or sets the shortest length a film theme is expected to be.</summary>
    public int MovieIdealMinSeconds { get; set; } = 60;

    /// <summary>Gets or sets the longest length a film theme is expected to be.</summary>
    public int MovieIdealMaxSeconds { get; set; } = 300;

    /// <summary>Gets or sets the length below which a candidate is disqualified outright.</summary>
    public int HardMinSeconds { get; set; } = 10;

    /// <summary>Gets or sets the length above which a candidate is disqualified outright.</summary>
    public int HardMaxSeconds { get; set; } = 600;

    // ---- Acquisition and audio -----------------------------------------------------

    /// <summary>Gets or sets the MP3 bitrate in kbit/s.</summary>
    public int AudioBitrate { get; set; } = 192;

    /// <summary>
    /// Gets or sets a value indicating whether every theme is normalized to a common loudness.
    /// This is what makes themes play at a consistent volume; Jellyfin has no theme volume
    /// control of its own, so without it loudness is whatever the uploader mastered.
    /// </summary>
    public bool EnableLoudnessNormalization { get; set; } = true;

    /// <summary>Gets or sets the integrated loudness target in LUFS (EBU R128 default is -23).</summary>
    public double TargetLoudnessLufs { get; set; } = -23;

    /// <summary>Gets or sets the true-peak ceiling in dBTP.</summary>
    public double TargetTruePeakDb { get; set; } = -1.5;

    /// <summary>Gets or sets the target loudness range.</summary>
    public double TargetLoudnessRange { get; set; } = 11;

    /// <summary>Gets or sets a hard length cap in seconds, applied with a fade. Zero keeps the full theme.</summary>
    public int MaxThemeSeconds { get; set; }

    /// <summary>Gets or sets the fade-in length in seconds, which stops themes starting abruptly.</summary>
    public double FadeInSeconds { get; set; } = 0.5;

    /// <summary>Gets or sets the fade-out length in seconds.</summary>
    public double FadeOutSeconds { get; set; } = 3;

    // ---- Placement -----------------------------------------------------------------

    /// <summary>
    /// Gets or sets what ThemeForge may do about an existing theme, for libraries with no rule
    /// of their own.
    /// </summary>
    public ThemeOverwritePolicy DefaultOverwritePolicy { get; set; } = ThemeOverwritePolicy.Never;

    /// <summary>
    /// Gets or sets per-library overrides.
    /// </summary>
    /// <remarks>
    /// Overwrite behaviour differs by library in practice: a shows library is usually worth
    /// re-running as scoring improves, while a curated film library may hold themes chosen by
    /// hand that should never be touched. One server-wide switch forces the more cautious
    /// setting onto both.
    /// </remarks>
    public List<LibraryThemePolicy> LibraryPolicies { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether a pre-existing theme is backed up before being replaced.</summary>
    public bool BackupExistingThemes { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether a film must live in its own folder before a theme is written.
    /// In a flat library every film shares one directory, so writing theme.mp3 there would give
    /// every film the same theme.
    /// </summary>
    public bool RequireDedicatedFolder { get; set; } = true;

    // ---- Orchestration -------------------------------------------------------------

    /// <summary>Gets or sets how many items are processed at once. Kept low to avoid rate limiting.</summary>
    public int MaxConcurrency { get; set; } = 2;

    /// <summary>Gets or sets the minimum delay between outbound requests, in milliseconds.</summary>
    public int RequestDelayMs { get; set; } = 1500;

    /// <summary>Gets or sets a value indicating whether runs only score and report, writing nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Gets or sets how many times a failing item is retried before it is left alone.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Gets or sets a value indicating whether films are processed.</summary>
    public bool ProcessMovies { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether series are processed.</summary>
    public bool ProcessSeries { get; set; } = true;

    // ---- Logging -------------------------------------------------------------------

    /// <summary>
    /// Gets or sets a value indicating whether ThemeForge keeps its own log file.
    /// </summary>
    /// <remarks>
    /// A run over a large library produces thousands of lines that only make sense together.
    /// Everything written to the file is also written to Jellyfin's log, so turning this off
    /// hides nothing — it only removes the separate view.
    /// </remarks>
    public bool EnableFileLogging { get; set; } = true;

    /// <summary>
    /// Gets or sets the lowest level written to ThemeForge's log file, independent of Jellyfin's
    /// own level, so a run can be traced in detail without making the server log verbose.
    /// </summary>
    public LogLevel FileLogLevel { get; set; } = LogLevel.Information;

    /// <summary>Gets or sets the size at which the log file is rotated, in megabytes.</summary>
    public int MaxLogFileSizeMb { get; set; } = 5;

    /// <summary>Gets or sets how many rotated log files are kept.</summary>
    public int MaxLogFiles { get; set; } = 3;

    /// <summary>
    /// Creates a copy that can be adjusted for a single operation without disturbing the saved
    /// settings — used when an explicit user action needs to override one option, such as an
    /// approval that must be allowed to replace an existing theme.
    /// </summary>
    /// <returns>A copy of these settings.</returns>
    public PluginConfiguration ShallowCopy() => (PluginConfiguration)MemberwiseClone();
}
