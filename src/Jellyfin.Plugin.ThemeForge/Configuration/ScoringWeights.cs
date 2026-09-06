namespace Jellyfin.Plugin.ThemeForge.Configuration;

/// <summary>
/// How much each scoring rule counts. Exposed as configuration rather than constants so a
/// user whose library scores badly can tune it without waiting for a release.
/// </summary>
public class ScoringWeights
{
    /// <summary>Gets or sets the weight for how closely the candidate title matches the media title.</summary>
    public double TitleSimilarity { get; set; } = 40;

    /// <summary>Gets or sets the weight for theme-indicating words such as "opening" or "main title".</summary>
    public double KeywordAffinity { get; set; } = 18;

    /// <summary>Gets or sets the weight for disqualifying words such as "reaction" or "cover".</summary>
    public double NegativeKeywords { get; set; } = 30;

    /// <summary>Gets or sets the weight for the candidate having a plausible theme-song length.</summary>
    public double DurationPlausibility { get; set; } = 22;

    /// <summary>Gets or sets the weight for the uploading channel's reputation.</summary>
    public double ChannelReputation { get; set; } = 12;

    /// <summary>Gets or sets the weight for view count. Deliberately small: popularity is weak evidence of correctness.</summary>
    public double Popularity { get; set; } = 6;

    /// <summary>Gets or sets the weight for the upload date being plausible relative to the release year.</summary>
    public double Recency { get; set; } = 3;

    /// <summary>Gets or sets the weight for the penalty applied when a video is already assigned elsewhere.</summary>
    public double Duplicate { get; set; } = 15;

    /// <summary>Gets or sets the weight for the bonus given to hits from a more specific query.</summary>
    public double QuerySpecificity { get; set; } = 8;
}
