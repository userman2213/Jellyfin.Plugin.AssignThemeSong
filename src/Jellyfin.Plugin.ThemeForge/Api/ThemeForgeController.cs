using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
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
    private readonly ILibraryPolicyResolver _policyResolver;
    private readonly IThemeForgeLogger<ThemeForgeController> _logger;

    /// <summary>Initializes a new instance of the <see cref="ThemeForgeController"/> class.</summary>
    /// <param name="orchestrator">The pipeline.</param>
    /// <param name="index">The decision index.</param>
    /// <param name="libraryManager">Library access.</param>
    /// <param name="placementEngine">Theme file placement.</param>
    /// <param name="identityResolver">Item identity.</param>
    /// <param name="toolProvisioner">Tool discovery, for the status panel.</param>
    /// <param name="policyResolver">Lists libraries and their overwrite rules.</param>
    /// <param name="logger">Logger.</param>
    public ThemeForgeController(
        IThemeOrchestrator orchestrator,
        IThemeIndex index,
        ILibraryManager libraryManager,
        IThemePlacementEngine placementEngine,
        IMediaIdentityResolver identityResolver,
        IToolProvisioner toolProvisioner,
        ILibraryPolicyResolver policyResolver,
        IThemeForgeLogger<ThemeForgeController> logger)
    {
        _orchestrator = orchestrator;
        _index = index;
        _libraryManager = libraryManager;
        _placementEngine = placementEngine;
        _identityResolver = identityResolver;
        _toolProvisioner = toolProvisioner;
        _policyResolver = policyResolver;
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
            StaleDecisions = entries.Count(e => e.IsStale),
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
            status.ConfigurationProblems = _policyResolver.Audit(Plugin.Config).Problems;
        }
        catch (Exception ex)
        {
            // Reporting a broken rule must never be the thing that breaks the status panel.
            _logger.LogWarning(ex, "ThemeForge: could not check the per-library rules for the status panel.");
        }

        try
        {
            var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);
            status.YtDlpVersion = tools.YtDlpVersion;
            status.FfmpegPath = tools.Ffmpeg;

            var configuration = Plugin.Config;
            if (Engines.Tooling.YtDlpProvisioner.IsStale(tools.YtDlpVersion, configuration.MaxYtDlpAgeDays))
            {
                status.ToolWarning =
                    $"yt-dlp {tools.YtDlpVersion} is more than {configuration.MaxYtDlpAgeDays} days old. "
                    + "That is the usual cause of downloads failing with HTTP 403 while searching still works. "
                    + "Run the \"Update yt-dlp\" scheduled task, or clear the yt-dlp path so the plugin manages its own copy.";
            }
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
                configuration.BlockedVideoIds = configuration.BlockedVideoIds
                    .Append(entry.ChosenId)
                    .ToArray();
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
        var result = await RemoveWrittenThemesAsync(cancellationToken).ConfigureAwait(false);

        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ThemeForge: bulk theme removal — {Summary}", result.Summary);
        return result;
    }

    /// <summary>
    /// Deletes the theme files ThemeForge wrote, leaving anything changed since alone.
    /// </summary>
    /// <remarks>
    /// The index must already be loaded, and the caller flushes: this is shared between deleting
    /// the themes and starting over, and neither wants the save happening twice.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was deleted, kept and already gone.</returns>
    private async Task<ThemeRemovalResult> RemoveWrittenThemesAsync(CancellationToken cancellationToken)
    {
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

        return result;
    }

    /// <summary>
    /// Throws away everything ThemeForge has decided, so the library is looked at again from
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed because a recorded decision is only as good as the code that made it. Decisions
    /// taken by an earlier version of the matcher are not merely stale — several of the states
    /// they are recorded in, such as "rejected" or "locked", are treated as human judgements and
    /// would never be revisited. Clearing the file is the only way a library that was scored
    /// badly gets scored again.
    /// </para>
    /// <para>
    /// Deleting the theme files is separate and optional. Each one is checked against the
    /// contents recorded when it was written, so anything replaced by hand since is kept: a
    /// fresh start has no business destroying a deliberate choice.
    /// </para>
    /// </remarks>
    /// <param name="confirm">Must be true. Present so this cannot be triggered by accident.</param>
    /// <param name="deleteThemes">Whether to also delete the theme files ThemeForge wrote.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was cleared.</returns>
    [HttpPost("Reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ResetResult>> Reset(
        [FromQuery] bool confirm,
        [FromQuery] bool deleteThemes,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return BadRequest("This discards every decision ThemeForge has recorded. Pass confirm=true to proceed.");
        }

        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        var result = new ResetResult();

        if (deleteThemes)
        {
            var removal = await RemoveWrittenThemesAsync(cancellationToken).ConfigureAwait(false);
            result.ThemesDeleted = removal.Deleted;
            result.ThemesKept = removal.SkippedModified;
        }

        foreach (var entry in _index.All().ToList())
        {
            _index.Remove(entry.ItemId);
            result.DecisionsCleared++;
        }

        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);

        result.Summary = deleteThemes
            ? $"Deleted {result.ThemesDeleted} theme files, kept {result.ThemesKept} you had changed, and discarded {result.DecisionsCleared} recorded decisions."
            : $"Discarded {result.DecisionsCleared} recorded decisions. Theme files were left where they are.";

        _logger.LogInformation("ThemeForge: reset — {Summary}", result.Summary);
        return result;
    }

    /// <summary>
    /// Clears the retry backoff on failed items so the next run tries them again straight away.
    /// </summary>
    /// <remarks>
    /// Failures back off exponentially, which is right when a title genuinely has no theme on
    /// YouTube but wrong after fixing whatever was breaking the run. Without this, a run that
    /// failed for an environmental reason — a stale yt-dlp, no network, a full disk — leaves
    /// every affected item untouchable for hours or days after the cause is gone.
    /// </remarks>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many items were released.</returns>
    [HttpPost("RetryFailed")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<OperationResult>> RetryFailed(CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

        var released = 0;
        foreach (var entry in _index.All().Where(e => e.State == ThemeItemState.Failed))
        {
            entry.State = ThemeItemState.Unprocessed;
            entry.Attempts = 0;
            entry.NextRetryUtc = null;
            entry.LastError = null;
            _index.Put(entry);
            released++;
        }

        await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("ThemeForge: cleared the retry backoff on {Count} failed items.", released);

        return new OperationResult(
            true,
            released == 0
                ? "No failed items to retry."
                : $"{released} items will be tried again on the next run.");
    }

    /// <summary>Lists the server's movie and show libraries with their overwrite rules.</summary>
    /// <returns>One entry per library ThemeForge can act on.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<LibrarySummary>> GetLibraries() =>
        _policyResolver.ListLibraries(Plugin.Config).ToList();

    /// <summary>Saves the per-library rules.</summary>
    /// <remarks>
    /// The saved configuration is read back and compared against what was asked for, and the
    /// answer describes what is actually stored. "Saved" reported from the request body proves
    /// only that the request arrived; the user's report of a rule that would not stay saved is
    /// exactly the case that message cannot distinguish from success.
    /// </remarks>
    /// <param name="policies">The rules to store, one per library.</param>
    /// <returns>What happened.</returns>
    [HttpPost("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<OperationResult> SaveLibraryPolicies([FromBody] List<LibraryThemePolicy> policies)
    {
        ArgumentNullException.ThrowIfNull(policies);

        var configuration = Plugin.Config;

        // Rules that say nothing are not stored, so a library reverting to the default leaves no
        // stale row behind to puzzle over later.
        var meaningful = policies
            .Where(policy => !policy.Enabled || policy.Overwrite != ThemeOverwritePolicy.UseDefault)
            .ToList();

        // A rule whose library id is missing or unparseable can never match an item, so storing
        // it would produce a settings page that shows the rule and an engine that ignores it.
        // Dropping it silently is what made this failure invisible; it is now an error.
        var unusable = meaningful
            .Where(policy => LibraryPolicyResolver.ParseId(policy.LibraryId) == Guid.Empty)
            .ToList();

        if (unusable.Count > 0)
        {
            var names = string.Join(", ", unusable.Select(rule =>
                string.IsNullOrWhiteSpace(rule.LibraryName) ? "(unnamed)" : rule.LibraryName));

            _logger.LogError(
                "ThemeForge: refused to save rules for {Names} because Jellyfin reported no id for those libraries.",
                names);

            return new OperationResult(
                false,
                $"Jellyfin did not report an id for {names}, so a rule saved against it could never be applied. "
                + "Nothing was saved. Reload the page and try again.");
        }

        var wanted = meaningful
            .Select(policy => new LibraryThemePolicy
            {
                // Stored in one canonical form so a value read back compares equal to the id the
                // resolver produces, whatever form the browser sent.
                LibraryId = LibraryPolicyResolver.CanonicalId(LibraryPolicyResolver.ParseId(policy.LibraryId)),
                LibraryName = policy.LibraryName,
                Enabled = policy.Enabled,
                Overwrite = policy.Overwrite,
            })
            .ToArray();

        configuration.LibraryPolicies = wanted;
        Plugin.Instance?.UpdateConfiguration(configuration);

        // Read back from the file Jellyfin wrote, not from the object just handed to it. The
        // in-memory configuration is the same instance that was mutated, so checking it would
        // prove nothing; only what reached the disk survives a restart.
        var persisted = Plugin.Instance?.ReadPersistedConfiguration();
        if (persisted is null)
        {
            _logger.LogError("ThemeForge: the library rules could not be read back after saving.");
            return new OperationResult(
                false,
                "The rules were applied for this session but could not be read back from disk, so they may not survive a restart. Check the server log.");
        }

        var stored = persisted.LibraryPolicies ?? Array.Empty<LibraryThemePolicy>();
        var missing = wanted
            .Where(want => !stored.Any(have =>
                LibraryPolicyResolver.ParseId(have.LibraryId) == LibraryPolicyResolver.ParseId(want.LibraryId)
                && have.Enabled == want.Enabled
                && have.Overwrite == want.Overwrite))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogError(
                "ThemeForge: {Count} of {Total} library rules did not survive being written to {Path}.",
                missing.Count,
                wanted.Length,
                Plugin.Instance?.ConfigurationFilePath);

            return new OperationResult(
                false,
                $"{missing.Count} of {wanted.Length} rules did not persist: {string.Join(", ", missing.Select(rule => rule.LibraryName))}. "
                + "Jellyfin may not be able to write its plugin configuration directory.");
        }

        _logger.LogInformation("ThemeForge: saved and verified {Count} per-library rules.", stored.Length);

        var audit = _policyResolver.Audit(Plugin.Config);
        foreach (var problem in audit.Problems)
        {
            _logger.LogWarning("ThemeForge: {Problem}", problem);
        }

        return new OperationResult(
            true,
            audit.Problems.Count == 0
                ? (stored.Length == 0
                    ? "Every library now follows the server-wide default."
                    : $"{stored.Length} library rules saved and verified.")
                : $"Saved, but: {string.Join(" ", audit.Problems)}");
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
                SkipReason = entry?.LastSkipReason,
                BandDiffStd = entry?.BandDiffStd,
            });
        }

        return rows.OrderBy(row => row.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
