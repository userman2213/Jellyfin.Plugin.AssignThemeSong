using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

public class TitleNormalizerTests
{
    [Theory]
    [InlineData("The Expanse", "expanse")]
    [InlineData("THE WIRE", "wire")]
    [InlineData("A Series of Unfortunate Events", "series of unfortunate events")]
    [InlineData("Amélie", "amelie")]
    [InlineData("Æon Flux", "aeon flux")]
    [InlineData("Star Wars: Episode IV", "star wars episode 4")]
    [InlineData("Rocky II", "rocky 2")]
    [InlineData("Doctor Who (2005)", "doctor who")]
    [InlineData("The Office (US)", "office")]
    [InlineData("Law & Order", "law and order")]
    [InlineData("Marvel's Agents of S.H.I.E.L.D.", "marvel s agents of s h i e l d")]
    [InlineData("  spaced   out  ", "spaced out")]
    [InlineData("I, Robot", "i robot")]
    [InlineData("Mission: Impossible III", "mission impossible 3")]
    [InlineData("Blade Runner 2049", "blade runner 2049")]
    [InlineData("Curb Your Enthusiasm", "curb your enthusiasm")]
    public void Normalize_ReducesTitlesToAComparableForm(string input, string expected) =>
        Assert.Equal(expected, TitleNormalizer.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("()")]
    public void Normalize_ReturnsEmptyWhenNothingSurvives(string? input) =>
        Assert.Equal(string.Empty, TitleNormalizer.Normalize(input));

    [Fact]
    public void Normalize_OnlyDropsALeadingArticle()
    {
        // "the" in the middle of a title is part of the name and must be kept.
        Assert.Equal("wag the dog", TitleNormalizer.Normalize("Wag the Dog"));
    }

    [Fact]
    public void Normalize_KeepsASingleWordArticleTitle()
    {
        // Dropping the article here would leave nothing to search for.
        Assert.Equal("the", TitleNormalizer.Normalize("The"));
    }

    [Fact]
    public void Normalize_DropsBracketedQualifiersFromLibraryTitles()
    {
        // On a library item the brackets hold qualifiers that would only pollute a search.
        Assert.Equal("doctor who", TitleNormalizer.Normalize("Doctor Who (2005)"));
    }

    [Fact]
    public void Tokenize_SplitsOnSpaces() =>
        Assert.Equal(new[] { "star", "trek" }, TitleNormalizer.Tokenize("star trek"));

    [Fact]
    public void Tokenize_ReturnsEmptyForEmptyInput() =>
        Assert.Empty(TitleNormalizer.Tokenize(string.Empty));
}
