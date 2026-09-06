using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Acquisition;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Placement;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>Drives the whole pipeline.</summary>
public interface IThemeOrchestrator
{
    /// <summary>Processes the whole library.</summary>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what happened.</returns>
    Task<RunReport> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>Processes a single item.</summary>
    /// <param name="item">The item.</param>
    /// <param name="report">Report to record the outcome in.</param>
    /// <param name="configuration">
    /// The settings to use. Passed in rather than read here so that every item in a run sees the
    /// same configuration, even if it is edited while the run is going.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What happened to the item.</returns>
    Task<ItemOutcome> ProcessItemAsync(BaseItem item, RunReport report, PluginConfiguration configuration, CancellationToken cancellationToken);

    /// <summary>Gets the most recent run's report, if there has been one.</summary>
    RunReport? LastRun { get; }

    /// <summary>Gets a value indicating whether a run is currently in progress.</summary>
    bool IsRunning { get; }

    /// <summary>
    /// Downloads a specific source and makes it an item's theme, bypassing search and scoring.
    /// </summary>
    /// <param name="itemId">The item to give a theme.</param>
    /// <param name="sourceUrl">The URL to download.</param>
    /// <param name="state">The state to record, distinguishing an approved suggestion from a manual choice.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Whether it worked, and a message describing the outcome.</returns>
    Task<(bool Success, string Message)> ApplyThemeAsync(
        Guid itemId,
        string sourceUrl,
        ThemeItemState state,
        CancellationToken cancellationToken);
}

/// <summary>
/// Runs Identity, Query, Discovery, Scoring, Decision, Acquisition and Placement in order, over
/// the whole library.
/// </summary>
/// <remarks>
/// This is the only class that knows the shape of the pipeline. Every engine it calls is
/// replaceable without touching the others, and none of them knows this class exists.
/// </remarks>
public sealed class ThemeOrchestrator : IThemeOrchestrator, IDisposable
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaIdentityResolver _identityResolver;
    private readonly IQueryPlanner _queryPlanner;
    private readonly ICandidateSource _candidateSource;
    private readonly IScoringEngine _scoringEngine;
    private readonly IDecisionPolicy _decisionPolicy;
    private readonly IAcquisitionEngine _acquisitionEngine;
    private readonly IThemePlacementEngine _placementEngine;
    private readonly IThemeIndex _index;
    private readonly ILibraryPolicyResolver _policyResolver;
    private readonly IThemeForgeLogger<ThemeOrchestrator> _logger;
    private readonly RequestThrottle _throttle = new();

    /// <summary>How many items are processed between index saves during a run.</summary>
    private const int IndexFlushInterval = 25;

    private int _running;

    /// <summary>Initializes a new instance of the <see cref="ThemeOrchestrator"/> class.</summary>
    /// <param name="libraryManager">Enumerates library items.</param>
    /// <param name="identityResolver">Resolves item identity.</param>
    /// <param name="queryPlanner">Plans the search ladder.</param>
    /// <param name="candidateSource">Finds candidates.</param>
    /// <param name="scoringEngine">Ranks candidates.</param>
    /// <param name="decisionPolicy">Decides what to do with the best candidate.</param>
    /// <param name="acquisitionEngine">Downloads and normalises the chosen candidate.</param>
    /// <param name="placementEngine">Writes the theme into the library.</param>
    /// <param name="index">Records decisions.</param>
    /// <param name="policyResolver">Resolves per-library overwrite rules.</param>
    /// <param name="logger">Logger.</param>
    public ThemeOrchestrator(
        ILibraryManager libraryManager,
        IMediaIdentityResolver identityResolver,
        IQueryPlanner queryPlanner,
        ICandidateSource candidateSource,
        IScoringEngine scoringEngine,
        IDecisionPolicy decisionPolicy,
        IAcquisitionEngine acquisitionEngine,
        IThemePlacementEngine placementEngine,
        IThemeIndex index,
        ILibraryPolicyResolver policyResolver,
        IThemeForgeLogger<ThemeOrchestrator> logger)
    {
        _libraryManager = libraryManager;
        _identityResolver = identityResolver;
        _queryPlanner = queryPlanner;
        _candidateSource = candidateSource;
        _scoringEngine = scoringEngine;
        _decisionPolicy = decisionPolicy;
        _acquisitionEngine = acquisitionEngine;
        _placementEngine = placementEngine;
        _index = index;
        _policyResolver = policyResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public RunReport? LastRun { get; private set; }

    /// <inheritdoc />
    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <inheritdoc />
    public async Task<RunReport> RunAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            throw new InvalidOperationException("A ThemeForge run is already in progress.");
        }

        // Taken once. Reading it per item meant a settings change mid-run applied to some items
        // and not others, and the run's own report could contradict what actually happened --
        // a dry run that reported writing nothing while items were being downloaded and assigned.
        var configuration = Plugin.Config.ShallowCopy();
        var report = new RunReport { WasDryRun = configuration.DryRun };
        LastRun = report;

        try
        {
            await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

            // Stated up front, every run: the rule the pipeline will actually apply to each
            // library, and any rule stored against a library that no longer exists. A settings
            // page can only show what was typed into it; this shows what the engine resolved.
            _policyResolver.LogEffectiveRules(configuration);

            var items = GetLibraryItems(configuration);
            report.Considered = items.Count;
            _logger.LogInformation(
                "ThemeForge: starting a run over {Count} items{DryRun}.",
                items.Count,
                configuration.DryRun ? " in dry-run mode" : string.Empty);

            var processed = 0;
            using var concurrency = new SemaphoreSlim(Math.Max(1, configuration.MaxConcurrency));

            var work = items.Select(async item =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var outcome = await ProcessItemAsync(item, report, configuration, cancellationToken).ConfigureAwait(false);
                    lock (report)
                    {
                        report.Record(outcome);
                    }
                }
                finally
                {
                    concurrency.Release();
                    var done = Interlocked.Increment(ref processed);
                    progress?.Report(items.Count == 0 ? 100 : 100.0 * done / items.Count);

                    // A run over a large library takes hours. Flushing only at the end meant a
                    // container restart or a power cut threw away everything it had decided, so
                    // the next run started from nothing. Flushes are debounced by the index's
                    // dirty flag, so this is close to free when nothing has changed.
                    if (done % IndexFlushInterval == 0)
                    {
                        await _index.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                }
            });

            await Task.WhenAll(work).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            report.WasCancelled = true;
            _logger.LogInformation("ThemeForge: the run was cancelled.");
        }
        catch (Exception ex)
        {
            // A failure here is environmental — yt-dlp missing, no network — rather than about
            // any one item, so it is recorded once and surfaced on the configuration page.
            report.FatalError = ex.Message;
            _logger.LogError(ex, "ThemeForge: the run stopped early.");
        }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            await _index.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _running, 0);
            _logger.LogInformation("ThemeForge: run finished — {Summary}", report.ToString());
        }

        return report;
    }

    /// <inheritdoc />
    public async Task<ItemOutcome> ProcessItemAsync(
        BaseItem item,
        RunReport report,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(configuration);

        var identity = _identityResolver.Resolve(item);
        if (identity is null)
        {
            return ItemOutcome.Skipped;
        }

        var policy = _policyResolver.Resolve(item, configuration);
        if (!policy.Enabled)
        {
            return ItemOutcome.Skipped;
        }

        var entry = _index.Resolve(item.Id, identity.StableKey) ?? NewEntry(identity);

        var skipReason = ShouldSkip(item, entry, configuration, policy);
        if (skipReason is not null)
        {
            // Recorded rather than only logged: "why was this skipped" is the first question asked
            // when a settings change appears to do nothing, and the log is not where people look.
            entry.LastSkipReason = skipReason;
            entry.LastSkipUtc = DateTime.UtcNow;
            _index.Put(entry);

            _logger.LogDebug("ThemeForge: skipping \"{Item}\" — {Reason}.", identity.Label, skipReason);
            return ItemOutcome.Skipped;
        }

        entry.LastSkipReason = null;
        entry.LastSkipUtc = null;

        try
        {
            var ranked = await SearchAndRankAsync(identity, configuration, cancellationToken).ConfigureAwait(false);
            var decision = _decisionPolicy.Decide(ranked, configuration);

            RecordCandidates(entry, ranked, decision);

            return decision.Outcome switch
            {
                DecisionOutcome.AutoAssign => await AssignAsync(item, identity, entry, decision, configuration, policy, report, cancellationToken).ConfigureAwait(false),
                DecisionOutcome.Review => Queue(entry, decision, report),
                _ => NoCandidate(entry, decision, configuration, report),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ThemeForge: failed while processing \"{Item}\".", identity.Label);
            return Fail(entry, ex.Message, configuration, report);
        }
        finally
        {
            _index.Put(entry);
        }
    }

    /// <inheritdoc />
    public async Task<(bool Success, string Message)> ApplyThemeAsync(
        Guid itemId,
        string sourceUrl,
        ThemeItemState state,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return (false, "No source URL was supplied.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return (false, "That item is no longer in the library.");
        }

        var identity = _identityResolver.Resolve(item);
        if (identity is null)
        {
            return (false, "ThemeForge only handles movies and series.");
        }

        var configuration = Plugin.Config;
        var entry = _index.Resolve(itemId, identity.StableKey) ?? NewEntry(identity);

        try
        {
            var candidate = await DescribeAsync(sourceUrl, cancellationToken).ConfigureAwait(false);

            // A human asking for this specific theme outranks whatever the library's rule says;
            // refusing to act on an explicit instruction would be the surprising behaviour.
            var overwriting = new ResolvedThemePolicy(true, ThemeOverwritePolicy.ReplaceAny, string.Empty);

            var audio = await _acquisitionEngine.AcquireAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);

            try
            {
                var placement = await _placementEngine.PlaceAsync(item, audio, configuration, overwriting, cancellationToken).ConfigureAwait(false);
                if (!placement.Success)
                {
                    entry.LastError = placement.Reason;
                    return (false, placement.Reason ?? "The theme could not be written.");
                }

                entry.State = state;
                entry.ChosenId = candidate.Id;
                entry.ChosenTitle = candidate.Title;
                entry.ChosenUrl = candidate.Url;
                entry.ChosenChannel = candidate.Channel;
                entry.ThemePath = placement.Path;
                entry.Sha256 = audio.Sha256;
                entry.LoudnessLufs = audio.MeasuredLoudnessLufs;
                entry.DurationSeconds = audio.DurationSeconds;
                entry.LastError = null;
                entry.NextRetryUtc = null;

                return (true, $"Theme set for \"{identity.Label}\".");
            }
            finally
            {
                CleanUpStaging(audio);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ThemeForge: could not apply {Url} to \"{Item}\".", sourceUrl, identity.Label);
            entry.LastError = ex.Message;
            return (false, ex.Message);
        }
        finally
        {
            _index.Put(entry);
            await _index.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a candidate for an explicit URL, filling in metadata where possible. A failed
    /// lookup is not fatal: the download only needs the URL, and the title is cosmetic.
    /// </summary>
    private async Task<Candidate> DescribeAsync(string sourceUrl, CancellationToken cancellationToken)
    {
        var stub = new Candidate
        {
            Id = ExtractVideoId(sourceUrl),
            Url = sourceUrl,
            Title = sourceUrl,
            FoundBy = new SearchQuery(sourceUrl, 0, "manual"),
        };

        try
        {
            var hydrated = await _candidateSource.HydrateAsync(new[] { stub }, cancellationToken).ConfigureAwait(false);
            return hydrated.Count > 0 ? hydrated[0] : stub;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not read metadata for {Url}; downloading it anyway.", sourceUrl);
            return stub;
        }
    }

    /// <summary>Pulls the video id out of a YouTube URL, falling back to the whole URL.</summary>
    internal static string ExtractVideoId(string url)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            url,
            @"(?:v=|youtu\.be/|/shorts/|/embed/)([A-Za-z0-9_-]{11})");

        return match.Success ? match.Groups[1].Value : url;
    }

    /// <inheritdoc />
    public void Dispose() => _throttle.Dispose();

    /// <summary>
    /// Walks the search ladder, stopping as soon as a candidate is good enough to assign.
    /// </summary>
    /// <remarks>
    /// Each rung is scored twice: once on the cheap flat listing to pick which candidates are
    /// worth a full metadata fetch, then again once that fetch has supplied duration, view count
    /// and channel. Scoring twice costs nothing and saves most of the network traffic.
    /// </remarks>
    private async Task<IReadOnlyList<ScoreResult>> SearchAndRankAsync(
        MediaIdentity identity,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var context = new ScoringContext
        {
            Identity = identity,
            Configuration = configuration,
            FindExistingAssignment = videoId => _index.FindAssignmentOwner(videoId, identity.ItemId),
        };

        var plan = _queryPlanner.Plan(identity, configuration);
        var best = new Dictionary<string, ScoreResult>(StringComparer.Ordinal);
        var autoAssign = Math.Max(configuration.AutoAssignThreshold, configuration.ReviewThreshold);

        foreach (var query in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _throttle.WaitAsync(TimeSpan.FromMilliseconds(configuration.RequestDelayMs), cancellationToken).ConfigureAwait(false);

            var found = await _candidateSource
                .SearchAsync(query, configuration.SearchResultsPerQuery, cancellationToken)
                .ConfigureAwait(false);

            if (found.Count == 0)
            {
                continue;
            }

            // Rank on titles alone, then spend a network round trip only on the shortlist.
            var shortlist = _scoringEngine.Rank(found, context)
                .Where(result => !result.IsVetoed)
                .Take(Math.Max(1, configuration.HydrateTopCandidates))
                .Select(result => result.Candidate)
                .ToList();

            if (shortlist.Count == 0)
            {
                continue;
            }

            await _throttle.WaitAsync(TimeSpan.FromMilliseconds(configuration.RequestDelayMs), cancellationToken).ConfigureAwait(false);
            var hydrated = await _candidateSource.HydrateAsync(shortlist, cancellationToken).ConfigureAwait(false);

            foreach (var result in _scoringEngine.Rank(hydrated, context))
            {
                if (!best.TryGetValue(result.Candidate.Id, out var existing) || result.Total > existing.Total)
                {
                    best[result.Candidate.Id] = result;
                }
            }

            if (configuration.StopLadderOnConfidentHit
                && best.Values.Any(result => !result.IsVetoed && result.Total >= autoAssign))
            {
                _logger.LogDebug("ThemeForge: stopping the search for \"{Item}\" early after a confident match.", identity.Label);
                break;
            }
        }

        return best.Values.OrderByDescending(result => result.Total).ToList();
    }

    private async Task<ItemOutcome> AssignAsync(
        BaseItem item,
        MediaIdentity identity,
        ThemeIndexEntry entry,
        ThemeDecision decision,
        PluginConfiguration configuration,
        ResolvedThemePolicy policy,
        RunReport report,
        CancellationToken cancellationToken)
    {
        var best = decision.Best!;

        if (configuration.DryRun)
        {
            entry.State = ThemeItemState.PendingReview;
            entry.LastError = null;
            _logger.LogInformation(
                "ThemeForge: [dry run] would assign \"{Candidate}\" to \"{Item}\" ({Reason}).",
                best.Candidate.Title,
                identity.Label,
                decision.Reason);
            return ItemOutcome.Queued;
        }

        entry.Attempts++;
        var audio = await _acquisitionEngine.AcquireAsync(best.Candidate, configuration, cancellationToken).ConfigureAwait(false);

        try
        {
            var placement = await _placementEngine.PlaceAsync(item, audio, configuration, policy, cancellationToken).ConfigureAwait(false);
            if (!placement.Success)
            {
                report.NoteReason(placement.Reason ?? "the theme could not be written");
                return Fail(entry, placement.Reason ?? "the theme could not be written", configuration, report);
            }

            entry.State = ThemeItemState.AutoAssigned;
            entry.ThemePath = placement.Path;
            entry.Sha256 = audio.Sha256;
            entry.LoudnessLufs = audio.MeasuredLoudnessLufs;
            entry.DurationSeconds = audio.DurationSeconds;
            entry.LastError = null;
            entry.NextRetryUtc = null;

            return ItemOutcome.Assigned;
        }
        finally
        {
            CleanUpStaging(audio);
        }
    }

    private ItemOutcome Queue(ThemeIndexEntry entry, ThemeDecision decision, RunReport report)
    {
        entry.State = ThemeItemState.PendingReview;
        entry.LastError = null;
        report.NoteReason("scored below the auto-assign threshold, so it needs review");
        _logger.LogDebug("ThemeForge: queued \"{Item}\" for review — {Reason}.", entry.Label, decision.Reason);
        return ItemOutcome.Queued;
    }

    private ItemOutcome NoCandidate(
        ThemeIndexEntry entry,
        ThemeDecision decision,
        PluginConfiguration configuration,
        RunReport report)
    {
        entry.State = ThemeItemState.Failed;
        entry.Attempts++;
        entry.LastError = decision.Reason;
        entry.NextRetryUtc = NextRetry(entry.Attempts, configuration);
        report.NoteReason(decision.Reason);
        return ItemOutcome.NoCandidate;
    }

    private ItemOutcome Fail(ThemeIndexEntry entry, string error, PluginConfiguration configuration, RunReport report)
    {
        entry.State = ThemeItemState.Failed;
        entry.LastError = error;
        entry.NextRetryUtc = NextRetry(entry.Attempts, configuration);
        report.NoteReason(error);
        return ItemOutcome.Failed;
    }

    /// <summary>
    /// Decides which items are worth looking at, and why an item should be left alone.
    /// </summary>
    /// <returns>A reason to skip, or null to process the item.</returns>
    private string? ShouldSkip(BaseItem item, ThemeIndexEntry entry, PluginConfiguration configuration, ResolvedThemePolicy policy)
    {
        // A human's decision always outranks the pipeline's.
        if (entry.IsSettledByHuman)
        {
            return string.Format(CultureInfo.InvariantCulture, "it is marked {0}", entry.State);
        }

        // A theme ThemeForge chose is only revisited when the library allows replacing one.
        if (entry.State is ThemeItemState.AutoAssigned or ThemeItemState.Approved
            && policy.Overwrite == ThemeOverwritePolicy.Never)
        {
            return "it already has a theme assigned by ThemeForge";
        }

        if (entry.NextRetryUtc is { } retryAt && retryAt > DateTime.UtcNow)
        {
            return string.Format(CultureInfo.InvariantCulture, "the retry backoff runs until {0:u}", retryAt);
        }

        if (entry.Attempts >= configuration.MaxAttempts && entry.State == ThemeItemState.Failed)
        {
            return string.Format(CultureInfo.InvariantCulture, "it has failed {0} times", entry.Attempts);
        }

        // A theme file already on disk that ThemeForge did not write belongs to the user, and is
        // only replaced when the library is explicitly set to replace anything.
        //
        // This is deliberately NOT recorded as a state. It used to latch the item to
        // ManualOverride, which is checked before the policy, so the item could never be
        // reconsidered no matter how the library rule changed afterwards. Whether a theme file
        // exists is a fact about right now; it is re-read on every run and judged against the
        // policy in force at the time.
        if (policy.Overwrite != ThemeOverwritePolicy.ReplaceAny
            && _placementEngine.HasExistingTheme(item, configuration))
        {
            return "it already has a theme that ThemeForge did not write, and this library is not set to replace those";
        }

        return null;
    }

    private IReadOnlyList<BaseItem> GetLibraryItems(PluginConfiguration configuration)
    {
        var kinds = new List<BaseItemKind>();
        if (configuration.ProcessSeries)
        {
            kinds.Add(BaseItemKind.Series);
        }

        if (configuration.ProcessMovies)
        {
            kinds.Add(BaseItemKind.Movie);
        }

        if (kinds.Count == 0)
        {
            return Array.Empty<BaseItem>();
        }

        return _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = kinds.ToArray(),
            Recursive = true,
            IsVirtualItem = false,
        });
    }

    private static ThemeIndexEntry NewEntry(MediaIdentity identity) => new()
    {
        ItemId = identity.ItemId,
        StableKey = identity.StableKey,
        Label = identity.Label,
        Kind = identity.Kind.ToString(),
        State = ThemeItemState.Unprocessed,
    };

    /// <summary>
    /// Records the chosen candidate and the runners-up, so the review queue can show alternatives
    /// and a later run can see what was already considered.
    /// </summary>
    private static void RecordCandidates(ThemeIndexEntry entry, IReadOnlyList<ScoreResult> ranked, ThemeDecision decision)
    {
        var best = decision.Best;
        if (best is not null)
        {
            entry.ChosenId = best.Candidate.Id;
            entry.ChosenTitle = best.Candidate.Title;
            entry.ChosenUrl = best.Candidate.Url;
            entry.ChosenChannel = best.Candidate.Channel;
            entry.Score = best.Total;
            entry.ScoreBreakdown = ScoringEngine.Describe(best).ToList();
        }

        entry.Rejected = ranked
            .Where(result => !ReferenceEquals(result, best))
            .Take(5)
            .Select(result => new RejectedCandidateRecord
            {
                Id = result.Candidate.Id,
                Title = result.Candidate.Title,
                Url = result.Candidate.Url,
                Score = result.Total,
                Reason = result.IsVetoed
                    ? result.VetoReason ?? "disqualified"
                    : string.Join("; ", result.TopSignals(2).Select(signal => signal.Reason)),
            })
            .ToList();
    }

    /// <summary>
    /// Backs off exponentially so a title with no theme on YouTube is not searched for nightly
    /// forever, while a transient network failure still gets retried soon.
    /// </summary>
    private static DateTime NextRetry(int attempts, PluginConfiguration configuration)
    {
        var hours = Math.Min(24 * 30, Math.Pow(2, Math.Clamp(attempts, 1, 10)) * 6);
        return DateTime.UtcNow.AddHours(hours);
    }

    private void CleanUpStaging(AcquiredAudio audio)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(audio.StagingPath);
            if (!string.IsNullOrEmpty(directory) && System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ThemeForge: could not clean up staging for {Path}.", audio.StagingPath);
        }
    }
}
