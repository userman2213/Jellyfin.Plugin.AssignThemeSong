namespace Jellyfin.Plugin.ThemeForge.Engines.Acquisition;

/// <summary>
/// A downloaded, normalized and verified theme sitting in the staging directory,
/// ready for the placement engine to publish into the library.
/// </summary>
public sealed class AcquiredAudio
{
    /// <summary>Gets the staging path of the finished file.</summary>
    public required string StagingPath { get; init; }

    /// <summary>Gets the duration ffprobe measured on the finished file.</summary>
    public required double DurationSeconds { get; init; }

    /// <summary>Gets the integrated loudness ffmpeg measured, in LUFS, after normalization.</summary>
    public double? MeasuredLoudnessLufs { get; init; }

    /// <summary>Gets the SHA-256 of the finished file, so the index can detect external edits.</summary>
    public required string Sha256 { get; init; }

    /// <summary>Gets the size of the finished file in bytes.</summary>
    public required long SizeBytes { get; init; }

    /// <summary>Gets the source URL the audio came from.</summary>
    public required string SourceUrl { get; init; }

    /// <summary>
    /// Gets what listening to the file concluded, before it was normalised.
    /// </summary>
    /// <remarks>
    /// Recorded whatever the verdict, including when it was accepted. The measure separates music
    /// from speech by a margin of about 0.6, which will be crossed sooner or later across a few
    /// hundred titles, and having the number beside each assignment is what makes it possible to
    /// see where rather than guess.
    /// </remarks>
    public AudioAssessment? Assessment { get; init; }

    /// <summary>Gets a value indicating whether a person should hear this before it is used.</summary>
    public bool NeedsReview => Assessment?.NeedsReview ?? false;
}
