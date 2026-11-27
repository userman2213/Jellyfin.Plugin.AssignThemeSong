using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Mime;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Policy;
using Jellyfin.Plugin.ThemeForge.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.ThemeForge.Api;

/// <summary>One setting, as the engine has it and as it is stored.</summary>
/// <param name="Name">The setting's name.</param>
/// <param name="InUse">The value the engine will use for the next run.</param>
/// <param name="OnDisk">The value in the saved configuration file.</param>
/// <param name="Agrees">Whether those are the same.</param>
public sealed record SettingRow(string Name, string InUse, string OnDisk, bool Agrees);

/// <summary>How often one skip reason occurred, and an example.</summary>
/// <param name="Reason">The reason, as the pipeline phrased it.</param>
/// <param name="Count">How many items it applies to.</param>
/// <param name="Examples">A few of those items, by name.</param>
public sealed record SkipReasonRow(string Reason, int Count, IReadOnlyList<string> Examples);

/// <summary>Everything needed to tell whether the settings are actually in force.</summary>
public sealed class DiagnosticsDto
{
    /// <summary>Gets or sets where Jellyfin stores this plugin's settings.</summary>
    public string ConfigurationFilePath { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether that file could be read back.</summary>
    public bool ConfigurationReadable { get; set; }

    /// <summary>Gets or sets when that file was last written.</summary>
    public DateTime? ConfigurationSavedUtc { get; set; }

    /// <summary>Gets or sets every setting, in use and as stored.</summary>
    public IReadOnlyList<SettingRow> Settings { get; set; } = Array.Empty<SettingRow>();

    /// <summary>Gets or sets the settings whose stored value differs from the one in use.</summary>
    public IReadOnlyList<SettingRow> Disagreements { get; set; } = Array.Empty<SettingRow>();

    /// <summary>Gets or sets each library and the rule that will actually apply to it.</summary>
    public IReadOnlyList<LibraryDiagnostic> Libraries { get; set; } = Array.Empty<LibraryDiagnostic>();

    /// <summary>Gets or sets stored rules that match no library on this server.</summary>
    public IReadOnlyList<LibrarySummary> OrphanedRules { get; set; } = Array.Empty<LibrarySummary>();

    /// <summary>Gets or sets why items were passed over on the last run, most common first.</summary>
    public IReadOnlyList<SkipReasonRow> SkipReasons { get; set; } = Array.Empty<SkipReasonRow>();

    /// <summary>Gets or sets anything found to be wrong, in plain words.</summary>
    public IReadOnlyList<string> Problems { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the plugin's data directory.</summary>
    public string DataPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the index file's path.</summary>
    public string IndexPath { get; set; } = string.Empty;

    /// <summary>Gets or sets the log file's path.</summary>
    public string LogPath { get; set; } = string.Empty;

    /// <summary>Gets or sets what the local ThemerrDB copy holds.</summary>
    public CatalogueStatus Themerr { get; set; } = new();

    /// <summary>Gets or sets how many films the library holds.</summary>
    public int Movies { get; set; }

    /// <summary>Gets or sets how many of those have no TMDB id, which is what ThemerrDB is keyed on.</summary>
    public int MoviesWithoutTmdbId { get; set; }

    /// <summary>Gets or sets how many series the library holds.</summary>
    public int Series { get; set; }

    /// <summary>Gets or sets how many of those have no TMDB id, so ThemerrDB can only match them by name.</summary>
    public int SeriesWithoutTmdbId { get; set; }

    /// <summary>Gets or sets where that copy is kept.</summary>
    public string ThemerrPath { get; set; } = string.Empty;

    /// <summary>Gets or sets how many titles somebody is known to have written the music for.</summary>
    /// <remarks>
    /// The measurement that says whether researching composers earned its place. An item with no
    /// composer is searched for with one fewer phrasing, scored without the bonus, and -- if its
    /// title is an ordinary word -- held below the auto-assign band for want of corroboration.
    /// </remarks>
    public int TitlesWithComposer { get; set; }

    /// <summary>Gets or sets how many titles nobody is recorded for.</summary>
    public int TitlesWithoutComposer { get; set; }

    /// <summary>Gets or sets how many series have a known theme song, searched for by name.</summary>
    public int SeriesWithThemeSong { get; set; }

    /// <summary>Gets or sets how many films have a known theme song.</summary>
    public int MoviesWithThemeSong { get; set; }

    /// <summary>Gets or sets where the music credits cache is kept.</summary>
    public string ComposersPath { get; set; } = string.Empty;

    /// <summary>Gets or sets when the music credits cache was last filled, if ever.</summary>
    public DateTime? ComposersUpdatedUtc { get; set; }

    /// <summary>Gets or sets how many works the music credits cache holds an answer for.</summary>
    public int ComposersKnown { get; set; }

    /// <summary>Gets or sets the state of the local copy of IMDb's crew data.</summary>
    public ImdbCrewStatus? ImdbCrew { get; set; }

    /// <summary>Gets or sets where the copy of IMDb's crew data is kept.</summary>
    public string ImdbCrewPath { get; set; } = string.Empty;

    /// <summary>Gets or sets how many films nobody is credited with the music of, and no theme is named for.</summary>
    /// <remarks>
    /// These are not searched for at all: an unattended run searches only for who made the music.
    /// The count, and the titles, are the measured basis for whatever is tried next.
    /// </remarks>
    public int MoviesWithNothingToSearchFor { get; set; }

    /// <summary>Gets or sets how many series nobody is credited with the music of, and no theme is named for.</summary>
    public int SeriesWithNothingToSearchFor { get; set; }

    /// <summary>Gets or sets some of the titles with nothing to search for, films first.</summary>
    public IReadOnlyList<string> NothingToSearchForExamples { get; set; } = Array.Empty<string>();
}

/// <summary>
/// Reports what ThemeForge is actually configured to do, as opposed to what the settings form
/// shows.
/// </summary>
/// <remarks>
/// A settings page shows what was typed into it. It cannot show a save that did not reach the
/// disk, a rule stored against a library id nothing resolves to, or the reason an item was passed
/// over — and those were exactly the failures that made "I set the rule and nothing happened"
/// impossible to diagnose from the UI. Every value here is read back from where the engine reads
/// it, so a discrepancy is visible rather than inferred.
/// </remarks>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("ThemeForge/Diagnostics")]
[Produces(MediaTypeNames.Application.Json)]
public class DiagnosticsController : ControllerBase
{
    private const int MaxExamplesPerReason = 5;

    /// <summary>How many films, and how many series, with nothing to search for are named.</summary>
    private const int MaxNothingExamples = 10;

    private readonly IThemeIndex _index;
    private readonly ILibraryPolicyResolver _policyResolver;
    private readonly IThemerrDbCatalogue _themerrDb;
    private readonly IComposerCatalogue _composers;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaIdentityResolver _identityResolver;
    private readonly IImdbCrewData? _crew;

    /// <summary>Initializes a new instance of the <see cref="DiagnosticsController"/> class.</summary>
    /// <param name="index">The decision index, for recorded skip reasons.</param>
    /// <param name="policyResolver">Resolves libraries and their rules.</param>
    /// <param name="themerrDb">The local ThemerrDB copy.</param>
    /// <param name="composers">The music credits cache.</param>
    /// <param name="libraryManager">The library, to count items and their ids.</param>
    /// <param name="identityResolver">Reads provider ids off items.</param>
    /// <param name="crew">IMDb's crew data, or null for none.</param>
    public DiagnosticsController(
        IThemeIndex index,
        ILibraryPolicyResolver policyResolver,
        IThemerrDbCatalogue themerrDb,
        IComposerCatalogue composers,
        ILibraryManager libraryManager,
        IMediaIdentityResolver identityResolver,
        IImdbCrewData? crew = null)
    {
        _index = index;
        _policyResolver = policyResolver;
        _themerrDb = themerrDb;
        _composers = composers;
        _libraryManager = libraryManager;
        _identityResolver = identityResolver;
        _crew = crew;
    }

    /// <summary>Reports the configuration in force, the resolved library rules and why items were skipped.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The diagnostics.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<DiagnosticsDto>> Get(CancellationToken cancellationToken)
    {
        await _index.LoadAsync(cancellationToken).ConfigureAwait(false);

        var inUse = Plugin.Config;
        var onDisk = Plugin.Instance?.ReadPersistedConfiguration();
        var problems = new List<string>();

        var settings = CompareSettings(inUse, onDisk);
        var disagreements = settings.Where(row => !row.Agrees).ToList();

        if (onDisk is null)
        {
            problems.Add(
                "The saved settings file could not be read, so nothing here is guaranteed to survive a restart. "
                + "Check that Jellyfin can read and write its plugin configuration directory.");
        }
        else if (disagreements.Count > 0)
        {
            problems.Add(
                $"{disagreements.Count} settings differ between what the engine is using and what is saved on disk. "
                + "The saved values are what will be used after a restart.");
        }

        var audit = _policyResolver.Audit(inUse);
        problems.AddRange(audit.Problems);

        var themerr = await _themerrDb.GetAsync(cancellationToken).ConfigureAwait(false);
        if (inUse.UseThemerrDb && inUse.SyncThemerrDb && !themerr.IsUsable)
        {
            problems.Add(
                "The local copy of ThemerrDB is empty, so every title in the library will cost a request just "
                + "to discover it is not in the database. Run the \"Update the ThemerrDB catalogue\" scheduled task.");
        }

        var composers = await _composers.GetAsync(cancellationToken).ConfigureAwait(false);
        var ids = CountProviderIds();
        var crew = _crew?.Status(inUse);

        if (crew is { Enabled: true, Available: false })
        {
            problems.Add(
                "IMDb's crew data has not been downloaded"
                + (crew.Refreshing ? " yet; it is being downloaded now." : string.IsNullOrEmpty(crew.LastError)
                    ? ", so most films have nobody to search for. Run the \"Update IMDb crew data\" scheduled task."
                    : $": the last attempt failed ({crew.LastError}). Run the \"Update IMDb crew data\" scheduled task to try again."));
        }

        var nothing = ids.MoviesWithNothing + ids.SeriesWithNothing;
        if (nothing > 0)
        {
            problems.Add(
                $"{ids.MoviesWithNothing} films and {ids.SeriesWithNothing} series have nobody credited with their music and no "
                + "theme named for them, so they are not searched for: an unattended run searches only for who made the music, "
                + "never a title on its own. They are looked at again twice a day, and searched for as soon as somebody is credited. "
                + (!inUse.ResearchComposers
                    ? "Looking up who made the music is switched off, which leaves only Jellyfin's own credits."
                    : composers.IsUsable
                        ? "A title with no IMDb, TMDB or TVDB id cannot be looked up at all, so a metadata provider is what helps those."
                        : "Run the \"Look up who wrote the music\" scheduled task."));
        }

        if (ids.SeriesWithoutTmdb > 0)
        {
            problems.Add(
                $"{ids.SeriesWithoutTmdb} of {ids.Series} series have no TMDB id. ThemerrDB keys shows on TMDB and nothing else, "
                + "so those can only be matched against it by name. Enabling TheMovieDb as a metadata provider for the library fixes this.");
        }

        return new DiagnosticsDto
        {
            Movies = ids.Movies,
            MoviesWithoutTmdbId = ids.MoviesWithoutTmdb,
            Series = ids.Series,
            SeriesWithoutTmdbId = ids.SeriesWithoutTmdb,
            ConfigurationFilePath = Plugin.Instance?.ConfigurationFilePath ?? string.Empty,
            ConfigurationReadable = onDisk is not null,
            ConfigurationSavedUtc = LastWritten(Plugin.Instance?.ConfigurationFilePath),
            Settings = settings,
            Disagreements = disagreements,
            Libraries = audit.Libraries,
            OrphanedRules = audit.OrphanedRules,
            SkipReasons = SkipReasons(),
            Problems = problems,
            DataPath = Plugin.Instance?.DataPath ?? string.Empty,
            IndexPath = Plugin.Instance?.IndexPath ?? string.Empty,
            LogPath = ThemeForgeLogFile.Shared.CurrentPath ?? "(file logging is off)",
            ThemerrPath = ThemerrDbCatalogue.SnapshotPath,
            TitlesWithComposer = ids.WithComposer,
            TitlesWithoutComposer = ids.WithoutComposer,
            SeriesWithThemeSong = ids.SeriesWithTheme,
            MoviesWithThemeSong = ids.MoviesWithTheme,
            ComposersPath = ComposerCatalogue.SnapshotPath,
            ComposersUpdatedUtc = composers.IsUsable ? composers.UpdatedUtc : null,
            ComposersKnown = composers.Known,
            ImdbCrew = crew,
            ImdbCrewPath = ImdbCrewData.DefaultPath,
            MoviesWithNothingToSearchFor = ids.MoviesWithNothing,
            SeriesWithNothingToSearchFor = ids.SeriesWithNothing,
            NothingToSearchForExamples = ids.NothingExamples,
            Themerr = new CatalogueStatus
            {
                Movies = themerr.MovieTmdbIds.Count,
                Shows = themerr.TvShows.Count,
                Collections = themerr.Collections.Count,
                UpdatedUtc = themerr.IsUsable ? themerr.UpdatedUtc : null,
                AgeHours = themerr.IsUsable ? themerr.Age.TotalHours : null,
            },
        };
    }

    /// <summary>
    /// Counts the items ThemerrDB cannot be asked about by id.
    /// </summary>
    /// <remarks>
    /// The single most common reason a title the database has never resolves: Jellyfin never
    /// recorded the id the database is keyed on. Invisible from the settings page and from the
    /// run summary, which just says "nothing found".
    /// </remarks>
    private LibraryCounts CountProviderIds()
    {
        try
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { Jellyfin.Data.Enums.BaseItemKind.Movie, Jellyfin.Data.Enums.BaseItemKind.Series },
                Recursive = true,
                IsVirtualItem = false,
            });

            int movies = 0, moviesWithout = 0, series = 0, seriesWithout = 0, composed = 0, uncomposed = 0;
            int seriesThemed = 0, moviesThemed = 0, moviesNothing = 0, seriesNothing = 0;
            var filmExamples = new List<string>();
            var seriesExamples = new List<string>();

            foreach (var item in items)
            {
                var identity = _identityResolver.Resolve(item);
                if (identity is null)
                {
                    continue;
                }

                var missing = string.IsNullOrWhiteSpace(identity.TmdbId);
                var themed = identity.Theme is not null ? 1 : 0;
                if (identity.IsSeries)
                {
                    series++;
                    seriesWithout += missing ? 1 : 0;
                    seriesThemed += themed;
                }
                else
                {
                    movies++;
                    moviesWithout += missing ? 1 : 0;
                    moviesThemed += themed;
                }

                // Resolving already consults IMDb's crew data and the research cache, so this counts
                // what the pipeline will actually see rather than what Jellyfin alone holds.
                if (identity.Composers.Count > 0)
                {
                    composed++;
                }
                else
                {
                    uncomposed++;
                }

                // Nothing to search for: the same test the search planner applies.
                if (identity.Composers.Count == 0 && identity.Theme is null)
                {
                    var examples = identity.IsSeries ? seriesExamples : filmExamples;
                    if (identity.IsSeries)
                    {
                        seriesNothing++;
                    }
                    else
                    {
                        moviesNothing++;
                    }

                    if (examples.Count < MaxNothingExamples)
                    {
                        examples.Add(identity.Label);
                    }
                }
            }

            return new LibraryCounts(movies, moviesWithout, series, seriesWithout, composed, uncomposed, seriesThemed, moviesThemed)
            {
                MoviesWithNothing = moviesNothing,
                SeriesWithNothing = seriesNothing,
                NothingExamples = filmExamples.Concat(seriesExamples).ToList(),
            };
        }
        catch (Exception)
        {
            // The library being unavailable is reported elsewhere; the counts are just zero here.
            return new LibraryCounts(0, 0, 0, 0, 0, 0, 0, 0);
        }
    }

    /// <summary>What the library holds, as the pipeline sees it.</summary>
    private sealed record LibraryCounts(
        int Movies,
        int MoviesWithoutTmdb,
        int Series,
        int SeriesWithoutTmdb,
        int WithComposer,
        int WithoutComposer,
        int SeriesWithTheme,
        int MoviesWithTheme)
    {
        public int MoviesWithNothing { get; init; }

        public int SeriesWithNothing { get; init; }

        public IReadOnlyList<string> NothingExamples { get; init; } = Array.Empty<string>();
    }

    private static DateTime? LastWritten(string? path)
    {
        try
        {
            return string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)
                ? null
                : System.IO.File.GetLastWriteTimeUtc(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Compares every setting the engine holds against the one that was saved.
    /// </summary>
    /// <remarks>
    /// Done by reflection rather than by listing fields, so a setting added later cannot be
    /// forgotten here and quietly escape the check.
    /// </remarks>
    private static IReadOnlyList<SettingRow> CompareSettings(PluginConfiguration inUse, PluginConfiguration? onDisk) =>
        typeof(PluginConfiguration)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .OrderBy(property => property.Name, StringComparer.Ordinal)
            .Select(property =>
            {
                var live = Describe(Read(property, inUse));
                var saved = onDisk is null ? "(unreadable)" : Describe(Read(property, onDisk));
                return new SettingRow(property.Name, live, saved, onDisk is not null && string.Equals(live, saved, StringComparison.Ordinal));
            })
            .ToList();

    private static object? Read(PropertyInfo property, PluginConfiguration configuration)
    {
        try
        {
            return property.GetValue(configuration);
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    private static string Describe(object? value) => value switch
    {
        null => "(not set)",
        string text => text.Length == 0 ? "(empty)" : text,
        bool flag => flag ? "yes" : "no",
        double number => number.ToString("0.###", CultureInfo.InvariantCulture),
        IEnumerable list and not string => DescribeList(list),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    private static string DescribeList(IEnumerable list)
    {
        var parts = list.Cast<object?>().Select(Describe).ToList();
        return parts.Count == 0 ? "(none)" : $"{parts.Count}: {string.Join(", ", parts)}";
    }

    /// <summary>
    /// Groups the reasons items were passed over.
    /// </summary>
    /// <remarks>
    /// A run reports "767 skipped" and stops there, which says nothing about whether the rule you
    /// just changed was even consulted. The pipeline already phrases a reason per item; this
    /// counts them so the answer is one line rather than a log search.
    /// </remarks>
    private IReadOnlyList<SkipReasonRow> SkipReasons() =>
        _index.All()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.LastSkipReason))
            .GroupBy(entry => entry.LastSkipReason!, StringComparer.Ordinal)
            .Select(group => new SkipReasonRow(
                group.Key,
                group.Count(),
                group.OrderByDescending(entry => entry.LastSkipUtc ?? DateTime.MinValue)
                    .Take(MaxExamplesPerReason)
                    .Select(entry => entry.Label)
                    .ToList()))
            .OrderByDescending(row => row.Count)
            .ToList();
}
