using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>
/// Administrative API for ThemeForge.
/// </summary>
/// <remarks>
/// Every endpoint requires an administrator. Assigning a theme writes a file into the media
/// library and starting a run makes outbound network requests from the server, so there is no
/// part of this surface that a non-administrator should reach. The plugin has no per-user
/// settings of its own: theme playback is controlled by Jellyfin's own per-user display
/// settings, which are already the authority on it.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ThemeForge")]
[Produces(MediaTypeNames.Application.Json)]
public class ThemeForgeController : ControllerBase
{
    private readonly IThemeOrchestrator _orchestrator;
    private readonly IThemeIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly IThemePlacementEngine _placementEngine;
    private readonly IMediaIdentityResolver _identityResolver;
    private readonly IToolProvisioner _toolProvisioner;
    private readonly IThemeForgeLogger<ThemeForgeController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemeForgeController"/> class.</summary>
    /// <param name="orchestrator">The pipeline.</param>
    /// <param name="index">The decision index.</param>
    /// <param name="libraryManager">Library access.</param>
    /// <param name="placementEngine">Theme file placement.</param>
    /// <param name="identityResolver">Item identity.</param>
    /// <param name="toolProvisioner">Tool discovery, for the status panel.</param>
    /// <param name="logger">Logger.</param>
    public ThemeForgeController(
        IThemeOrchestrator orchestrator,
        IThemeIndex index,
        ILibraryManager libraryManager,
        IThemePlacementEngine placementEngine,
        IMediaIdentityResolver identityResolver,
        IToolProvisioner toolProvisioner,
        IThemeForgeLogger<ThemeForgeController> logger)
    {
        _orchestrator = orchestrator;
        _index = index;
        _libraryManager = libraryManager;
        _placementEngine = placementEngine;
        _identityResolver = identityResolver;
        _toolProvisioner = toolProvisioner;
        _logger = logger;
    }

    /// <summary>Reports what the plugin is doing and whether its tools are working.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The current status.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<StatusDto>> GetStatus(CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        var entries = _index.All();

        var status = new StatusDto
        {
            IsRunning = _orchestrator.IsRunning,
            PendingReview = entries.Count(e => e.State == ThemeItemState.PendingReview),
            Assigned = entries.Count(e => e.State is ThemeItemState.AutoAssigned or ThemeItemState.Approved),
            Failed = entries.Count(e => e.State == ThemeItemState.Failed),
            Indexed = entries.Count,
        };

        var lastRun = _orchestrator.LastRun;
        if (lastRun is not null)
        {
            status.LastRunSummary = lastRun.ToString();
            status.LastRunFinishedUtc = lastRun.FinishedUtc;
            status.LastRunReasons = lastRun.TopReasons
                .OrderByDescending(pair => pair.Value)
                .Take(5)
                .Select(pair => $"{pair.Value}x {pair.Key}")
                .ToList();
        }

        try
        {
            var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);
            status.YtDlpVersion = tools.YtDlpVersion;
            status.FfmpegPath = tools.Ffmpeg;
        }
        catch (Exception ex)
        {
            // The status panel exists to surface exactly this, so it is reported rather than thrown.
            status.ToolProblem = ex.Message;
        }

        return status;
    }

    /// <summary>Starts a run over the whole library.</summary>
    /// <returns>Whether the run was started.</returns>
    [HttpPost("Run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OperationResult> StartRun()
    {
        if (_orchestrator.IsRunning)
        {
            return new OperationResult(false, "A run is already in progress.");
        }

        // Detached deliberately: a full library run takes far longer than any sensible HTTP
        // timeout, and progress is polled through the status endpoint.
        _ = Task.Run(async () =>
        {
            try
            {
                await _orchestrator.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ThemeForge: the run started from the configuration page failed.");
            }
        });

        return new OperationResult(true, "Run started. Progress appears in the status panel and the server log.");
    }

    /// <summary>Lists the items awaiting a human decision.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The review queue, best score first.</returns>
    [HttpGet("Queue")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ReviewItemDto>>> GetQueue(CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

        return _index.ReviewQueue().Select(entry => new ReviewItemDto
        {
            ItemId = entry.ItemId,
            Label = entry.Label,
            Kind = entry.Kind,
            CandidateTitle = entry.ChosenTitle,
            CandidateUrl = entry.ChosenUrl,
            CandidateChannel = entry.ChosenChannel,
            Score = entry.Score,
            ScoreBreakdown = entry.ScoreBreakdown,
            Alternates = entry.Rejected
                .Select(r => new AlternateDto(r.Id, r.Title, r.Url, r.Score, r.Reason))
                .ToList(),
        }).ToList();
    }

    /// <summary>Accepts a proposed theme, or a specific alternative, and writes it to the library.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="url">The URL to use; defaults to the proposed candidate.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Queue/{itemId}/Approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Approve(
        [FromRoute] Guid itemId,
        [FromQuery] string? url,
        CancellationToken cancellationToken)
    {
        var entry = _index.Get(itemId);
        var target = url ?? entry?.ChosenUrl;

        if (string.IsNullOrWhiteSpace(target))
        {
            return new OperationResult(false, "There is no candidate to approve for this item.");
        }

        var (success, message) = await _orchestrator
            .ApplyThemeAsync(itemId, target, ThemeItemState.Approved, cancellationToken)
            .ConfigureAwait(false);

        return new OperationResult(success, message);
    }

    /// <summary>
    /// Rejects every candidate offered for an item, so it is not offered again.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="blockVideo">Whether to also block the proposed video across the whole library.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Queue/{itemId}/Reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Reject(
        [FromRoute] Guid itemId,
        [FromQuery] bool blockVideo,
        CancellationToken cancellationToken)
    {
        var entry = _index.Get(itemId);
        if (entry is null)
        {
            return new OperationResult(false, "That item is not in the index.");
        }

        entry.State = ThemeItemState.Rejected;
        entry.LastError = null;
        _index.Put(entry);

        if (blockVideo && !string.IsNullOrEmpty(entry.ChosenId))
        {
            var configuration = Plugin.Config;
            if (!configuration.BlockedVideoIds.Contains(entry.ChosenId, StringComparer.OrdinalIgnoreCase))
            {
                configuration.BlockedVideoIds.Add(entry.ChosenId);
                Plugin.Instance?.UpdateConfiguration(configuration);
            }
        }

        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new OperationResult(true, blockVideo
            ? "Rejected, and that video will not be suggested for anything else."
            : "Rejected. ThemeForge will not suggest a theme for this item again.");
    }

    /// <summary>Sets an item's theme from a URL supplied by the administrator.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="request">The URL to download.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Items/{itemId}/Assign")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> Assign(
        [FromRoute] Guid itemId,
        [FromBody] AssignRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (success, message) = await _orchestrator
            .ApplyThemeAsync(itemId, request.Url, ThemeItemState.ManualOverride, cancellationToken)
            .ConfigureAwait(false);

        return new OperationResult(success, message);
    }

    /// <summary>Pins an item so no automated run ever changes its theme.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="locked">Whether to lock or unlock.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Items/{itemId}/Lock")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> SetLock(
        [FromRoute] Guid itemId,
        [FromQuery] bool locked,
        CancellationToken cancellationToken)
    {
        var entry = _index.Get(itemId);
        if (entry is null)
        {
            return new OperationResult(false, "That item is not in the index.");
        }

        entry.State = locked
            ? ThemeItemState.Locked
            : entry.ThemePath is null ? ThemeItemState.Unprocessed : ThemeItemState.Approved;

        _index.Put(entry);
        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new OperationResult(true, locked ? "Locked." : "Unlocked.");
    }

    /// <summary>Deletes an item's theme file and forgets everything recorded about it.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened.</returns>
    [HttpDelete("Items/{itemId}/Theme")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> DeleteTheme([FromRoute] Guid itemId, CancellationToken cancellationToken)
    {
        var entry = _index.Get(itemId);
        var path = entry?.ThemePath;

        if (path is not null && System.IO.File.Exists(path))
        {
            try
            {
                System.IO.File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new OperationResult(false, $"Could not delete the theme file: {ex.Message}");
            }
        }

        _index.Remove(itemId);
        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new OperationResult(true, "Theme removed. The next run will look for a new one.");
    }

    /// <summary>
    /// Deletes every theme file ThemeForge wrote into the library.
    /// </summary>
    /// <remarks>
    /// Each file is hashed before deletion and left alone if the hash no longer matches what was
    /// recorded when it was written. A changed hash means the user replaced that theme by hand,
    /// and a bulk cleanup has no business destroying a deliberate choice. Files ThemeForge never
    /// wrote are not touched at all, because they are not in the index.
    /// </remarks>
    /// <param name="confirm">Must be true. Present so the endpoint cannot be triggered by accident.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Counts of what was deleted, kept and already missing.</returns>
    [HttpDelete("Themes")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ThemeRemovalResult>> RemoveAllThemes(
        [FromQuery] bool confirm,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return BadRequest("This deletes theme files from your library. Pass confirm=true to proceed.");
        }

        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new ThemeRemovalResult();

        foreach (var entry in _index.All())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.ThemePath is null || entry.Sha256 is null)
            {
                continue;
            }

            if (!System.IO.File.Exists(entry.ThemePath))
            {
                result.AlreadyMissing++;
                entry.ThemePath = null;
                entry.Sha256 = null;
                entry.State = ThemeItemState.Unprocessed;
                _index.Put(entry);
                continue;
            }

            try
            {
                var actual = await FileHash.ComputeSha256Async(entry.ThemePath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.SkippedModified++;
                    continue;
                }

                System.IO.File.Delete(entry.ThemePath);
                result.Deleted++;

                entry.ThemePath = null;
                entry.Sha256 = null;
                entry.State = ThemeItemState.Unprocessed;
                _index.Put(entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Failures.Add($"{entry.Label}: {ex.Message}");
            }
        }

        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ThemeForge: bulk theme removal — {Summary}", result.Summary);
        return result;
    }

    /// <summary>Returns the tail of ThemeForge's own log file.</summary>
    /// <param name="lines">How many lines to return.</param>
    /// <returns>The most recent log lines, oldest first.</returns>
    [HttpGet("Log")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<string>> GetLog([FromQuery] int lines = 300) =>
        Logging.ThemeForgeLogFile.Shared.Tail(lines);

    /// <summary>Lists every movie and series with its theme status.</summary>
    /// <param name="filter">Optional case-insensitive title filter.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The overview rows.</returns>
    [HttpGet("Library")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<LibraryRowDto>>> GetLibrary(
        [FromQuery] string? filter,
        CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        var configuration = Plugin.Config;

        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series },
            Recursive = true,
            IsVirtualItem = false,
        });

        var rows = new List<LibraryRowDto>(items.Count);

        foreach (var item in items)
        {
            var identity = _identityResolver.Resolve(item);
            if (identity is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(filter)
                && identity.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            var entry = _index.Get(item.Id);
            rows.Add(new LibraryRowDto
            {
                ItemId = item.Id,
                Label = identity.Label,
                Kind = identity.Kind.ToString(),
                State = (entry?.State ?? ThemeItemState.Unprocessed).ToString(),
                HasThemeFile = _placementEngine.HasExistingTheme(item, configuration),
                ThemeTitle = entry?.ChosenTitle,
                ThemeUrl = entry?.ChosenUrl,
                Score = entry?.Score,
                LoudnessLufs = entry?.LoudnessLufs,
                LastError = entry?.LastError,
            });
        }

        return rows.OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
