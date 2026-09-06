using System;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the string measures themselves.
/// </summary>
/// <remarks>
/// These say how alike two strings are and nothing else. Whether a candidate names a particular
/// work is decided by <c>TitleAnchor</c> and covered separately — the two used to be conflated,
/// and that is what let "Girls" match "The Golden Girls" at full marks.
/// </remarks>
public class TextSimilarityTests
{
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

    [Theory]
    [InlineData("castle", "castlevania")]
    [InlineData("dark", "darkside")]
    [InlineData("you", "young")]
    public void JaroWinkler_RatesADifferentWordAsNearlyIdentical(string first, string second)
    {
        // Kept as a test because it is the reason similarity alone cannot decide identity: each
        // of these pairs names two unrelated works and scores above 0.90.
        Assert.True(
            TextSimilarity.JaroWinkler(first, second) >= 0.90,
            $"{first}/{second} scored {TextSimilarity.JaroWinkler(first, second):0.000}");
    }

    [Fact]
    public void Coverage_CountsTheWantedWordsThatArePresent()
    {
        Assert.Equal(1.0, TextSimilarity.Coverage(new[] { "star", "trek" }, new[] { "star", "trek", "theme" }));
        Assert.Equal(0.5, TextSimilarity.Coverage(new[] { "star", "trek" }, new[] { "star", "wars" }));
    }

    [Fact]
    public void Coverage_IsZeroWhenEitherSideIsEmpty()
    {
        Assert.Equal(0, TextSimilarity.Coverage(new[] { "a" }, Array.Empty<string>()));
        Assert.Equal(0, TextSimilarity.Coverage(Array.Empty<string>(), new[] { "a" }));
    }
}
