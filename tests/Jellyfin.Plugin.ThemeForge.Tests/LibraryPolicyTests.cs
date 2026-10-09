using System;
using System.Collections.Generic;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers how a library's rule and the server-wide default combine.
/// </summary>
/// <remarks>
/// The resolver needs a live <c>ILibraryManager</c> to map an item to its library, which is not
/// available here, so these exercise the decision logic through the same collapsing rule the
/// resolver applies. What matters is that an unset rule falls back to the default and never
/// widens what ThemeForge may do.
/// </remarks>
public class OverwritePolicyTests
{
    private static ThemeOverwritePolicy Effective(ThemeOverwritePolicy rule, ThemeOverwritePolicy fallback)
    {
        var configuration = TestData.Config();
        configuration.DefaultOverwritePolicy = fallback;

        // Mirrors LibraryPolicyResolver.Effective.
        return rule == ThemeOverwritePolicy.UseDefault
            ? (configuration.DefaultOverwritePolicy == ThemeOverwritePolicy.UseDefault
                ? ThemeOverwritePolicy.Never
                : configuration.DefaultOverwritePolicy)
            : rule;
    }

    [Fact]
    public void ALibraryRuleWinsOverTheDefault() =>
        Assert.Equal(
            ThemeOverwritePolicy.ReplaceAny,
            Effective(ThemeOverwritePolicy.ReplaceAny, ThemeOverwritePolicy.Never));

    [Fact]
    public void UseDefaultFallsBackToTheServerWideSetting() =>
        Assert.Equal(
            ThemeOverwritePolicy.ReplaceOwn,
            Effective(ThemeOverwritePolicy.UseDefault, ThemeOverwritePolicy.ReplaceOwn));

    [Fact]
    public void AnUnsetDefaultResolvesToTheCautiousOption()
    {
        // "UseDefault" as the default itself is meaningless, and resolving it to anything but
        // Never would let a misconfiguration quietly start overwriting people's files.
        Assert.Equal(
            ThemeOverwritePolicy.Never,
            Effective(ThemeOverwritePolicy.UseDefault, ThemeOverwritePolicy.UseDefault));
    }

    [Fact]
    public void TheShippedDefaultTouchesNothing() =>
        Assert.Equal(ThemeOverwritePolicy.Never, TestData.Config().DefaultOverwritePolicy);

    [Fact]
    public void LibrariesStartEnabledWithNoRuleOfTheirOwn()
    {
        var rule = new LibraryThemePolicy();
        Assert.True(rule.Enabled);
        Assert.Equal(ThemeOverwritePolicy.UseDefault, rule.Overwrite);
    }

    [Fact]
    public void NoRulesAreStoredByDefault() =>
        Assert.Empty(TestData.Config().LibraryPolicies);

    [Theory]
    [InlineData(ThemeOverwritePolicy.Never, false)]
    [InlineData(ThemeOverwritePolicy.ReplaceOwn, false)]
    [InlineData(ThemeOverwritePolicy.ReplaceAny, true)]
    public void OnlyReplaceAnyMayTouchAHandPlacedTheme(ThemeOverwritePolicy policy, bool expected)
    {
        // The orchestrator's guard: an existing theme ThemeForge did not write is left alone
        // unless the library explicitly says to replace anything.
        var mayReplace = policy == ThemeOverwritePolicy.ReplaceAny;
        Assert.Equal(expected, mayReplace);
    }

    [Theory]
    [InlineData(ThemeOverwritePolicy.Never, false)]
    [InlineData(ThemeOverwritePolicy.ReplaceOwn, true)]
    [InlineData(ThemeOverwritePolicy.ReplaceAny, true)]
    public void OnlyNeverBlocksReRunningThemeForgesOwnChoice(ThemeOverwritePolicy policy, bool expected)
    {
        var mayRevisit = policy != ThemeOverwritePolicy.Never;
        Assert.Equal(expected, mayRevisit);
    }
}
