using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// Finds out who wrote the music for the titles in the library.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin records a composer for very few items, and an item with no composer is searched for
/// with one fewer phrasing, scored without the bonus an upload naming the composer earns, and --
/// if its title is an ordinary word -- held below the auto-assign band for want of anything to
/// corroborate it. The name is public and does not change, so it is worth looking up once.
/// </para>
/// <para>
/// Runs at 02:00 UTC, an hour before the discovery run, so a search always has the day's answers.
/// A whole library costs a handful of Wikidata requests plus one per second for whatever is left
/// over, and only for the titles that have not already been answered for.
/// </para>
/// </remarks>
public sealed class UpdateComposerCacheTask : IScheduledTask
{
    /// <summary>An hour before the discovery run, so the day's answers are in place before it starts.</summary>
    private static readonly TimeSpan RunAtUtc = TimeSpan.FromHours(2);

    private readonly IThemeOrchestrator _orchestrator;
    private readonly IThemeForgeLogger<UpdateComposerCacheTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateComposerCacheTask"/> class.</summary>
    /// <param name="orchestrator">Knows the library, which is what the question is asked about.</param>
    /// <param name="logger">Logger.</param>
    public UpdateComposerCacheTask(IThemeOrchestrator orchestrator, IThemeForgeLogger<UpdateComposerCacheTask> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Look up who wrote the music";

    /// <inheritdoc />
    public string Description =>
        "Asks Wikidata and MusicBrainz for the composer of every film and series in the library, by its own "
        + "database id, and keeps the answers. The composer's name is the most specific thing a search can ask "
        + "for, and Jellyfin itself records one for very few items. Answers are kept for two months, so this is "
        + "cheap after the first run.";

    /// <inheritdoc />
    public string Category => "ThemeForge";

    /// <inheritdoc />
    public string Key => "ThemeForgeUpdateComposers";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!Plugin.Config.ResearchComposers || !Plugin.Config.SyncComposers)
        {
            _logger.LogInformation("ThemeForge: looking up who wrote the music is switched off; nothing to do.");
            progress.Report(100);
            return;
        }

        var coverage = await _orchestrator.SyncComposersAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        _logger.LogInformation(
            "ThemeForge: somebody is credited with the music of {Known} of {Titles} titles; {Unknown} are not recorded anywhere.",
            coverage.Known,
            coverage.Titles,
            coverage.Unknown);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = RunAtUtc.Ticks,
        },
    };
}
