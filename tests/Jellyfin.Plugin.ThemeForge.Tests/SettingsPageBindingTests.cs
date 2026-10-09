using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins that every setting with a control on the page is a setting the page can actually save.
/// </summary>
/// <remarks>
/// <para>
/// The page saves by walking four arrays of setting names. A setting added to the configuration
/// class and given a control on the form, but left out of the array, renders, accepts a value and
/// reports "settings saved" — then silently discards it and shows the default again on the next
/// load. There is no error and no warning; the control is right there on the page looking like it
/// works.
/// </para>
/// <para>
/// That trap has caught this plugin twice, which is why this is a test rather than a note. It
/// reads the page out of the built plugin assembly, so it checks the file that actually ships.
/// </para>
/// <para>
/// The reverse — a setting with no control at all — is deliberate for the handful of expert
/// thresholds that are edited by hand, so it is not checked here.
/// </para>
/// </remarks>
public class SettingsPageBindingTests
{
    private static readonly string[] Arrays = { "CHECKBOXES", "NUMBERS", "TEXTS", "LISTS" };

    private static string Page()
    {
        var assembly = typeof(Plugin).Assembly;
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.EndsWith("configPage.html", StringComparison.Ordinal));

        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static IReadOnlyList<string> Bound(string page, string array)
    {
        var match = Regex.Match(page, @"var\s+" + array + @"\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline);
        Assert.True(match.Success, $"The settings page no longer declares a {array} array.");

        return Regex.Matches(match.Groups["body"].Value, @"'(?<name>[^']+)'")
            .Select(entry => entry.Groups["name"].Value)
            .ToList();
    }

    /// <summary>Which array a setting of this type has to be named in, or null if it is not a plain field.</summary>
    private static string? ArrayFor(Type type) => type switch
    {
        _ when type == typeof(bool) => "CHECKBOXES",
        _ when type == typeof(int) || type == typeof(double) || type.IsEnum => "NUMBERS",
        _ when type == typeof(string) => "TEXTS",
        _ when type == typeof(string[]) => "LISTS",
        _ => null,
    };

    /// <summary>Every setting the page puts a control on the form for.</summary>
    public static TheoryData<string, string> EverySettingWithAControl()
    {
        var page = Page();
        var onTheForm = Regex.Matches(page, @"id=""(?<id>[A-Za-z][A-Za-z0-9]*)""")
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        var data = new TheoryData<string, string>();

        foreach (var property in typeof(PluginConfiguration).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.CanWrite && onTheForm.Contains(property.Name) && ArrayFor(property.PropertyType) is { } array)
            {
                data.Add(property.Name, array);
            }
        }

        Assert.NotEmpty(data);
        return data;
    }

    [Theory]
    [MemberData(nameof(EverySettingWithAControl))]
    public void ASettingWithAControlIsSaved(string setting, string array)
    {
        var page = Page();

        // Either named in the array the save loop walks, or assigned by hand -- the two selects
        // are read individually because a select is neither a checkbox nor a number input.
        var saved = Bound(page, array).Contains(setting, StringComparer.Ordinal)
            || Regex.IsMatch(page, @"config\." + setting + @"\s*=");

        Assert.True(
            saved,
            $"\"{setting}\" has a control on the settings page but nothing saves it: it is missing from the "
            + $"{array} array and never assigned by hand, so the form will report success and discard whatever "
            + "is typed into it.");
    }

    [Fact]
    public void EveryBoundNameHasAControlToReadItFrom()
    {
        // The other direction: a name left in an array after its control was removed makes the
        // page throw while saving, which takes every other setting on it down too.
        var page = Page();

        foreach (var array in Arrays)
        {
            foreach (var name in Bound(page, array))
            {
                Assert.True(
                    page.Contains("id=\"" + name + "\"", StringComparison.Ordinal),
                    $"The page's {array} array names \"{name}\", but no control on the page has that id.");
            }
        }
    }

    [Fact]
    public void EveryBoundNameIsARealSetting()
    {
        // A misspelling here saves nothing and reads nothing, in both directions, silently.
        var page = Page();
        var settings = typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(property => property.Name, StringComparer.Ordinal);

        foreach (var array in Arrays)
        {
            foreach (var name in Bound(page, array))
            {
                Assert.True(settings.ContainsKey(name), $"The page's {array} array names \"{name}\", which is not a setting.");
                Assert.Equal(array, ArrayFor(settings[name].PropertyType));
            }
        }
    }
}
