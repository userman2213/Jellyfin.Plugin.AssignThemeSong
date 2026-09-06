using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// Keeps the bundled yt-dlp current.
/// </summary>
/// <remarks>
/// YouTube changes how it serves media without warning, and a stale yt-dlp simply stops being
/// able to download anything. Because ThemeForge keeps its own copy, staying current is a
/// scheduled task rather than something the user has to notice and fix.
/// </remarks>
public sealed class UpdateYtDlpTask : IScheduledTask
{
    private readonly IToolProvisioner _toolProvisioner;
    private readonly ILogger<UpdateYtDlpTask> _logger;

    /// <summary>Initializes a new instance of the <see cref="UpdateYtDlpTask"/> class.</summary>
    /// <param name="toolProvisioner">The provisioner that owns the binary.</param>
    /// <param name="logger">Logger.</param>
    public UpdateYtDlpTask(IToolProvisioner toolProvisioner, ILogger<UpdateYtDlpTask> logger)
    {
        _toolProvisioner = toolProvisioner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Update yt-dlp";

    /// <inheritdoc />
    public string Description =>
        "Downloads the current release of yt-dlp, which ThemeForge uses to search for and download theme songs. " +
        "Does nothing when yt-dlp is managed outside the plugin.";

    /// <inheritdoc />
    public string Category => "ThemeForge";

    /// <inheritdoc />
    public string Key => "ThemeForgeUpdateYtDlp";

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

        progress.Report(10);
        var version = await _toolProvisioner.UpdateAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);

        if (version is not null)
        {
            _logger.LogInformation("ThemeForge: yt-dlp is now at {Version}.", version);
        }
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
    {
        new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromDays(7).Ticks,
        },
    };
}
