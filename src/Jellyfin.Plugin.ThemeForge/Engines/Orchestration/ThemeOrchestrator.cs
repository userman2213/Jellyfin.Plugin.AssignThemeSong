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
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
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
    /// Fetches every theme ThemeForge wrote again, from the source it was chosen from, and writes
    /// it with the current audio settings.
    /// </summary>
    /// <remarks>
    /// The way a change to how themes are written reaches the themes already in the library --
    /// every theme written before 2.3 was normalised and faded -- without losing a single
    /// decision. States, scores and review verdicts are untouched; only the files change.
    /// </remarks>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A summary of what happened.</returns>
    Task<RedownloadReport> RedownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Searches for themes for one item on demand, without deciding anything.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is how a wrong theme gets put right: the same search, the same rules and the same
    /// explanations an automated run uses, pointed at whatever words the administrator types, with
    /// every result handed back -- including the ones the rules reject, since those are precisely
    /// what somebody overriding the decision may be looking for.
    /// </para>
    /// <para>
    /// It lives here rather than in the controller because the throttle that keeps a run from
    /// being rate limited belongs to this class, and a search started from a settings page has to
    /// queue behind the same gate. Nothing is recorded: a search is a question, not an attempt, so
    /// it must not consume a retry or move an item out of the state it is in.
    /// </para>
    /// </remarks>
    /// <param name="itemId">The item to search for.</param>
    /// <param name="query">What to search for, or null to use the first rung of the item's own ladder.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates found, best first, and what the item already has.</returns>
    Task<ManualSearchResult> SearchForItemAsync(Guid itemId, string? query, CancellationToken cancellationToken);

    /// <summary>Gets the most recent re-download's report, if there has been one.</summary>
    RedownloadReport? LastRedownload { get; }

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

    /// <summary>
    /// Finds out who wrote the music for every title in the library, and keeps the answers.
    /// </summary>
    /// <remarks>
    /// Lives here because the question is asked about the library, which only this class
    /// enumerates. The scheduled task is a wrapper around it.
    /// </remarks>
    /// <param name="progress">Progress reporter, 0 to 100.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many titles were looked at, and how many of them somebody is now known for.</returns>
    Task<ComposerCoverage> SyncComposersAsync(IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>How much of the library ThemeForge knows the music of.</summary>
/// <param name="Titles">How many films and series were looked at.</param>
/// <param name="Known">How many of them somebody is credited on the music of.</param>
/// <param name="Themes">How many of them have a theme song known by name.</param>
public readonly record struct ComposerCoverage(int Titles, int Known, int Themes = 0)
{
    /// <summary>Gets how many titles nobody could be found for.</summary>
    public int Unknown => Titles - Known;
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
    private readonly IReadOnlyList<IThemeProvenanceSource> _provenanceSources;
    private readonly IThemerrDbCatalogue _themerrDb;
    private readonly IComposerCatalogue _composers;
    private readonly IScoringEngine _scoringEngine;
    private readonly IDecisionPolicy _decisionPolicy;
    private readonly IAcquisitionEngine _acquisitionEngine;
    private readonly IThemePlacementEngine _placementEngine;
    private readonly IThemeIndex _index;
    private readonly ILibraryPolicyResolver _policyResolver;
    private readonly IThemeForgeLogger<ThemeOrchestrator> _logger;
    private readonly RequestThrottle _throttle = new();

    /// <summary>Paces searches somebody is waiting for, separately from the run's.</summary>
    private readonly RequestThrottle _interactive = new();

    /// <summary>How many items are processed between index saves during a run.</summary>
    private const int IndexFlushInterval = 25;

    /// <summary>How many candidates discarded before hydration are remembered per item.</summary>
    private const int MaxOverlookedRecorded = 8;

    /// <summary>How many runners-up, seen or unseen, an index entry keeps.</summary>
    private const int MaxRejectedRecorded = 8;

    /// <summary>How many results a search asked for by hand returns.</summary>
    /// <remarks>
    /// Larger than a ladder rung fetches, because a person scanning a list wants to see past the
    /// first few, and the whole point of searching by hand is that the obvious answer was missed.
    /// </remarks>
    private const int ManualSearchResults = 20;

    /// <summary>How many of those results have their full metadata fetched.</summary>
    /// <remarks>
    /// Hydration is one request for the whole batch, so this is about how long the page waits
    /// rather than how many requests are made. The rest are shown as the listing described them
    /// and marked as not looked at closely.
    /// </remarks>
    private const int ManualSearchInspected = 12;

    /// <summary>How long an interactive search waits between requests.</summary>
    /// <remarks>
    /// A run paces itself at a second and a half because it makes thousands of requests over
    /// hours and being rate limited would end it. A person watching a spinner makes a few dozen
    /// and is waiting for them, so the same pacing would turn a six-phrasing search into forty
    /// seconds of nothing. Its own throttle keeps the two from sharing a queue.
    /// </remarks>
    private const int ManualSearchSpacingMs = 400;

    /// <summary>How many of a manual search's phrasings are in flight at once.</summary>
    /// <remarks>
    /// Safe because each search is a separate process: the candidate source holds only readonly
    /// fields and the process runner spawns one per call.
    /// </remarks>
    private const int ManualSearchConcurrency = 3;

    /// <summary>The longest query that will be sent to a search, in characters.</summary>
    private const int MaxQueryLength = 200;

    private int _running;

    /// <summary>Initializes a new instance of the <see cref="ThemeOrchestrator"/> class.</summary>
    /// <param name="libraryManager">Enumerates library items.</param>
    /// <param name="identityResolver">Resolves item identity.</param>
    /// <param name="queryPlanner">Plans the search ladder.</param>
    /// <param name="candidateSource">Finds candidates.</param>
    /// <param name="provenanceSources">Catalogues that answer by the item's own database id.</param>
    /// <param name="themerrDb">The local ThemerrDB copy, refreshed at the start of a run if stale.</param>
    /// <param name="composers">What research has established about who wrote each work's music.</param>
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
        IEnumerable<IThemeProvenanceSource> provenanceSources,
        IThemerrDbCatalogue themerrDb,
        IComposerCatalogue composers,
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
        // Ordered here, once, rather than depending on the order services were registered in.
        _provenanceSources = provenanceSources.OrderBy(source => source.Order).ToList();
        _themerrDb = themerrDb;
        _composers = composers;
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
    public RedownloadReport? LastRedownload { get; private set; }

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
            await RefreshCatalogueIfStaleAsync(configuration, cancellationToken).ConfigureAwait(false);
            await RefreshComposersIfStaleAsync(configuration, cancellationToken).ConfigureAwait(false);

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
    public async Task<RedownloadReport> RedownloadAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 1)
        {
            throw new InvalidOperationException("A ThemeForge run is already in progress.");
        }

        var configuration = Plugin.Config.ShallowCopy();

        // Every file here passed the audio check when it was chosen, and every file replaced is
        // ThemeForge's own, verified by hash a moment before -- so there is nothing to listen for
        // again and nothing worth a backup copy.
        configuration.RejectNonMusic = false;
        configuration.BackupExistingThemes = false;

        var report = new RedownloadReport();
        LastRedownload = report;

        try
        {
            await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

            var entries = _index.All()
                .Where(entry => entry.ThemePath is not null
                    && entry.Sha256 is not null
                    && !string.IsNullOrWhiteSpace(entry.ChosenUrl))
                .ToList();
            report.Considered = entries.Count;
            _logger.LogInformation("ThemeForge: re-downloading {Count} themes with the current audio settings.", entries.Count);

            var processed = 0;
            using var concurrency = new SemaphoreSlim(Math.Max(1, configuration.MaxConcurrency));

            var work = entries.Select(async entry =>
            {
                await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    var outcome = await RedownloadOneAsync(entry, configuration, report, cancellationToken).ConfigureAwait(false);
                    lock (report)
                    {
                        report.Record(outcome);
                    }
                }
                finally
                {
                    concurrency.Release();
                    var done = Interlocked.Increment(ref processed);
                    progress?.Report(entries.Count == 0 ? 100 : 100.0 * done / entries.Count);

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
            _logger.LogInformation("ThemeForge: the re-download was cancelled.");
        }
        catch (Exception ex)
        {
            report.FatalError = ex.Message;
            _logger.LogError(ex, "ThemeForge: the re-download stopped early.");
        }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            await _index.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            Interlocked.Exchange(ref _running, 0);
            _logger.LogInformation("ThemeForge: re-download finished — {Summary}", report.ToString());
        }

        return report;
    }

    private async Task<RedownloadOutcome> RedownloadOneAsync(
        ThemeIndexEntry entry,
        PluginConfiguration configuration,
        RedownloadReport report,
        CancellationToken cancellationToken)
    {
        var path = entry.ThemePath!;
        if (!System.IO.File.Exists(path))
        {
            return RedownloadOutcome.Missing;
        }

        var actual = await FileHash.ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actual, entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            // Replaced by hand since it was written: somebody's choice, and not ours to redo.
            return RedownloadOutcome.KeptModified;
        }

        var item = _libraryManager.GetItemById(entry.ItemId);
        if (item is null)
        {
            NoteFailure(report, entry, "it is no longer in the library");
            return RedownloadOutcome.Failed;
        }

        try
        {
            await _throttle.WaitAsync(TimeSpan.FromMilliseconds(configuration.RequestDelayMs), cancellationToken).ConfigureAwait(false);

            // Everything about the source is already recorded, so there is no metadata to fetch.
            var url = entry.ChosenUrl!;
            var candidate = new Candidate
            {
                Id = entry.ChosenId ?? ExtractVideoId(url),
                Url = url,
                Title = entry.ChosenTitle ?? url,
                Channel = entry.ChosenChannel,
                FoundBy = new SearchQuery(url, 0, "re-download"),
                IsDirectAudio = LooksLikeDirectAudio(url),
                IsHydrated = true,
            };

            var audio = await _acquisitionEngine.AcquireAsync(candidate, configuration, cancellationToken).ConfigureAwait(false);

            try
            {
                // Our own file, verified a moment ago: the library's rule about existing themes
                // is about other people's files and does not apply to it.
                var replacing = new ResolvedThemePolicy(true, ThemeOverwritePolicy.ReplaceAny, string.Empty);
                var placement = await _placementEngine.PlaceAsync(item, audio, configuration, replacing, cancellationToken).ConfigureAwait(false);
                if (!placement.Success)
                {
                    NoteFailure(report, entry, placement.Reason ?? "the theme could not be written");
                    return RedownloadOutcome.Failed;
                }

                entry.ThemePath = placement.Path;
                entry.Sha256 = audio.Sha256;
                entry.LoudnessLufs = audio.MeasuredLoudnessLufs;
                entry.DurationSeconds = audio.DurationSeconds;
                entry.LastError = null;
                _index.Put(entry);

                _logger.LogInformation("ThemeForge: re-downloaded the theme for \"{Item}\" — {Treatment}.", entry.Label, audio.Treatment);
                return RedownloadOutcome.Redone;
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
            _logger.LogWarning(ex, "ThemeForge: could not re-download the theme for \"{Item}\" from {Url}.", entry.Label, entry.ChosenUrl);
            NoteFailure(report, entry, ex.Message);
            return RedownloadOutcome.Failed;
        }
    }

    private static void NoteFailure(RedownloadReport report, ThemeIndexEntry entry, string reason)
    {
        lock (report)
        {
            if (report.Failures.Count < 20)
            {
                report.Failures.Add($"{entry.Label}: {reason}");
            }
        }
    }

    /// <summary>Whether a URL points straight at an audio file rather than at a page to extract one from.</summary>
    internal static bool LooksLikeDirectAudio(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.AbsolutePath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public async Task<ManualSearchResult> SearchForItemAsync(Guid itemId, string? query, CancellationToken cancellationToken)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return ManualSearchResult.Failed("That item is no longer in the library.");
        }

        var identity = _identityResolver.Resolve(item);
        if (identity is null)
        {
            return ManualSearchResult.Failed("ThemeForge only handles movies and series.");
        }

        var configuration = Plugin.Config;
        var typed = ResolveQuery(query, () => null);
        var byHand = typed.Length > 0;

        // Nothing typed means "do what a run would do", so it does: the catalogues first, then
        // every phrasing of the ladder. Words typed mean the opposite -- somebody is overriding a
        // decision -- so those words are searched for and nothing else is put in front of them.
        var plan = byHand
            ? new[] { new SearchQuery(typed, 0, "manual search") }
            : _queryPlanner.Plan(identity, configuration);

        if (plan.Count == 0)
        {
            return ManualSearchResult.Failed("There is nothing to search for.");
        }

        var text = byHand ? typed : plan[0].Text;

        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        var entry = _index.Get(itemId);

        var context = new ScoringContext
        {
            Identity = identity,
            Configuration = configuration,
            FindExistingAssignment = videoId => _index.FindAssignmentOwner(videoId, identity.ItemId),
        };

        var listed = byHand ? null : await LookUpAsync(identity, configuration, cancellationToken).ConfigureAwait(false);

        var pooled = await SearchEveryPhrasingAsync(
            plan,
            byHand ? ManualSearchResults : configuration.SearchResultsPerQuery,
            identity.Label,
            cancellationToken).ConfigureAwait(false);

        // Seven phrasings can pool seventy videos, which is a wall rather than a list. Ranked
        // first, then cut: the cut is about what fits on a page, not about what was considered.
        IReadOnlyList<ScoreResult> results = pooled.Count == 0
            ? Array.Empty<ScoreResult>()
            : (await InspectAsync(pooled, context, cancellationToken).ConfigureAwait(false))
                .Take(ManualSearchResults)
                .ToList();

        // Counted before the catalogue's answer is added, because the sentence is about what the
        // search turned up rather than about how many rows are on the page.
        var summary = Describe(plan.Count, pooled.Count, results.Count, listed, text, byHand);

        // The catalogue's answer goes first and is not scored, exactly as in a run: it is keyed on
        // this title's own database id, so there is nothing to score and nothing to get wrong.
        if (listed is not null)
        {
            results = new[] { Certain(listed) }
                .Concat(results.Where(result => !string.Equals(result.Candidate.Id, listed.Id, StringComparison.Ordinal)))
                .ToList();
        }

        _logger.LogInformation(
            "ThemeForge: searched {Phrasings} phrasing(s) for \"{Item}\" -- {Found} distinct results, {Usable} of them usable{Listed}.",
            plan.Count,
            identity.Label,
            pooled.Count,
            results.Count(result => !result.IsVetoed),
            listed is null ? string.Empty : $", plus the {listed.Provenance} entry");

        return new ManualSearchResult(
            results.Count > 0,
            summary,
            text,
            entry?.ChosenId,
            entry?.ChosenTitle,
            entry?.ChosenUrl,
            results);
    }

    /// <summary>
    /// Runs every phrasing and pools what they find, keeping each video once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pooling before choosing what to inspect is the point, and it is something a run does not do.
    /// A run takes the best few of each rung separately, so a video that comes sixth on two
    /// different phrasings is never looked at although it would be near the top of the pooled set.
    /// Here every phrasing's results are put together first and the best twelve of the lot are
    /// fetched in one request.
    /// </para>
    /// <para>
    /// A video found by two phrasings keeps the more specific one. The specificity bonus is worth
    /// real points, and "found by the phrasing naming the composer" is the truer description of a
    /// video that also happens to turn up under the vaguest rung.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Candidate>> SearchEveryPhrasingAsync(
        IReadOnlyList<SearchQuery> plan,
        int perQuery,
        string label,
        CancellationToken cancellationToken)
    {
        var spacing = TimeSpan.FromMilliseconds(ManualSearchSpacingMs);
        using var concurrency = new SemaphoreSlim(ManualSearchConcurrency);

        var searches = plan.Select(async query =>
        {
            await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _interactive.WaitAsync(spacing, cancellationToken).ConfigureAwait(false);
                return await _candidateSource.SearchAsync(query, perQuery, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One phrasing failing is not the search failing: the others still have answers.
                _logger.LogWarning(ex, "ThemeForge: the search for \"{Query}\" failed while looking for \"{Item}\".", query.Text, label);
                return Array.Empty<Candidate>();
            }
            finally
            {
                concurrency.Release();
            }
        });

        return Pool(await Task.WhenAll(searches).ConfigureAwait(false));
    }

    /// <summary>
    /// Puts several phrasings' results together, keeping each video once.
    /// </summary>
    /// <remarks>
    /// A video found by two phrasings keeps the more specific one, which is the lower rank. The
    /// specificity bonus is worth real points, and "found by the phrasing naming the composer" is
    /// the truer description of a video that also happens to turn up under the vaguest rung.
    /// </remarks>
    /// <param name="batches">What each phrasing found.</param>
    /// <returns>Every distinct video, each attributed to the most specific phrasing that found it.</returns>
    internal static IReadOnlyList<Candidate> Pool(IEnumerable<IReadOnlyList<Candidate>> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);

        var pooled = new Dictionary<string, Candidate>(StringComparer.Ordinal);

        foreach (var found in batches)
        {
            foreach (var candidate in found)
            {
                if (!pooled.TryGetValue(candidate.Id, out var seen) || candidate.FoundBy.Rank < seen.FoundBy.Rank)
                {
                    pooled[candidate.Id] = candidate;
                }
            }
        }

        return pooled.Values.ToList();
    }

    /// <summary>Says in one line what the search actually did, since none of it is otherwise visible.</summary>
    private static string Describe(int phrasings, int pooled, int shown, Candidate? listed, string text, bool byHand)
    {
        if (shown == 0)
        {
            return listed is null
                ? $"Nothing found for \u201c{text}\u201d."
                : $"{listed.Provenance} has a theme for this title. Nothing else was found for \u201c{text}\u201d.";
        }

        var searched = byHand
            ? $"Searched for \u201c{text}\u201d"
            : string.Create(CultureInfo.InvariantCulture, $"Searched {phrasings} phrasing{(phrasings == 1 ? string.Empty : "s")}, starting with \u201c{text}\u201d");

        var looked = Math.Min(pooled, ManualSearchInspected);
        var found = string.Create(
            CultureInfo.InvariantCulture,
            $"{searched} \u2014 {pooled} result{(pooled == 1 ? string.Empty : "s")}, the best {shown} shown, the top {looked} looked at closely.");

        return listed is null ? found : $"{listed.Provenance} has a theme for this title; it is first below. {found}";
    }

    /// <summary>
    /// Scores a listing, fetches the full metadata of the most promising part of it, and scores
    /// that again.
    /// </summary>
    /// <remarks>
    /// The second pass is not a formality: hydration decides verdicts in both directions. A
    /// rights-holder upload titled by track name anchors on its album and stops being rejected,
    /// and a listing whose duration was unknown can turn out to be an hour long. Whatever the
    /// fuller picture says is what is shown.
    /// </remarks>
    private async Task<IReadOnlyList<ScoreResult>> InspectAsync(
        IReadOnlyList<Candidate> found,
        ScoringContext context,
        CancellationToken cancellationToken)
    {
        var ranked = _scoringEngine.Rank(found, context);

        // Taken in ranked order, vetoed candidates included rather than skipped: a veto on a flat
        // listing is often only the absence of the very metadata this fetch supplies.
        var shortlist = ranked.Take(ManualSearchInspected).Select(result => result.Candidate).ToList();

        // One request for the whole pooled shortlist, rather than one per phrasing as a run does.
        await _interactive.WaitAsync(TimeSpan.FromMilliseconds(ManualSearchSpacingMs), cancellationToken).ConfigureAwait(false);
        var hydrated = await _candidateSource.HydrateAsync(shortlist, cancellationToken).ConfigureAwait(false);

        return Merge(ranked, _scoringEngine.Rank(hydrated, context));
    }

    /// <summary>
    /// Combines what the listing said with what the full metadata said, preferring the latter.
    /// </summary>
    /// <remarks>
    /// Tolerant of the same video appearing twice on either side. It used to build a dictionary
    /// with <c>ToDictionary</c>, which throws on a duplicate key -- so a batch in which yt-dlp
    /// returned one video twice faulted the whole search and the page showed an error instead of
    /// results. The run's equivalent has always used <c>TryGetValue</c> and cannot.
    /// </remarks>
    /// <param name="ranked">Every candidate, scored on the listing alone.</param>
    /// <param name="inspected">The subset whose metadata was fetched, scored again.</param>
    /// <returns>Every candidate once, best first; a veto scores zero and so falls to the bottom.</returns>
    internal static IReadOnlyList<ScoreResult> Merge(
        IReadOnlyList<ScoreResult> ranked,
        IReadOnlyList<ScoreResult> inspected)
    {
        ArgumentNullException.ThrowIfNull(ranked);
        ArgumentNullException.ThrowIfNull(inspected);

        var better = new Dictionary<string, ScoreResult>(StringComparer.Ordinal);
        foreach (var result in inspected)
        {
            if (!better.TryGetValue(result.Candidate.Id, out var seen) || result.Total > seen.Total)
            {
                better[result.Candidate.Id] = result;
            }
        }

        var shown = new HashSet<string>(StringComparer.Ordinal);

        return ranked
            .Select(result => better.TryGetValue(result.Candidate.Id, out var full) ? full : result)
            .Where(result => shown.Add(result.Candidate.Id))
            .OrderByDescending(result => result.Total)
            .ThenBy(result => result.Candidate.Title, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Works out what to search for: what was typed, or failing that whatever the caller offers.
    /// </summary>
    /// <remarks>
    /// The cap is not about safety -- arguments reach yt-dlp as an array and never through a
    /// shell, so any character is merely text -- but about not sending a paragraph to a search
    /// engine that will make nothing of it.
    /// </remarks>
    /// <param name="query">What was typed, which may be nothing.</param>
    /// <param name="fallback">Supplies the query to use when nothing was typed.</param>
    /// <returns>The query text, trimmed and capped, or an empty string when there is none.</returns>
    internal static string ResolveQuery(string? query, Func<string?> fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);

        var text = query?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            text = fallback()?.Trim();
        }

        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length > MaxQueryLength ? text[..MaxQueryLength].Trim() : text;
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

        // A dry run is supposed to answer "what would this do?" without doing any of it. That has
        // to include the index: recording a decision it did not act on left the review queue full
        // of items the run would have assigned outright, showing the full score they earned, and
        // they stayed there after dry run was switched off again.
        var dryRun = configuration.DryRun;

        // Three gates, in this order, and the order is the point. Whether a theme may be written at
        // all is decided first and stops everything. The catalogues come next and are never behind
        // a backoff: a lookup keyed on the item's own id costs nothing for a miss, and the backoff
        // exists to stop a *search* that keeps failing from running nightly forever. It used to sit
        // in front of the catalogue too, so every item that had once failed -- a stale yt-dlp, or
        // simply nothing acceptable on YouTube -- was invisible to ThemerrDB for up to thirty days.
        var foreignTheme = policy.Overwrite != ThemeOverwritePolicy.ReplaceAny
            && entry.ThemePath is null
            && _placementEngine.HasExistingTheme(item, configuration);
        var eligibility = Eligibility.Decide(entry, policy, foreignTheme, configuration, DateTime.UtcNow);

        try
        {
            if (!eligibility.CatalogueAllowed)
            {
                return Skip(entry, identity, eligibility.StopReason!, dryRun);
            }

            if (eligibility.RetriedAfterUpgrade)
            {
                _logger.LogInformation(
                    "ThemeForge: \"{Item}\" was last decided by an earlier version of the matcher; trying again regardless of its backoff.",
                    identity.Label);
            }

            // Gate two: the catalogues. A catalogue keyed on the item's own TVDB or TMDB id
            // answers "the theme for this work"; a search answers "videos whose titles look
            // right". Scoring the first against the second's yardstick could only make it worse,
            // so a hit is applied on where it came from and nothing else runs.
            var known = await LookUpAsync(identity, configuration, cancellationToken).ConfigureAwait(false);
            if (known is not null)
            {
                var certain = new ThemeDecision(
                    DecisionOutcome.AutoAssign,
                    Certain(known),
                    $"listed in {known.Provenance} against this title's own database id");

                if (dryRun)
                {
                    return WouldAssign(identity, certain);
                }

                Begin(entry);
                RecordCandidates(entry, new[] { certain.Best! }, certain, Array.Empty<ScoreResult>());
                return await AssignAsync(item, identity, entry, certain, configuration, policy, report, cancellationToken).ConfigureAwait(false);
            }

            // Gate three: only the search is subject to backoff and attempt limits.
            if (!eligibility.SearchAllowed)
            {
                return Skip(entry, identity, eligibility.SearchDeferral!, dryRun);
            }

            entry.LastSkipReason = null;
            entry.LastSkipUtc = null;

            if (!dryRun)
            {
                Begin(entry);
            }

            var (ranked, overlooked) = await SearchAndRankAsync(identity, configuration, cancellationToken).ConfigureAwait(false);
            var decision = _decisionPolicy.Decide(ranked, configuration);

            if (dryRun)
            {
                return decision.Outcome == DecisionOutcome.AutoAssign
                    ? WouldAssign(identity, decision)
                    : Report(identity, decision, report);
            }

            RecordCandidates(entry, ranked, decision, overlooked);

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

            if (dryRun)
            {
                _logger.LogInformation("ThemeForge: [dry run] \"{Item}\" would have failed: {Error}.", identity.Label, ex.Message);
                return ItemOutcome.Failed;
            }

            return Fail(entry, ex.Message, configuration, report);
        }
        finally
        {
            if (!dryRun)
            {
                _index.Put(entry);
            }
        }
    }

    /// <summary>
    /// Marks the start of one genuine attempt at an item: the generation of the matcher deciding
    /// it, and one more try against the limit. Exactly once per processed item, so a search that
    /// throws counts the same as one that finds nothing.
    /// </summary>
    private static void Begin(ThemeIndexEntry entry)
    {
        entry.DecidedByMatcher = ThemeIndexEntry.CurrentMatcher;
        entry.Attempts++;
    }

    /// <summary>Leaves an item alone this run, and says why where the user will look.</summary>
    private ItemOutcome Skip(ThemeIndexEntry entry, MediaIdentity identity, string reason, bool dryRun)
    {
        // Recorded rather than only logged: "why was this skipped" is the first question asked
        // when a settings change appears to do nothing, and the log is not where people look.
        if (!dryRun)
        {
            entry.LastSkipReason = reason;
            entry.LastSkipUtc = DateTime.UtcNow;
        }

        _logger.LogDebug("ThemeForge: skipping \"{Item}\" — {Reason}.", identity.Label, reason);
        return ItemOutcome.Skipped;
    }

    /// <summary>Notes an item a real run would have assigned, without recording anything.</summary>
    private ItemOutcome WouldAssign(MediaIdentity identity, ThemeDecision decision)
    {
        _logger.LogInformation(
            "ThemeForge: [dry run] would assign \"{Candidate}\" to \"{Item}\" ({Reason}).",
            decision.Best!.Candidate.Title,
            identity.Label,
            decision.Reason);

        return ItemOutcome.WouldAssign;
    }

    /// <summary>Notes what a real run would have done with an item it could not assign.</summary>
    private ItemOutcome Report(MediaIdentity identity, ThemeDecision decision, RunReport report)
    {
        report.NoteReason(decision.Reason);
        _logger.LogInformation(
            "ThemeForge: [dry run] \"{Item}\" — {Reason}.",
            identity.Label,
            decision.Reason);

        return decision.Outcome == DecisionOutcome.Review ? ItemOutcome.Queued : ItemOutcome.NoCandidate;
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

            // The same applies to the audio check. Someone who has listened to a clip and asked
            // for it does not need to be told it does not sound like music.
            var asRequested = configuration.ShallowCopy();
            asRequested.RejectNonMusic = false;

            var audio = await _acquisitionEngine.AcquireAsync(candidate, asRequested, cancellationToken).ConfigureAwait(false);

            try
            {
                var placement = await _placementEngine.PlaceAsync(item, audio, configuration, overwriting, cancellationToken).ConfigureAwait(false);
                if (!placement.Success)
                {
                    entry.LastError = placement.Reason;
                    return (false, placement.Reason ?? "The theme could not be written.");
                }

                SettleScoreForNewChoice(entry, candidate.Id, DateTime.UtcNow);

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
    /// Discards a score that no longer describes the theme an item has.
    /// </summary>
    /// <remarks>
    /// A score belongs to the candidate that earned it. Assigning a different video used to leave
    /// the old number sitting beside the new theme in the library view, and the newly chosen video
    /// listed underneath as a runner-up that had been passed over -- both plainly wrong, and both
    /// far more visible now that changing a theme by hand is a normal thing to do. Approving the
    /// candidate that was already proposed changes nothing, so its score survives.
    /// </remarks>
    /// <param name="entry">The index entry about to be updated.</param>
    /// <param name="chosenId">The source id of the video now being assigned.</param>
    /// <param name="nowUtc">The current time, for the note left in place of the breakdown.</param>
    internal static void SettleScoreForNewChoice(ThemeIndexEntry entry, string chosenId, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Compared on the id, not the URL: the same video is reached by several forms of address
        // and DescribeAsync has already reduced them all to one id.
        if (string.Equals(entry.ChosenId, chosenId, StringComparison.Ordinal))
        {
            return;
        }

        entry.Score = null;
        entry.ScoreBreakdown = new List<string>
        {
            string.Format(CultureInfo.InvariantCulture, "Chosen by hand on {0:yyyy-MM-dd}.", nowUtc),
        };

        entry.Rejected = entry.Rejected
            .Where(rejected => !string.Equals(rejected.Id, chosenId, StringComparison.Ordinal))
            .ToList();
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
    public void Dispose()
    {
        _throttle.Dispose();
        _interactive.Dispose();
    }

    /// <summary>
    /// Brings the local ThemerrDB copy up to date before a run if the scheduled task has not.
    /// </summary>
    /// <remarks>
    /// The daily task is the normal path. This covers a server that was switched off when the task
    /// was due, and a fresh install that would otherwise spend its first scan asking the database
    /// about every title one at a time.
    /// </remarks>
    private async Task RefreshCatalogueIfStaleAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.UseThemerrDb || !configuration.SyncThemerrDb)
        {
            return;
        }

        try
        {
            var snapshot = await _themerrDb.GetAsync(cancellationToken).ConfigureAwait(false);
            var maxAge = TimeSpan.FromDays(Math.Max(1, configuration.ThemerrDbMaxAgeDays));

            if (snapshot.IsUsable && snapshot.Age < maxAge)
            {
                return;
            }

            _logger.LogInformation(
                "ThemeForge: the ThemerrDB catalogue is {State}; refreshing it before the run.",
                snapshot.IsUsable ? $"{snapshot.Age.TotalDays:0.#} days old" : "empty");

            await _themerrDb.SyncAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A stale catalogue costs requests, not correctness: the run still works without it.
            _logger.LogWarning(ex, "ThemeForge: could not refresh the ThemerrDB catalogue before the run.");
        }
    }

    /// <inheritdoc />
    public async Task<ComposerCoverage> SyncComposersAsync(IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var configuration = Plugin.Config.ShallowCopy();
        var works = await ComposerRequestsAsync(configuration, cancellationToken).ConfigureAwait(false);

        if (works.Count > 0)
        {
            await _composers.SyncAsync(works, progress, cancellationToken).ConfigureAwait(false);
        }

        var snapshot = await _composers.GetAsync(cancellationToken).ConfigureAwait(false);
        var known = works.Count(work => snapshot.Find(work.Keys)?.Found == true);
        var themes = works.Count(work => !string.IsNullOrWhiteSpace(snapshot.Find(work.Keys)?.ThemeTitle));

        progress?.Report(100);
        return new ComposerCoverage(works.Count, known, themes);
    }

    /// <summary>Turns the library into the questions the credits sources can be asked.</summary>
    /// <remarks>
    /// <para>
    /// ThemerrDB comes first, so a title it has a theme for is left out: that theme is taken
    /// outright and nothing has to be searched for, which makes looking up what the theme is called
    /// and who wrote it a question nobody needs the answer to. On a library ThemerrDB covers well
    /// this is most of it, and every one left out is a page not fetched.
    /// </para>
    /// <para>
    /// Items with no provider ids are left out too. No source can be asked about a title by name
    /// -- a title search returns the 1978 composer for the 2004 Battlestar Galactica -- so
    /// including them would only mean asking questions nothing can answer.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<CreditsRequest>> ComposerRequestsAsync(
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        // Only consulted when ThemerrDB is switched on: with it off it gives nothing, so nothing it
        // lists should be skipped. An unsynced snapshot lists nothing and skips nothing either.
        var catalogue = configuration.UseThemerrDb
            ? await _themerrDb.GetAsync(cancellationToken).ConfigureAwait(false)
            : null;

        var works = new List<CreditsRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var covered = 0;

        foreach (var item in GetLibraryItems(configuration))
        {
            var identity = _identityResolver.Resolve(item);
            if (identity is null)
            {
                continue;
            }

            var keys = CreditsKeys.For(identity.ImdbId, identity.TmdbId, identity.TvdbId, identity.IsSeries);
            if (keys.Count == 0 || !seen.Add(keys[0]))
            {
                continue;
            }

            if (catalogue is not null && HasThemerrTheme(catalogue, identity))
            {
                covered++;
                continue;
            }

            works.Add(new CreditsRequest(
                keys[0],
                keys,
                identity.ImdbId,
                identity.TmdbId,
                identity.IsSeries,
                identity.Label));
        }

        if (covered > 0)
        {
            _logger.LogInformation(
                "{Covered} titles already have a theme in ThemerrDB and are not looked up; "
                + "{Remaining} are.",
                covered,
                works.Count);
        }

        return works;
    }

    /// <summary>Reports whether ThemerrDB has a theme for a title, by any id it is known by.</summary>
    internal static bool HasThemerrTheme(ThemerrDbSnapshot catalogue, MediaIdentity identity)
    {
        if (identity.IsSeries)
        {
            return catalogue.HasShow(identity.TmdbId);
        }

        return catalogue.HasMovie(identity.TmdbId)
            || catalogue.HasMovieByImdb(identity.ImdbId)
            || catalogue.CollectionContaining(identity.TmdbId) is not null;
    }

    /// <summary>
    /// Fills the composer cache before a run when it has gone stale.
    /// </summary>
    /// <remarks>
    /// The daily task is the normal path. This covers a server that was switched off when the task
    /// was due, and a fresh install whose first run would otherwise search without the one name
    /// that most sharply identifies a theme.
    /// </remarks>
    private async Task RefreshComposersIfStaleAsync(PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (!configuration.ResearchComposers || !configuration.SyncComposers)
        {
            return;
        }

        try
        {
            var snapshot = await _composers.GetAsync(cancellationToken).ConfigureAwait(false);
            var maxAge = TimeSpan.FromDays(Math.Max(1, configuration.ComposerCacheMaxAgeDays));

            if (snapshot.IsUsable && snapshot.Age < maxAge)
            {
                return;
            }

            _logger.LogInformation(
                "ThemeForge: the music credits cache is {State}; filling it before the run.",
                snapshot.IsUsable ? $"{snapshot.Age.TotalDays:0.#} days old" : "empty");

            await SyncComposersAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Not knowing the composer costs specificity, not correctness: the run still works.
            _logger.LogWarning(ex, "ThemeForge: could not look up the music credits before the run.");
        }
    }

    /// <summary>
    /// Asks each enabled catalogue whether it holds the theme for this exact work.
    /// </summary>
    /// <remarks>
    /// One request per catalogue per item, and only for items that carry the id the catalogue is
    /// keyed on. A catalogue that is unreachable or has nothing returns nothing, and the search
    /// ladder runs as it always did.
    /// </remarks>
    private async Task<Candidate?> LookUpAsync(
        MediaIdentity identity,
        PluginConfiguration configuration,
        CancellationToken cancellationToken)
    {
        foreach (var source in _provenanceSources)
        {
            if (!source.IsEnabled(configuration))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();

            Candidate? found;
            try
            {
                found = await source.FindAsync(identity, configuration, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A catalogue failing is not this item's failure: the search still runs.
                _logger.LogWarning(ex, "ThemeForge: {Source} failed for \"{Item}\".", source.Name, identity.Label);
                continue;
            }

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps a catalogue answer as a full-marks result, so the index and the review queue can
    /// describe it the same way they describe everything else.
    /// </summary>
    private static ScoreResult Certain(Candidate candidate) => new()
    {
        Candidate = candidate,
        Total = 100,
        Breakdown = new[]
        {
            new Signal(
                "Provenance",
                1,
                100,
                $"listed in {candidate.Provenance} against this title's own database id, so it was not scored"),
        },
    };

    /// <summary>
    /// Walks the search ladder, stopping as soon as a candidate is good enough to assign.
    /// </summary>
    /// <remarks>
    /// Each rung is scored twice: once on the cheap flat listing to pick which candidates are
    /// worth a full metadata fetch, then again once that fetch has supplied duration, view count
    /// and channel. Scoring twice costs nothing and saves most of the network traffic.
    /// </remarks>
    private async Task<(IReadOnlyList<ScoreResult> Ranked, IReadOnlyList<ScoreResult> Overlooked)> SearchAndRankAsync(
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
        var overlooked = new Dictionary<string, ScoreResult>(StringComparer.Ordinal);
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
            var flat = _scoringEngine.Rank(found, context);
            var shortlist = flat
                .Where(result => !result.IsVetoed)
                .Take(Math.Max(1, configuration.HydrateTopCandidates))
                .Select(result => result.Candidate)
                .ToList();

            // A candidate vetoed here is never hydrated and used to vanish without a trace, so the
            // one question that matters afterwards -- "why was the right video not used?" -- had
            // no answer. Kept, so the index can say what was discarded unseen and why.
            foreach (var result in flat.Where(result => result.IsVetoed))
            {
                if (overlooked.Count < MaxOverlookedRecorded)
                {
                    overlooked.TryAdd(result.Candidate.Id, result);
                }
            }

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

        var ranked = best.Values.OrderByDescending(result => result.Total).ToList();
        var unseen = overlooked.Values.Where(result => !best.ContainsKey(result.Candidate.Id)).ToList();
        return (ranked, unseen);
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
        // Dry runs never reach here: they are decided in ProcessItemAsync, before anything is
        // recorded. This method's job is to act, and everything it does is a write.
        var best = decision.Best!;

        var audio = await _acquisitionEngine.AcquireAsync(best.Candidate, configuration, cancellationToken).ConfigureAwait(false);

        try
        {
            entry.BandDiffStd = audio.Assessment?.BandDiffStd;

            if (audio.NeedsReview)
            {
                // The download sounded like neither music nor speech. It is not thrown away and
                // not written either: it goes to the queue so a person can listen, and the staged
                // file is discarded because approving it re-fetches anyway.
                entry.State = ThemeItemState.PendingReview;
                entry.LastError = null;
                report.NoteReason(audio.Assessment!.Reason);
                _logger.LogInformation(
                    "ThemeForge: holding \"{Item}\" for review — {Reason}.",
                    identity.Label,
                    audio.Assessment.Reason);
                return ItemOutcome.Queued;
            }

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

            report.NoteAssignedBy(best.Candidate.Provenance ?? "search");
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

        // A stale backoff on a review item skipped it before anyone looked at it again.
        entry.NextRetryUtc = null;
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
        // Its own state, not Failed: nothing broke, the search simply found nothing acceptable.
        // The two used to be indistinguishable, so "cannot be mapped" read as "download failed".
        entry.State = ThemeItemState.NoCandidate;
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
    private static void RecordCandidates(
        ThemeIndexEntry entry,
        IReadOnlyList<ScoreResult> ranked,
        ThemeDecision decision,
        IReadOnlyList<ScoreResult> overlooked)
    {
        entry.DecidedByMatcher = ThemeIndexEntry.CurrentMatcher;

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

        var seen = ranked
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
            });

        // Candidates discarded on their title before hydration, marked as such: they were never
        // inspected, and knowing that is the difference between "the search never found it" and
        // "the search found it and the matcher threw it away".
        var unseen = overlooked
            .Select(result => new RejectedCandidateRecord
            {
                Id = result.Candidate.Id,
                Title = result.Candidate.Title,
                Url = result.Candidate.Url,
                Score = result.Total,
                Reason = "not inspected — " + (result.VetoReason ?? "disqualified on its title"),
            });

        entry.Rejected = seen.Concat(unseen).Take(MaxRejectedRecorded).ToList();
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
