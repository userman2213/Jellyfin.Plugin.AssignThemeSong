using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Logging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>One setting, as the engine has it and as it is stored.</summary>
/// <param name="Name">The setting's name.</param>
/// <param name="InUse">The value the engine will use for the next run.</param>
/// <param name="OnDisk">The value in the saved configuration file.</param>
/// <param name="Agrees">Whether those are the same.</param>
public sealed record SettingRow(string Name, string InUse, string OnDisk, bool Agrees);

/// <summary>How often one skip reason occurred, and an example.</summary>
/// <param name="Reason">The reason, as the pipeline phrased it.</param>
/// <param name="Count">How many items it applies to.</param>
/// <param name="Examples">A few of those items, by name.</param>
public sealed record SkipReasonRow(string Reason, int Count, IReadOnlyList<string> Examples);

/// <summary>Everything needed to tell whether the settings are actually in force.</summary>
public sealed class DiagnosticsDto
{
    /// <summary>Gets or sets where Jellyfin stores this plugin's settings.</summary>
    public string ConfigurationFilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that file could be read back.</summary>
    public bool ConfigurationReadable { get; set; }

    /// <summary>Gets or sets when that file was last written.</summary>
    public DateTime? ConfigurationSavedUtc { get; set; }

    /// <summary>Gets or sets every setting, in use and as stored.</summary>
    public IReadOnlyList<SettingRow> Settings { get; set; } = Array.Empty<SettingRow>();

    /// <summary>Gets or sets the settings whose stored value differs from the one in use.</summary>
    public IReadOnlyList<SettingRow> Disagreements { get; set; } = Array.Empty<SettingRow>();

    /// <summary>Gets or sets each library and the rule that will actually apply to it.</summary>
    public IReadOnlyList<LibraryDiagnostic> Libraries { get; set; } = Array.Empty<LibraryDiagnostic>();

    /// <summary>Gets or sets stored rules that match no library on this server.</summary>
    public IReadOnlyList<LibrarySummary> OrphanedRules { get; set; } = Array.Empty<LibrarySummary>();

    /// <summary>Gets or sets why items were passed over on the last run, most common first.</summary>
    public IReadOnlyList<SkipReasonRow> SkipReasons { get; set; } = Array.Empty<SkipReasonRow>();

    /// <summary>Gets or sets anything found to be wrong, in plain words.</summary>
    public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the plugin's data directory.</summary>
    public string DataPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the index file's path.</summary>
    public string IndexPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the log file's path.</summary>
    public string LogPath { get; set; } = string.Empty;
}

/// <summary>
/// Reports what ThemeForge is actually configured to do, as opposed to what the settings form
/// shows.
/// </summary>
/// <remarks>
/// A settings page shows what was typed into it. It cannot show a save that did not reach the
/// disk, a rule stored against a library id nothing resolves to, or the reason an item was passed
/// over — and those were exactly the failures that made "I set the rule and nothing happened"
/// impossible to diagnose from the UI. Every value here is read back from where the engine reads
/// it, so a discrepancy is visible rather than inferred.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ThemeForge/Diagnostics")]
[Produces(MediaTypeNames.Application.Json)]
public class DiagnosticsController : ControllerBase
{
    private const int MaxExamplesPerReason = 5;

    private readonly IThemeIndex _index;
    private readonly ILibraryPolicyResolver _policyResolver;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticsController"/> class.</summary>
    /// <param name="index">The decision index, for recorded skip reasons.</param>
    /// <param name="policyResolver">Resolves libraries and their rules.</param>
    public DiagnosticsController(IThemeIndex index, ILibraryPolicyResolver policyResolver)
    {
        _index = index;
        _policyResolver = policyResolver;
    }

    /// <summary>Reports the configuration in force, the resolved library rules and why items were skipped.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The diagnostics.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsDto>> Get(CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

        var inUse = Plugin.Config;
        var onDisk = Plugin.Instance?.ReadPersistedConfiguration();
        var problems = new List<string>();

        var settings = CompareSettings(inUse, onDisk);
        var disagreements = settings.Where(row => !row.Agrees).ToList();

        if (onDisk is null)
        {
            problems.Add(
                "The saved settings file could not be read, so nothing here is guaranteed to survive a restart. "
                + "Check that Jellyfin can read and write its plugin configuration directory.");
        }
        else if (disagreements.Count > 0)
        {
            problems.Add(
                $"{disagreements.Count} settings differ between what the engine is using and what is saved on disk. "
                + "The saved values are what will be used after a restart.");
        }

        var audit = _policyResolver.Audit(inUse);
        problems.AddRange(audit.Problems);

        return new DiagnosticsDto
        {
            ConfigurationFilePath = Plugin.Instance?.ConfigurationFilePath ?? string.Empty,
            ConfigurationReadable = onDisk is not null,
            ConfigurationSavedUtc = LastWritten(Plugin.Instance?.ConfigurationFilePath),
            Settings = settings,
            Disagreements = disagreements,
            Libraries = audit.Libraries,
            OrphanedRules = audit.OrphanedRules,
            SkipReasons = SkipReasons(),
            Problems = problems,
            DataPath = Plugin.Instance?.DataPath ?? string.Empty,
            IndexPath = Plugin.Instance?.IndexPath ?? string.Empty,
            LogPath = ThemeForgeLogFile.Shared.CurrentPath ?? "(file logging is off)",
        };
    }

    private static DateTime? LastWritten(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
                ? null
                : System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares every setting the engine holds against the one that was saved.
    /// </summary>
    /// <remarks>
    /// Done by reflection rather than by listing fields, so a setting added later cannot be
    /// forgotten here and quietly escape the check.
    /// </remarks>
    private static IReadOnlyList<SettingRow> CompareSettings(PluginConfiguration inUse, PluginConfiguration? onDisk) =>
        typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                var live = Describe(Read(property, inUse));
                var saved = onDisk is null ? "(unreadable)" : Describe(Read(property, onDisk));
                return new SettingRow(property.Name, live, saved, onDisk is not null && string.Equals(live, saved, StringComparison.Ordinal));
            })
            .ToList();

    private static object? Read(PropertyInfo property, PluginConfiguration configuration)
    {
        try
        {
            return property.GetValue(configuration);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "(not set)",
        string text => text.Length == 0 ? "(empty)" : text,
        bool flag => flag ? "yes" : "no",
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        IEnumerable list and not string => DescribeList(list),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string DescribeList(IEnumerable list)
    {
        var parts = list.Cast<object?>().Select(Describe).ToList();
        return parts.Count == 0 ? "(none)" : $"{parts.Count}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Groups the reasons items were passed over.
    /// </summary>
    /// <remarks>
    /// A run reports "767 skipped" and stops there, which says nothing about whether the rule you
    /// just changed was even consulted. The pipeline already phrases a reason per item; this
    /// counts them so the answer is one line rather than a log search.
    /// </remarks>
    private IReadOnlyList<SkipReasonRow> SkipReasons() =>
        _index.All()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.LastSkipReason))
            .GroupBy(entry => entry.LastSkipReason!, StringComparer.Ordinal)
            .Select(group => new SkipReasonRow(
                group.Key,
                group.Count(),
                group.OrderByDescending(entry => entry.LastSkipUtc ?? DateTime.MinValue)
                    .Take(MaxExamplesPerReason)
                    .Select(entry => entry.Label)
                    .ToList()))
            .OrderByDescending(row => row.Count)
            .ToList();
}
