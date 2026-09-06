using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// The scheduled run that finds and assigns theme songs across the library.
/// </summary>
public sealed class DiscoverThemesTask : IScheduledTask
{
    private readonly IThemeOrchestrator _orchestrator;
    private readonly ILogger<DiscoverThemesTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="DiscoverThemesTask"/> class.</summary>
    /// <param name="orchestrator">The pipeline.</param>
    /// <param name="logger">Logger.</param>
    public DiscoverThemesTask(IThemeOrchestrator orchestrator, ILogger<DiscoverThemesTask> logger)
    {
        _orchestrator = orchestrator;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Find and assign theme songs";

    /// <inheritdoc />
    public string Description =>
        "Searches for a theme song for every movie and series that does not have one, scores the candidates, " +
        "and assigns the confident matches. Anything less certain is added to the ThemeForge review queue.";

    /// <inheritdoc />
    public string Category => "ThemeForge";

    /// <inheritdoc />
    public string Key => "ThemeForgeDiscover";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var report = await _orchestrator.RunAsync(progress, cancellationToken).ConfigureAwait(false);

        if (report.FatalError is not null)
        {
            // Surfacing this as a task failure is what puts it in front of the user in the
            // dashboard, rather than leaving it buried in the log.
            throw new InvalidOperationException("ThemeForge could not complete the run: " + report.FatalError);
        }

        _logger.LogInformation("ThemeForge: {Summary}", report.ToString());
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        // Overnight by default: a full run makes a lot of outbound requests and competes with
        // transcoding for CPU during the encode step.
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        },
    };
}
