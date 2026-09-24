using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>The local record of who wrote what, and what each theme is called.</summary>
public interface IComposerCatalogue
{
    /// <summary>Gets the cache in memory, loading it from disk on first use.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache, which may hold nothing if nothing has been looked up yet.</returns>
    Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Looks up whatever is not already known about these works, and saves what comes back.
    /// </summary>
    /// <param name="works">Everything worth knowing about.</param>
    /// <param name="progress">Progress reporter, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The cache as it now stands.</returns>
    Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken);

    /// <summary>
    /// Reads what is already known about one work, without asking anybody.
    /// </summary>
    /// <remarks>
    /// Synchronous on purpose. Identity is resolved synchronously all through the pipeline, and
    /// this has to be answerable from there; the cache is loaded once and read from memory, which
    /// is the same bargain the ThemerrDB mirror makes.
    /// </remarks>
    /// <param name="keys">The provider keys for the work.</param>
    /// <returns>What is known, or nothing.</returns>
    ResearchedCredits Known(IReadOnlyList<string> keys);
}

/// <summary>
/// Keeps what has been found out about each work's music.
/// </summary>
/// <remarks>
/// <para>
/// Built on the same bargain as the ThemerrDB mirror: ask once, keep the answer beside the index
/// so it survives upgrades, read it from memory thereafter, and treat a failed refresh as a reason
/// to keep yesterday's copy rather than to lose it. A sync works on a copy and swaps it in at the
/// end, so a search reading the cache meanwhile never sees it half-changed.
/// </para>
/// <para>
/// Two questions are asked -- who wrote the music, and what the theme is called -- and each is
/// settled separately. A question is settled when a source answers it, or when every source that
/// could answer it was asked and none did. A question some source could not ask about, because it
/// was down or refused, is left open and asked again next time. Recording it as "nobody knows"
/// instead would hide the work for a fortnight for a reason that had nothing to do with the work,
/// and until 2.6 that is exactly what a Wikidata outage did to an entire library.
/// </para>
/// </remarks>
public sealed class ComposerCatalogue : IComposerCatalogue
{
    private readonly IReadOnlyList<ICreditsSource> _sources;
    private readonly IThemeForgeLogger<ComposerCatalogue> _logger;
    private readonly string? _snapshotPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Guards the first read from disk only, which is why it is not the sync gate.</summary>
    /// <remarks>
    /// <see cref="Known"/> is called from identity resolution, on whatever thread is serving a
    /// page. Were it to wait on <see cref="_gate"/> it would block behind a slow pass that
    /// can run for minutes. Loading a file is short and has its own lock.
    /// </remarks>
    private readonly object _load = new();

    private volatile ComposerSnapshot? _snapshot;

    /// <summary>Initializes a new instance of the <see cref="ComposerCatalogue"/> class.</summary>
    /// <param name="sources">The databases to ask, in whatever order they declare.</param>
    /// <param name="logger">Logger.</param>
    public ComposerCatalogue(IEnumerable<ICreditsSource> sources, IThemeForgeLogger<ComposerCatalogue> logger)
        : this(sources, logger, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ComposerCatalogue"/> class, keeping its cache somewhere specific.</summary>
    /// <param name="sources">The databases to ask, in whatever order they declare.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="snapshotPath">Where the cache is kept, or null for beside the index.</param>
    internal ComposerCatalogue(IEnumerable<ICreditsSource> sources, IThemeForgeLogger<ComposerCatalogue> logger, string? snapshotPath)
    {
        ArgumentNullException.ThrowIfNull(sources);

        // Ordered here, once, rather than depending on the order services were registered in.
        _sources = sources.OrderBy(source => source.Order).ToList();
        _logger = logger;
        _snapshotPath = snapshotPath;
    }

    /// <summary>Gets where the cache is kept, beside the index so it survives plugin upgrades.</summary>
    public static string SnapshotPath =>
        Path.Combine(Plugin.Instance?.DataPath ?? Path.GetTempPath(), "composers.json");

    private string ActualPath => _snapshotPath ?? SnapshotPath;

    /// <inheritdoc />
    public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Loaded());
    }

    /// <inheritdoc />
    public ResearchedCredits Known(IReadOnlyList<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);

        if (keys.Count == 0)
        {
            return ResearchedCredits.None;
        }

        var found = Loaded().Find(keys);
        return found?.AsCredits() ?? ResearchedCredits.None;
    }

    /// <summary>Returns the cache, reading it from disk the first time anybody asks.</summary>
    private ComposerSnapshot Loaded()
    {
        var snapshot = _snapshot;
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (_load)
        {
            return _snapshot ??= Read() ?? new ComposerSnapshot();
        }
    }

    /// <inheritdoc />
    public async Task<ComposerSnapshot> SyncAsync(
        IReadOnlyList<CreditsRequest> works,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(works);

        var configuration = Plugin.Config;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Loaded();
            var snapshot = current.Clone();
            var now = DateTime.UtcNow;

            var findings = new Dictionary<string, Findings>(StringComparer.OrdinalIgnoreCase);
            foreach (var work in works)
            {
                var needs = CreditsQuestion.None;
                if (snapshot.NeedsComposers(work.Keys, now))
                {
                    needs |= CreditsQuestion.Composers;
                }

                if (snapshot.NeedsTheme(work.Keys, now))
                {
                    needs |= CreditsQuestion.Theme;
                }

                if (needs == CreditsQuestion.None || findings.ContainsKey(work.Key))
                {
                    continue;
                }

                // Leads found on an earlier run are passed on, so a source can skip a step.
                var known = snapshot.Find(work.Keys);
                findings[work.Key] = new Findings(work, needs)
                {
                    ReleaseGroupId = work.ReleaseGroupId ?? known?.ReleaseGroupId,
                    WikipediaTitle = work.WikipediaTitle ?? known?.WikipediaTitle,
                };
            }

            if (findings.Count == 0)
            {
                _logger.LogInformation("ThemeForge: everything about every title's music is already known; nothing to look up.");
                progress?.Report(100);
                return current;
            }

            _logger.LogInformation(
                "ThemeForge: looking up the music of {Count} of {Total} titles.",
                findings.Count,
                works.Count);

            var enabled = _sources.Where(source => source.IsEnabled(configuration)).ToList();
            var share = enabled.Count == 0 ? 0 : 100.0 / enabled.Count;

            for (var index = 0; index < enabled.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var source = enabled[index];
                var candidates = findings.Values
                    .Where(finding => finding.Wants(source.Answers))
                    .Select(finding => finding.Request)
                    .ToList();

                if (candidates.Count == 0)
                {
                    continue;
                }

                var start = share * index;
                var step = new Progress<double>(within => progress?.Report(start + (within * share / 100.0)));

                CreditsAnswer answer;
                try
                {
                    answer = await source.LookUpAsync(candidates, configuration, step, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One database being unreachable is not the whole job's failure, and not a
                    // reason to believe it has nothing to say.
                    _logger.LogWarning(ex, "ThemeForge: {Source} could not be asked about music credits.", source.Name);
                    answer = CreditsAnswer.FailedFor(candidates);
                }

                var answered = 0;
                foreach (var (key, credits) in answer.Found)
                {
                    if (findings.TryGetValue(key, out var finding) && finding.Take(source, credits))
                    {
                        answered++;
                    }
                }

                var failed = 0;
                foreach (var key in answer.Failed)
                {
                    if (findings.TryGetValue(key, out var finding))
                    {
                        finding.Failed |= source.Answers;
                        failed++;
                    }
                }

                _logger.LogInformation(
                    "ThemeForge: {Source} answered for {Found} of {Asked} titles{Failed}.",
                    source.Name,
                    answered,
                    candidates.Count,
                    failed == 0 ? string.Empty : $"; {failed} could not be asked about and will be tried again");
            }

            var composers = 0;
            var themes = 0;
            var open = 0;
            var settledAnything = false;

            foreach (var finding in findings.Values)
            {
                var keys = finding.Request.Keys;
                var leads = new Leads(finding.ReleaseGroupId, finding.WikipediaTitle);

                if (finding.Needs.HasFlag(CreditsQuestion.Composers))
                {
                    if (finding.Composers is { } credits)
                    {
                        snapshot.RecordComposers(keys, leads.On(credits), finding.ComposerSource!, now);
                        composers++;
                        settledAnything = true;
                    }
                    else if (!finding.Failed.HasFlag(CreditsQuestion.Composers))
                    {
                        snapshot.RecordComposers(keys, leads.On(ResearchedCredits.None), "nobody", now);
                        settledAnything = true;
                    }
                }

                if (finding.Needs.HasFlag(CreditsQuestion.Theme))
                {
                    if (finding.Theme is { } credits)
                    {
                        snapshot.RecordTheme(keys, leads.On(credits), finding.ThemeSource!, now);
                        themes++;
                        settledAnything = true;
                    }
                    else if (!finding.Failed.HasFlag(CreditsQuestion.Theme))
                    {
                        snapshot.RecordTheme(keys, leads.On(ResearchedCredits.None), "nobody", now);
                        settledAnything = true;
                    }
                }

                if ((finding.Needs & finding.Failed & ~finding.Answered) != CreditsQuestion.None)
                {
                    open++;
                }
            }

            // Only a sync that settled something counts as having happened. One that could ask
            // nobody leaves the cache's age alone, so the next run tries again instead of trusting
            // a refresh that never took place.
            if (settledAnything)
            {
                snapshot.UpdatedUtc = now;
            }

            Write(snapshot);
            _snapshot = snapshot;

            _logger.LogInformation(
                "ThemeForge: found who wrote the music for {Composers} titles and what the theme is called for {Themes}{Open}.",
                composers,
                themes,
                open == 0 ? string.Empty : $"; {open} could not be looked up now and will be tried again");

            progress?.Report(100);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    private ComposerSnapshot? Read()
    {
        try
        {
            var path = ActualPath;
            if (!File.Exists(path))
            {
                return null;
            }

            var snapshot = JsonSerializer.Deserialize<ComposerSnapshot>(File.ReadAllText(path));
            if (snapshot is not null)
            {
                _logger.LogInformation(
                    "ThemeForge: the music credits cache names somebody for {Known} and a theme for {Themes} of {Total} entries, read {Age:0} hours ago.",
                    snapshot.Known,
                    snapshot.ThemesKnown,
                    snapshot.Entries.Count,
                    snapshot.Age.TotalHours);
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "ThemeForge: the music credits cache could not be read and will be rebuilt.");
            return null;
        }
    }

    private void Write(ComposerSnapshot snapshot)
    {
        try
        {
            var path = ActualPath;
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Kept in memory regardless, so the current session still benefits.
            _logger.LogWarning(ex, "ThemeForge: could not save the music credits cache.");
        }
    }

    /// <summary>The leads gathered for one work, stamped onto whatever is recorded for it.</summary>
    private readonly record struct Leads(string? ReleaseGroupId, string? WikipediaTitle)
    {
        public ResearchedCredits On(ResearchedCredits credits) => credits with
        {
            ReleaseGroupId = ReleaseGroupId ?? credits.ReleaseGroupId,
            WikipediaTitle = WikipediaTitle ?? credits.WikipediaTitle,
        };
    }

    /// <summary>Everything learned about one work during a sync.</summary>
    private sealed class Findings
    {
        public Findings(CreditsRequest work, CreditsQuestion needs)
        {
            Work = work;
            Needs = needs;
        }

        public CreditsRequest Work { get; }

        /// <summary>Gets the questions this sync is trying to settle.</summary>
        public CreditsQuestion Needs { get; }

        /// <summary>Gets or sets the questions some source could not ask about.</summary>
        public CreditsQuestion Failed { get; set; }

        public ResearchedCredits? Composers { get; private set; }

        public string? ComposerSource { get; private set; }

        public ResearchedCredits? Theme { get; private set; }

        public string? ThemeSource { get; private set; }

        public string? ReleaseGroupId { get; set; }

        public string? WikipediaTitle { get; set; }

        /// <summary>Gets the questions answered so far.</summary>
        public CreditsQuestion Answered =>
            (Composers is null ? CreditsQuestion.None : CreditsQuestion.Composers)
            | (Theme is null ? CreditsQuestion.None : CreditsQuestion.Theme);

        /// <summary>Gets the work as the next source should see it, carrying every lead found so far.</summary>
        public CreditsRequest Request => Work with { ReleaseGroupId = ReleaseGroupId, WikipediaTitle = WikipediaTitle };

        /// <summary>
        /// Reports whether a source answering these questions should be asked about this work:
        /// something it answers is still needed, still open, and no earlier source failed at it.
        /// </summary>
        /// <remarks>
        /// The last part keeps a Wikidata outage from becoming a crawl of the whole library at
        /// a slow source's one request at a time: whatever Wikidata could not ask about waits, and is
        /// asked about in order on the next run.
        /// </remarks>
        public bool Wants(CreditsQuestion answers) =>
            (Needs & answers & ~Answered & ~Failed) != CreditsQuestion.None;

        /// <summary>Takes what a source found, keeping the first answer to each question.</summary>
        /// <returns><see langword="true"/> when this settled something.</returns>
        public bool Take(ICreditsSource source, ResearchedCredits credits)
        {
            ReleaseGroupId ??= credits.ReleaseGroupId;
            WikipediaTitle ??= credits.WikipediaTitle;

            var settled = false;
            if (source.Answers.HasFlag(CreditsQuestion.Composers) && Composers is null && credits.Any)
            {
                Composers = credits;
                ComposerSource = source.Name;
                settled |= Needs.HasFlag(CreditsQuestion.Composers);
            }

            if (source.Answers.HasFlag(CreditsQuestion.Theme) && Theme is null && credits.KnowsTheme)
            {
                Theme = credits;
                ThemeSource = source.Name;
                settled |= Needs.HasFlag(CreditsQuestion.Theme);
            }

            return settled;
        }
    }
}
