using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers how a stored rule is matched to a library.
/// </summary>
/// <remarks>
/// <para>
/// The reported failure was "I set the rule that it should overwrite but somehow it does not get
/// applied, and the rule doesn't stay saved". An earlier version listed libraries by
/// <c>VirtualFolderInfo.ItemId</c> and matched items by <c>CollectionFolder.Id</c>. Nothing in
/// Jellyfin's API says those are the same value, and <c>ItemId</c> can be null when the virtual
/// folder's path does not match a collection folder exactly — in which case the settings page
/// saves a rule keyed on an empty string, which is then dropped, and displays it anyway because
/// both the list and the form read the same field.
/// </para>
/// <para>
/// The fix is that one id source exists. These tests pin the properties that failure violated:
/// the id a rule is saved against is the id an item resolves to, an id form saved by an older
/// version still matches, and an id Jellyfin did not report never silently matches anything.
/// </para>
/// </remarks>
public class LibraryIdentityTests
{
    private static readonly Guid Shows = Guid.Parse("1111abcd-2222-3333-4444-55555555ffff");
    private static readonly Guid Films = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

    private static IReadOnlyList<LibraryFolderRef> TwoLibraries() => new[]
    {
        new LibraryFolderRef(Shows, "Shows", "tvshows"),
        new LibraryFolderRef(Films, "Films", "movies"),
    };

    private static PluginConfiguration WithRule(string libraryId, ThemeOverwritePolicy overwrite, bool enabled = true)
    {
        var configuration = TestData.Config();
        configuration.LibraryPolicies = new LibraryThemePolicy[]
        {
            new() { LibraryId = libraryId, LibraryName = "Shows", Enabled = enabled, Overwrite = overwrite },
        };
        return configuration;
    }

    [Fact]
    public void TheIdShownOnTheSettingsPageIsTheIdItemsResolveTo()
    {
        // This is the whole point of the single source: whatever ListLibraries hands the browser
        // is what a rule comes back keyed on, and it must match the collection folder's own id.
        var listed = LibraryPolicyResolver.Summarise(TwoLibraries(), TestData.Config())
            .Single(library => library.Name == "Shows");

        Assert.Equal(Shows, LibraryPolicyResolver.ParseId(listed.Id));
    }

    [Theory]
    [InlineData("1111abcd22223333444455555555ffff")]         // the canonical "N" form
    [InlineData("1111abcd-2222-3333-4444-55555555ffff")]      // the dashed form an older build stored
    [InlineData("1111ABCD-2222-3333-4444-55555555FFFF")]      // dashes and upper case
    [InlineData("{1111abcd-2222-3333-4444-55555555ffff}")]
    public void ARuleStoredInAnyIdFormStillMatches(string storedId)
    {
        var configuration = WithRule(storedId, ThemeOverwritePolicy.ReplaceAny);

        var shows = LibraryPolicyResolver.Audit(TwoLibraries(), configuration)
            .Libraries.Single(row => row.Name == "Shows");

        Assert.True(shows.HasStoredRule);
        Assert.Equal(ThemeOverwritePolicy.ReplaceAny, shows.EffectiveOverwrite);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-guid")]
    public void AnIdJellyfinNeverReportedMatchesNothing(string storedId)
    {
        // An empty id must not become a wildcard. Matching it against a library whose id also
        // failed to parse would apply one library's rule to every library on the server.
        var configuration = WithRule(storedId, ThemeOverwritePolicy.ReplaceAny);
        var libraries = new[] { new LibraryFolderRef(Guid.Empty, "Broken", "tvshows") };

        var row = LibraryPolicyResolver.Audit(libraries, configuration).Libraries.Single();

        Assert.False(row.HasStoredRule);
        Assert.Equal(ThemeOverwritePolicy.Never, row.EffectiveOverwrite);
    }

    [Fact]
    public void ARuleThatMatchesNoLibraryIsReportedRatherThanIgnored()
    {
        // The failure that made this invisible: the rule is stored, the page shows it, and the
        // engine never sees it. The audit has to say so out loud.
        var configuration = WithRule(Guid.NewGuid().ToString("N"), ThemeOverwritePolicy.ReplaceAny);

        var audit = LibraryPolicyResolver.Audit(TwoLibraries(), configuration);

        Assert.Single(audit.OrphanedRules);
        Assert.Contains(audit.Problems, problem => problem.Contains("does nothing", StringComparison.Ordinal));
        Assert.All(audit.Libraries, row => Assert.False(row.HasStoredRule));
    }

    [Fact]
    public void ARuleAppliesOnlyToItsOwnLibrary()
    {
        var configuration = WithRule(LibraryPolicyResolver.CanonicalId(Shows), ThemeOverwritePolicy.ReplaceAny);

        var audit = LibraryPolicyResolver.Audit(TwoLibraries(), configuration);

        Assert.Equal(ThemeOverwritePolicy.ReplaceAny, audit.Libraries.Single(r => r.Name == "Shows").EffectiveOverwrite);
        Assert.Equal(ThemeOverwritePolicy.Never, audit.Libraries.Single(r => r.Name == "Films").EffectiveOverwrite);
        Assert.Empty(audit.OrphanedRules);
        Assert.Empty(audit.Problems);
    }

    [Fact]
    public void TheAuditReportsWhatWillBeUsedNotWhatWasTyped()
    {
        // A library left on "use the default" must report the default it will actually get, not
        // the placeholder. Showing UseDefault here is what let "I set it to replace" and "nothing
        // was replaced" both look correct at the same time.
        var configuration = TestData.Config();
        configuration.DefaultOverwritePolicy = ThemeOverwritePolicy.ReplaceOwn;

        var row = LibraryPolicyResolver.Audit(TwoLibraries(), configuration).Libraries.First();

        Assert.Equal(ThemeOverwritePolicy.UseDefault, row.StoredOverwrite);
        Assert.Equal(ThemeOverwritePolicy.ReplaceOwn, row.EffectiveOverwrite);
    }

    [Fact]
    public void ADisabledLibraryIsReportedAsDisabled()
    {
        var configuration = WithRule(
            LibraryPolicyResolver.CanonicalId(Shows),
            ThemeOverwritePolicy.UseDefault,
            enabled: false);

        var shows = LibraryPolicyResolver.Audit(TwoLibraries(), configuration)
            .Libraries.Single(row => row.Name == "Shows");

        Assert.True(shows.HasStoredRule);
        Assert.False(shows.Enabled);
    }

    [Fact]
    public void CanonicalIdIsTheFormRulesAreStoredIn() =>
        Assert.Equal("1111abcd22223333444455555555ffff", LibraryPolicyResolver.CanonicalId(Shows));

    [Fact]
    public void NoLibrariesIsOnlyAProblemWhenRulesExist()
    {
        Assert.Empty(LibraryPolicyResolver.Audit(Array.Empty<LibraryFolderRef>(), TestData.Config()).Problems);

        var configuration = WithRule(LibraryPolicyResolver.CanonicalId(Shows), ThemeOverwritePolicy.ReplaceAny);
        Assert.NotEmpty(LibraryPolicyResolver.Audit(Array.Empty<LibraryFolderRef>(), configuration).Problems);
    }
}
