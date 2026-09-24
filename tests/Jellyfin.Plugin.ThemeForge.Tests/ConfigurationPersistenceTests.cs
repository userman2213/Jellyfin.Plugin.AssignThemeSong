using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Serialization;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers the settings surviving the trip to disk and back.
/// </summary>
/// <remarks>
/// Jellyfin stores plugin configuration with <see cref="XmlSerializer"/>, which silently ignores
/// anything it cannot handle: a property with no setter, a non-public type, an interface, a
/// dictionary. The result is a setting that appears to save, works until the server restarts, and
/// then reverts — which is what "the rule doesn't stay saved" looks like from the outside. The
/// serializer is exercised here directly so that a property added later cannot introduce that
/// silently.
/// </remarks>
public class ConfigurationPersistenceTests
{
    private static PluginConfiguration RoundTrip(PluginConfiguration configuration)
    {
        var serializer = new XmlSerializer(typeof(PluginConfiguration));

        using var buffer = new MemoryStream();
        serializer.Serialize(buffer, configuration);
        buffer.Position = 0;

        return (PluginConfiguration)serializer.Deserialize(buffer)!;
    }

    [Fact]
    public void EverySettingIsSomethingXmlSerializerCanStore()
    {
        // XmlSerializer requires a public settable property of a serializable type. Anything else
        // is dropped without an error, so this asserts the shape rather than trusting it.
        var unstorable = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.DeclaringType == typeof(PluginConfiguration))
            .Where(property => !property.CanWrite || !property.SetMethod!.IsPublic)
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(unstorable);
    }

    [Fact]
    public void ALibraryRuleSurvivesBeingSaved()
    {
        var id = Guid.NewGuid();
        var configuration = TestData.Config();
        configuration.DefaultOverwritePolicy = ThemeOverwritePolicy.Never;
        configuration.LibraryPolicies = new LibraryThemePolicy[]
        {
            new()
            {
                LibraryId = LibraryPolicyResolver.CanonicalId(id),
                LibraryName = "Shows",
                Enabled = true,
                Overwrite = ThemeOverwritePolicy.ReplaceAny,
            },
        };

        var stored = RoundTrip(configuration).LibraryPolicies.Single();

        Assert.Equal(LibraryPolicyResolver.CanonicalId(id), stored.LibraryId);
        Assert.Equal("Shows", stored.LibraryName);
        Assert.True(stored.Enabled);
        Assert.Equal(ThemeOverwritePolicy.ReplaceAny, stored.Overwrite);
    }

    [Fact]
    public void ASavedRuleStillResolvesAfterTheRoundTrip()
    {
        // The end-to-end property that matters: what comes back off disk still matches the
        // library it was set for. Storing it correctly is not enough if the id no longer matches.
        var id = Guid.NewGuid();
        var configuration = TestData.Config();
        configuration.LibraryPolicies = new LibraryThemePolicy[]
        {
            new()
            {
                LibraryId = LibraryPolicyResolver.CanonicalId(id),
                LibraryName = "Shows",
                Enabled = true,
                Overwrite = ThemeOverwritePolicy.ReplaceAny,
            },
        };

        var libraries = new[] { new LibraryFolderRef(id, "Shows", "tvshows") };
        var audit = LibraryPolicyResolver.Audit(libraries, RoundTrip(configuration));

        Assert.Equal(ThemeOverwritePolicy.ReplaceAny, audit.Libraries.Single().EffectiveOverwrite);
        Assert.Empty(audit.Problems);
    }

    [Fact]
    public void TheLogLevelSurvivesBeingSaved()
    {
        // An enum from another assembly is the kind of property that round-trips in JSON and
        // fails in XML, which would only show up after a restart.
        var configuration = TestData.Config();
        configuration.FileLogLevel = LogLevel.Debug;

        Assert.Equal(LogLevel.Debug, RoundTrip(configuration).FileLogLevel);
    }

    [Fact]
    public void TheKeywordListsSurviveBeingSaved()
    {
        var configuration = TestData.Config();
        configuration.NegativeKeywords = new[] { "reaction", "cover" };
        configuration.PreferredChannels = Array.Empty<string>();

        var stored = RoundTrip(configuration);

        Assert.Equal(new[] { "reaction", "cover" }, stored.NegativeKeywords);
        Assert.Empty(stored.PreferredChannels);
    }

    [Fact]
    public void TheScoringWeightsSurviveBeingSaved()
    {
        var configuration = TestData.Config();
        configuration.Weights.TitleSimilarity = 41.5;

        Assert.Equal(41.5, RoundTrip(configuration).Weights.TitleSimilarity);
    }

    [Fact]
    public void AShippedDefaultCanBeRemovedAndStaysRemoved()
    {
        // The list form of these properties made this impossible: XmlSerializer appended the
        // saved values to the defaults, so a keyword you deleted came back on the next restart
        // and there was no way to tell from the settings page that it had.
        var configuration = TestData.Config();
        Assert.Contains("scene", configuration.NegativeKeywords);

        configuration.NegativeKeywords = configuration.NegativeKeywords
            .Where(keyword => keyword != "scene")
            .ToArray();

        Assert.DoesNotContain("scene", RoundTrip(configuration).NegativeKeywords);
    }

    [Fact]
    public void ASettingListDoesNotGrowWhenSavedRepeatedly()
    {
        // Saving concatenated the stored list onto the defaults every time, so the file grew by
        // the full default set on each restart. Three round trips is enough to catch that.
        var configuration = TestData.Config();
        var original = configuration.SeriesQueryTemplates.Length;

        for (var i = 0; i < 3; i++)
        {
            configuration = RoundTrip(configuration);
        }

        Assert.Equal(original, configuration.SeriesQueryTemplates.Length);
        Assert.Equal(TestData.Config().SeriesQueryTemplates, configuration.SeriesQueryTemplates);
    }

    [Fact]
    public void AListEmptiedOnPurposeStaysEmpty()
    {
        var configuration = TestData.Config();
        configuration.PositiveKeywords = Array.Empty<string>();

        Assert.Empty(RoundTrip(configuration).PositiveKeywords);
    }
}
