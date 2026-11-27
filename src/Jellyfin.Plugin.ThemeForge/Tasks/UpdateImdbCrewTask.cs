using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// Keeps the local copy of IMDb's composer credits current.
/// </summary>
/// <remarks>
/// <para>
/// Checks daily, at 01:30 UTC before the composer research and the discovery run, and when the
/// server starts, so a new installation has the data before its first run. Checking is free; a
/// download happens only when there is no copy, when the copy is a month old, or when a title in
/// the library was added to IMDb after the copy was made and the copy is a week old.
/// </para>
/// <para>
/// Running the task by hand checks the same way, and says in the log why it did or did not
/// download.
/// </para>
/// </remarks>
public sealed class UpdateImdbCrewTask : IScheduledTask
{
    /// <summary>Before the composer research at 02:00 and the discovery run at 03:00.</summary>
    private static readonly TimeSpan RunAtUtc = new(1, 30, 0);

    private readonly IImdbCrewData _crew;
    private readonly IThemeForgeLogger<UpdateImdbCrewTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateImdbCrewTask"/> class.</summary>
    /// <param name="crew">The local copy.</param>
    /// <param name="logger">Logger.</param>
    public UpdateImdbCrewTask(IImdbCrewData crew, IThemeForgeLogger<UpdateImdbCrewTask> logger)
    {
        _crew = crew;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update IMDb crew data";

    /// <inheritdoc />
    public string Description =>
        "Downloads IMDb's own dataset files and keeps who IMDb credits as the composer of each film, and of each "
        + "series through its episodes, so a title is searched for by who made its music. About 1.2 GB is read as "
        + "it arrives and not stored; what is kept is about 20 MB. Downloads only when there is no copy, when the copy "
        + "is a month old, or when the library has a title newer than the copy and the copy is a week old.";

    /// <inheritdoc />
    public string Category => "ThemeForge";

    /// <inheritdoc />
    public string Key => "ThemeForgeUpdateImdbCrew";

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

        if (!_crew.IsDue(Plugin.Config, DateTime.UtcNow, out var reason))
        {
            _logger.LogInformation("ThemeForge: not downloading IMDb's crew data: {Reason}.", reason);
            progress.Report(100);
            return;
        }

        _logger.LogInformation("ThemeForge: downloading IMDb's crew data, because {Reason}.", reason);
        await _crew.RefreshAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.StartupTrigger,
        },
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = RunAtUtc.Ticks,
        },
    };
}
