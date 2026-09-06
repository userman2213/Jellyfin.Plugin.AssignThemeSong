using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class TextSimilarityTests
{
    [Fact]
    public void TitleMatch_IsPerfectWhenTheTitleAppearsVerbatim()
    {
        // The usual shape of a real theme upload: the show name plus descriptive noise.
        var score = TextSimilarity.TitleMatch(
            TitleNormalizer.Normalize("Battlestar Galactica"),
            TitleNormalizer.Normalize("Battlestar Galactica - Main Title Theme (HD)"));

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void TitleMatch_IsZeroForAnUnrelatedTitle()
    {
        var score = TextSimilarity.TitleMatch(
            TitleNormalizer.Normalize("Firefly"),
            TitleNormalizer.Normalize("Top 10 Cooking Fails"));

        Assert.True(score < 0.35, $"expected a low score, got {score}");
    }

    [Fact]
    public void TitleMatch_FallsWhenOnlyPartOfAMultiWordTitleIsPresent()
    {
        var full = TextSimilarity.TitleMatch(
            TitleNormalizer.Normalize("Star Trek Voyager"),
            TitleNormalizer.Normalize("Star Trek Voyager opening"));

        var partial = TextSimilarity.TitleMatch(
            TitleNormalizer.Normalize("Star Trek Voyager"),
            TitleNormalizer.Normalize("Star Trek opening"));

        Assert.Equal(1.0, full);
        Assert.True(partial < full, "a partial match must score lower than a complete one");
    }

    [Fact]
    public void TitleMatch_ToleratesASmallSpellingDifference()
    {
        var score = TextSimilarity.TitleMatch(
            TitleNormalizer.Normalize("Battlestar Galactica"),
            TitleNormalizer.Normalize("Battlestar Galactika theme"));

        Assert.True(score >= 0.9, $"a one-letter difference should still match, got {score}");
    }

    [Fact]
    public void JaroWinkler_ScoresIdenticalStringsAsOne() =>
        Assert.Equal(1.0, TextSimilarity.JaroWinkler("firefly", "firefly"));

    [Fact]
    public void JaroWinkler_RewardsASharedPrefix()
    {
        var shared = TextSimilarity.JaroWinkler("firefly", "firefloy");
        var unshared = TextSimilarity.JaroWinkler("firefly", "xirefly");
        Assert.True(shared > unshared);
    }

    [Fact]
    public void Coverage_IsZeroWhenEitherSideIsEmpty()
    {
        Assert.Equal(0, TextSimilarity.Coverage(new[] { "a" }, System.Array.Empty<string>()));
        Assert.Equal(0, TextSimilarity.Coverage(System.Array.Empty<string>(), new[] { "a" }));
    }
}
