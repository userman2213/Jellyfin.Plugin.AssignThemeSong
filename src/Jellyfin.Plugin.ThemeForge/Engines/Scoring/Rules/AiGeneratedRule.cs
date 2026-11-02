using System;
using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;

namespace Jellyfin.Plugin.ThemeForge.Engines.Scoring.Rules;

/// <summary>
/// Rejects music a generator made rather than whoever wrote the work's theme.
/// </summary>
/// <remarks>
/// <para>
/// YouTube is full of uploads titled "<c>Lucky Strike – Official Theme Song</c>" that a music
/// generator produced from the title. They name the work exactly and call themselves its theme, so
/// every other rule rates them highly, and since main themes outrank everything else one was
/// assigned. They are never a theme anybody means.
/// </para>
/// <para>
/// A veto rather than a negative keyword, so it cannot be edited away and reaches every install
/// without a settings migration. Built to avoid the obvious false alarms: <c>AI</c> on its own counts
/// only in capitals and as a whole word, so the anime song "Ai no Uta", Hawaii and Daisuke pass; it
/// does not count at all when the work's own title has it, so the theme of Spielberg's
/// <c>A.I. Artificial Intelligence</c> passes; and Suno counts only as the generator ("Suno AI",
/// "Suno v4", "suno.com", "made with Suno"), because "Suno" is everyday Hindi and opens a great many
/// film songs. A description saying it is <em>not</em> AI-made does not count against it.
/// </para>
/// </remarks>
public sealed class AiGeneratedRule : IScoringRule
{
    /// <summary>"AI" or "A.I." as a word in its own right, in capitals.</summary>
    private static readonly Regex BareAi = new(
        @"(?<![\p{L}\p{N}.])(?:AI|A\.I\.?)(?![\p{L}\p{N}])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>What a generated upload says about itself, in any case.</summary>
    private static readonly Regex Phrases = new(
        @"\bai[\s-]*(?:generated|made|created|composed|song|songs|music|cover|covers|version|remix|vocals?|voice|soundtrack)\b"
        + @"|\b(?:generated|made|created|composed|produced)\s+(?:by|with|using|in)\s+(?:an?\s+)?(?:ai|a\.i\.|artificial\s+intelligence)\b"
        + @"|\bsuno(?:\s*ai\b|\s*v\d|\.com)"
        + @"|\b(?:generated|made|created|composed|produced)\s+(?:by|with|using|in)\s+(?:suno|udio)\b"
        + @"|\budio(?:\s*ai\b|\.com)"
        + @"|#(?:suno|udio|aimusic|aisong|aigenerated|aicover)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Words that, just before a phrase, mean it is being denied rather than claimed.</summary>
    private static readonly Regex Denial = new(
        @"(?:\bnot|\bno|\bnon|\bwithout|\bnever|n't|\bzero)[\s-]*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <inheritdoc />
    public string Name => "AiGenerated";

    /// <inheritdoc />
    public double WeightFrom(ScoringWeights weights) => 0;

    /// <inheritdoc />
    public bool ContributesPositively => false;

    /// <inheritdoc />
    public RuleVerdict Evaluate(Candidate candidate, ScoringContext context)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(context);

        // The work's own title may carry the letters; then they say nothing about the upload.
        var workSaysAi = BareAi.IsMatch(context.Identity.Title ?? string.Empty)
            || BareAi.IsMatch(context.Identity.OriginalTitle ?? string.Empty);

        foreach (var (place, text) in new[] { ("title", candidate.Title), ("channel", candidate.Channel) })
        {
            var said = Claim(text) ?? (workSaysAi ? null : Bare(text));
            if (said is not null)
            {
                return RuleVerdict.Veto(string.Format(CultureInfo.InvariantCulture, "its {0} says it was made by AI (\"{1}\")", place, said));
            }
        }

        // A description mentions all sorts of things, so only an unmistakable phrase counts there.
        if (Claim(candidate.Description) is { } described)
        {
            return RuleVerdict.Veto(string.Format(CultureInfo.InvariantCulture, "its description says it was made by AI (\"{0}\")", described));
        }

        return RuleVerdict.Abstain("nothing says it was made by AI");
    }

    /// <summary>Finds a phrase claiming the upload was generated, unless it is being denied.</summary>
    internal static string? Claim(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        foreach (Match match in Phrases.Matches(text))
        {
            var before = text[..match.Index];
            if (!Denial.IsMatch(before))
            {
                return match.Value;
            }
        }

        return null;
    }

    private static string? Bare(string? text) =>
        string.IsNullOrEmpty(text) || BareAi.Match(text) is not { Success: true } match ? null : match.Value;
}
