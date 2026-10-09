namespace Jellyfin.Plugin.ThemeForge.Engines.Query;

/// <summary>
/// One rung of the search ladder: the text handed to yt-dlp, plus where it sits in
/// the plan so that results from a more specific query can be preferred.
/// </summary>
/// <param name="Text">The search text, with all placeholders already substituted.</param>
/// <param name="Rank">Zero-based position in the ladder; lower is more specific.</param>
/// <param name="Template">The configured template this was built from, kept for logging.</param>
public sealed record SearchQuery(string Text, int Rank, string Template)
{
    /// <summary>
    /// Gets a small score bonus for candidates found by a more specific query. A hit on
    /// "Firefly opening theme" is better evidence than the same hit on "Firefly soundtrack".
    /// </summary>
    public double SpecificityBonus => Rank == 0 ? 1.0 : 1.0 / (1.0 + Rank);
}
