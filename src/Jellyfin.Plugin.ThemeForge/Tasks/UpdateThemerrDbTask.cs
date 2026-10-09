using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// Keeps the local copy of ThemerrDB's index current.
/// </summary>
/// <remarks>
/// <para>
/// ThemerrDB publishes once a day, at 12:00 UTC, so this runs an hour after that. Nothing is
/// gained by asking more often, and the alternative — asking the database about every title in the
/// library on every scan — spends hundreds of requests discovering that most of them are not in
/// it.
/// </para>
/// <para>
/// The task is a mirror, not the lookup itself. If it has never run, the source falls back to
/// asking about each item directly, which is slower but correct.
/// </para>
/// </remarks>
public sealed class UpdateThemerrDbTask : IScheduledTask
{
    /// <summary>An hour after ThemerrDB publishes, so a run always sees the day's release.</summary>
    private static readonly TimeSpan PublishedAtUtc = TimeSpan.FromHours(13);

    private readonly IThemerrDbCatalogue _catalogue;
    private readonly IThemeForgeLogger<UpdateThemerrDbTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateThemerrDbTask"/> class.</summary>
    /// <param name="catalogue">The local copy to refresh.</param>
    /// <param name="logger">Logger.</param>
    public UpdateThemerrDbTask(IThemerrDbCatalogue catalogue, IThemeForgeLogger<UpdateThemerrDbTask> logger)
    {
        _catalogue = catalogue;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update the ThemerrDB catalogue";

    /// <inheritdoc />
    public string Description =>
        "Reads the list of films, shows and collections ThemerrDB has a theme for, so ThemeForge can tell at a "
        + "glance whether a title is in the database instead of asking about every one of them. "
        + "ThemerrDB publishes once a day at 12:00 UTC.";

    /// <inheritdoc />
    public string Category => "ThemeForge";

    /// <inheritdoc />
    public string Key => "ThemeForgeUpdateThemerrDb";

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

        if (!Plugin.Config.SyncThemerrDb)
        {
            _logger.LogInformation("ThemeForge: the ThemerrDB catalogue is switched off; nothing to do.");
            progress.Report(100);
            return;
        }

        var snapshot = await _catalogue.SyncAsync(progress, cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        _logger.LogInformation(
            "ThemeForge: the ThemerrDB catalogue lists {Movies} films, {Shows} shows and {Collections} collections.",
            snapshot.MovieTmdbIds.Count,
            snapshot.TvShows.Count,
            snapshot.Collections.Count);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = PublishedAtUtc.Ticks,
        },
    };
}
