using System;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers what a run may do for an item, and in what order.
/// </summary>
/// <remarks>
/// The requirement is "the catalogue first, then our own search". The checks that decide this
/// used to be one list answering one question, and the retry backoff sat in it, so every item
/// that had failed once was skipped outright -- catalogue included -- for twelve hours to thirty
/// days. These pin the two questions apart: what stops everything, and what stops only the search.
/// </remarks>
public class EligibilityTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static ThemeIndexEntry Entry(
        ThemeItemState state,
        DateTime? retryAt = null,
        int attempts = 0,
        int matcher = ThemeIndexEntry.CurrentMatcher,
        string? themePath = null) => new()
        {
            ItemId = Guid.NewGuid(),
            Label = "Firefly",
            State = state,
            NextRetryUtc = retryAt,
            Attempts = attempts,
            DecidedByMatcher = matcher,
            ThemePath = themePath,
        };

    private static ResolvedThemePolicy Policy(ThemeOverwritePolicy overwrite = ThemeOverwritePolicy.ReplaceOwn) =>
        new(true, overwrite, "Shows");

    private static EligibilityDecision Decide(ThemeIndexEntry entry, ThemeOverwritePolicy overwrite = ThemeOverwritePolicy.ReplaceOwn, bool foreignTheme = false) =>
        Eligibility.Decide(entry, Policy(overwrite), foreignTheme, TestData.Config(), Now);

    [Theory]
    [InlineData(ThemeItemState.Failed)]
    [InlineData(ThemeItemState.NoCandidate)]
    public void AnItemInBackoffStillGetsTheCatalogue(ThemeItemState state)
    {
        // The whole point: the backoff exists for the search, and a lookup keyed on the item's
        // own id has no business being behind it.
        var decision = Decide(Entry(state, retryAt: Now.AddDays(1)));

        Assert.True(decision.CatalogueAllowed);
        Assert.False(decision.SearchAllowed);
        Assert.Contains("backoff", decision.SearchDeferral, StringComparison.Ordinal);
    }

    [Fact]
    public void AnItemPastItsBackoffGetsBoth()
    {
        var decision = Decide(Entry(ThemeItemState.Failed, retryAt: Now.AddHours(-1)));

        Assert.True(decision.CatalogueAllowed);
        Assert.True(decision.SearchAllowed);
    }

    [Theory]
    [InlineData(ThemeItemState.Locked)]
    [InlineData(ThemeItemState.Rejected)]
    public void AHumanDecisionStopsEverything(ThemeItemState state)
    {
        var decision = Decide(Entry(state));

        Assert.False(decision.CatalogueAllowed);
        Assert.False(decision.SearchAllowed);
        Assert.Contains("marked", decision.StopReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ThemeForgesOwnThemeUnderNeverStopsEverything()
    {
        var decision = Decide(Entry(ThemeItemState.AutoAssigned, themePath: "/x/theme.mp3"), ThemeOverwritePolicy.Never);

        Assert.False(decision.CatalogueAllowed);
    }

    [Fact]
    public void ThemeForgesOwnThemeUnderReplaceOwnIsRevisited()
    {
        var decision = Decide(Entry(ThemeItemState.AutoAssigned, themePath: "/x/theme.mp3"), ThemeOverwritePolicy.ReplaceOwn);

        Assert.True(decision.CatalogueAllowed);
        Assert.True(decision.SearchAllowed);
    }

    [Fact]
    public void AForeignThemeStopsEverythingUnlessTheLibrarySaysReplaceAny()
    {
        Assert.False(Decide(Entry(ThemeItemState.Unprocessed), ThemeOverwritePolicy.ReplaceOwn, foreignTheme: true).CatalogueAllowed);
        Assert.True(Decide(Entry(ThemeItemState.Unprocessed), ThemeOverwritePolicy.ReplaceAny, foreignTheme: true).CatalogueAllowed);
    }

    [Theory]
    [InlineData(ThemeItemState.Failed)]
    [InlineData(ThemeItemState.NoCandidate)]
    [InlineData(ThemeItemState.PendingReview)]
    public void ADecisionByAnEarlierMatcherIgnoresItsBackoff(ThemeItemState state)
    {
        // The algorithm that gave up on the item no longer exists, so its opinion of when to try
        // again does not either.
        var decision = Decide(Entry(state, retryAt: Now.AddDays(20), attempts: 5, matcher: ThemeIndexEntry.CurrentMatcher - 1));

        Assert.True(decision.SearchAllowed);
        Assert.True(decision.RetriedAfterUpgrade);
    }

    [Fact]
    public void ADecisionByTheCurrentMatcherRespectsItsBackoff()
    {
        var decision = Decide(Entry(ThemeItemState.NoCandidate, retryAt: Now.AddDays(20)));

        Assert.False(decision.SearchAllowed);
        Assert.False(decision.RetriedAfterUpgrade);
    }

    [Fact]
    public void AHumanDecisionIsNeverRetriedForBeingStale()
    {
        var decision = Decide(Entry(ThemeItemState.Rejected, matcher: 0));

        Assert.False(decision.CatalogueAllowed);
        Assert.False(decision.RetriedAfterUpgrade);
    }

    [Theory]
    [InlineData(ThemeItemState.Failed)]
    [InlineData(ThemeItemState.NoCandidate)]
    public void TheAttemptLimitDefersOnlyTheSearch(ThemeItemState state)
    {
        var decision = Decide(Entry(state, attempts: TestData.Config().MaxAttempts));

        Assert.True(decision.CatalogueAllowed);
        Assert.False(decision.SearchAllowed);
        Assert.Contains("tried", decision.SearchDeferral, StringComparison.Ordinal);
    }

    [Fact]
    public void AFreshItemGetsEverything()
    {
        var decision = Decide(Entry(ThemeItemState.Unprocessed));

        Assert.True(decision.CatalogueAllowed);
        Assert.True(decision.SearchAllowed);
        Assert.Null(decision.StopReason);
        Assert.Null(decision.SearchDeferral);
    }
}
