using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Decision;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>Marks the assignment benchmark, which runs the real pipeline against the internet.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THEMEFORGE_BENCHMARK") != "1")
        {
            Skip = "Set THEMEFORGE_BENCHMARK=1 (and THEMEFORGE_YTDLP) to run the assignment benchmark.";
        }
    }
}

/// <summary>
/// Runs the real discovery pipeline over titles whose theme is known, and reports what it picks.
/// </summary>
/// <remarks>
/// The research (Wikidata, then Wikipedia), ThemerrDB, the query planner, yt-dlp, the scoring
/// rules and the decision policy are all the production ones. The search loop mirrors
/// ThemeOrchestrator.SearchAndRankAsync, which is private. The search runs for every title, even
/// one ThemerrDB covers, so its quality can be judged on its own.
/// </remarks>
public class AssignmentBenchmark
{
    private readonly ITestOutputHelper _out;

    public AssignmentBenchmark(ITestOutputHelper output) => _out = output;

    /// <summary>One title, and the words a correct pick would contain.</summary>
    public sealed record Case(string Title, int Year, bool Series, string Tmdb, string Imdb, string[] Accept);

    public static readonly Case[] Cases =
    {
        // ---- films ----
        new("Fight Club", 1999, false, "550", "tt0137523", new[] { "dust brothers", "where is my mind", "this is your life", "fight club theme", "fight club main", "fight club soundtrack", "fight club ost", "fight club score" }),
        new("The Godfather", 1972, false, "238", "tt0068646", new[] { "godfather theme", "godfather main", "love theme", "speak softly", "nino rota", "godfather waltz", "godfather soundtrack", "godfather ost" }),
        new("Interstellar", 2014, false, "157336", "tt0816692", new[] { "interstellar main theme", "interstellar theme", "hans zimmer", "cornfield chase", "no time for caution", "first step", "interstellar soundtrack", "interstellar ost" }),
        new("Jurassic Park", 1993, false, "329", "tt0107290", new[] { "jurassic park theme", "john williams", "welcome to jurassic park", "jurassic park main", "jurassic park soundtrack" }),
        new("Gladiator", 2000, false, "98", "tt0172495", new[] { "now we are free", "hans zimmer", "lisa gerrard", "gladiator theme", "gladiator main", "honor him", "gladiator soundtrack", "the battle" }),
        new("Pulp Fiction", 1994, false, "680", "tt0110912", new[] { "misirlou", "dick dale", "pulp fiction theme", "pulp fiction main", "pulp fiction intro", "pulp fiction opening" }),
        new("Back to the Future", 1985, false, "105", "tt0088763", new[] { "back to the future theme", "alan silvestri", "power of love", "back to the future main", "back to the future overture", "back to the future soundtrack" }),
        new("The Shawshank Redemption", 1994, false, "278", "tt0111161", new[] { "thomas newman", "shawshank theme", "stoic theme", "shawshank main", "shawshank soundtrack", "so was red", "end title", "shawshank ost" }),
        new("Inception", 2010, false, "27205", "tt1375666", new[] { "inception - time", "inception time", "inception theme", "hans zimmer", "inception main", "inception soundtrack", "inception ost" }),
        new("Amélie", 2001, false, "194", "tt0211915", new[] { "yann tiersen", "comptine", "valse", "amelie theme", "amélie theme", "amelie soundtrack", "amélie soundtrack" }),
        new("The Fall", 2006, false, "14784", "tt0460791", new[] { "beethoven", "symphony no. 7", "7th symphony", "allegretto", "the fall theme", "the fall soundtrack", "krishna levy" }),
        new("Drive", 2011, false, "64690", "tt0780504", new[] { "nightcall", "kavinsky", "a real hero", "cliff martinez", "drive theme", "drive soundtrack", "drive ost" }),

        // ---- series ----
        new("Breaking Bad", 2008, true, "1396", "tt0903747", new[] { "breaking bad theme", "breaking bad intro", "breaking bad opening", "dave porter", "breaking bad main title" }),
        new("The Sopranos", 1999, true, "1398", "tt0141842", new[] { "woke up this morning", "alabama 3", "sopranos theme", "sopranos intro", "sopranos opening" }),
        new("Game of Thrones", 2011, true, "1399", "tt0944947", new[] { "game of thrones theme", "game of thrones main title", "game of thrones intro", "game of thrones opening", "ramin djawadi" }),
        new("Friends", 1994, true, "1668", "tt0108778", new[] { "i'll be there for you", "ill be there for you", "rembrandts", "friends theme", "friends intro", "friends opening" }),
        new("The Office", 2005, true, "2316", "tt0386676", new[] { "the office theme", "the office intro", "the office opening", "jay ferguson", "the office main title" }),
        new("Stranger Things", 2016, true, "66732", "tt4574334", new[] { "stranger things theme", "stranger things intro", "stranger things opening", "kyle dixon", "michael stein", "stranger things main title" }),
        new("Twin Peaks", 1990, true, "1920", "tt0098936", new[] { "twin peaks theme", "badalamenti", "falling", "twin peaks intro", "twin peaks opening" }),
        new("Firefly", 2002, true, "1437", "tt0303461", new[] { "ballad of serenity", "firefly theme", "firefly opening", "firefly intro" }),
        new("The X-Files", 1993, true, "4087", "tt0106179", new[] { "x-files theme", "x files theme", "mark snow", "x-files intro", "x files intro", "x-files opening", "x files opening" }),
        new("Dexter", 2006, true, "1405", "tt0773262", new[] { "dexter theme", "dexter intro", "dexter opening", "rolfe kent", "dexter main title" }),
        new("Halt and Catch Fire", 2014, true, "59659", "tt2543312", new[] { "halt and catch fire theme", "halt and catch fire intro", "halt and catch fire opening", "paul haslinger", "halt and catch fire main title" }),
        new("Severance", 2022, true, "95396", "tt11280740", new[] { "severance theme", "severance intro", "severance opening", "theodore shapiro", "severance main title" }),
    };

    private sealed class FixedTools : IToolProvisioner
    {
        public Task<ToolPaths> EnsureToolsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ToolPaths(
                Environment.GetEnvironmentVariable("THEMEFORGE_YTDLP")!, "ffmpeg", "ffprobe", "bench"));

        public Task<string?> UpdateAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    [BenchmarkFact]
    public async Task RunTheBenchmark()
    {
        var filter = Environment.GetEnvironmentVariable("THEMEFORGE_BENCHMARK_ONLY");
        var cases = string.IsNullOrEmpty(filter)
            ? Cases
            : Cases.Where(c => c.Title.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

        var http = new RealHttpClientFactory();
        var config = new PluginConfiguration();
        var processes = new ProcessRunner(new NullThemeForgeLogger<ProcessRunner>());
        var search = new YtDlpCandidateSource(new FixedTools(), processes, new NullThemeForgeLogger<YtDlpCandidateSource>());
        var planner = new QueryPlanner();
        var engine = TestData.Engine();
        var policy = new DecisionPolicy();

        // ---- research, as the composer task does it: Wikidata for everything, Wikipedia for the
        // series still without a theme. IMDb is first in production but is challenged from a
        // datacenter address, so it is left out here and noted in the report.
        var requests = cases.Select(c =>
        {
            var keys = CreditsKeys.For(c.Imdb, c.Tmdb, null, c.Series);
            return new CreditsRequest(keys[0], keys, c.Imdb, c.Tmdb, c.Series, c.Title);
        }).ToList();

        var wikidata = new WikidataCreditsSource(http, new NullThemeForgeLogger<WikidataCreditsSource>());
        var research = await wikidata.LookUpAsync(requests, config, null, CancellationToken.None);
        var found = new Dictionary<string, ResearchedCredits>(research.Found, StringComparer.OrdinalIgnoreCase);

        var needTheme = requests
            .Where(r => r.IsSeries && !(found.TryGetValue(r.Key, out var f) && f.Theme is not null))
            .Select(r => r with { WikipediaTitle = found.TryGetValue(r.Key, out var f) ? f.WikipediaTitle : null })
            .Where(r => r.WikipediaTitle is not null)
            .ToList();
        if (needTheme.Count > 0)
        {
            var wikipedia = new WikipediaThemeSource(http, new NullThemeForgeLogger<WikipediaThemeSource>());
            var themes = await wikipedia.LookUpAsync(needTheme, config, null, CancellationToken.None);
            foreach (var (key, credits) in themes.Found)
            {
                if (!found.TryGetValue(key, out var earlier))
                {
                    found[key] = credits;
                    continue;
                }

                // Wikipedia adds the theme; Wikidata's composers stay.
                found[key] = new ResearchedCredits(
                    earlier.Composers.Count > 0 ? earlier.Composers : credits.Composers,
                    earlier.Artist,
                    earlier.ReleaseGroupId)
                {
                    Theme = credits.Theme ?? earlier.Theme,
                    ThemeComposers = credits.ThemeComposers.Count > 0 ? credits.ThemeComposers : earlier.ThemeComposers,
                    WikipediaTitle = earlier.WikipediaTitle,
                    Countries = earlier.Countries,
                };
            }
        }

        var report = new List<Dictionary<string, object?>>();

        foreach (var c in cases)
        {
            var key = requests.First(r => r.Label == c.Title).Key;
            found.TryGetValue(key, out var credits);

            var identity = new MediaIdentity
            {
                ItemId = Guid.NewGuid(),
                Title = c.Title,
                NormalizedTitle = TitleNormalizer.Normalize(c.Title),
                Year = c.Year,
                Kind = c.Series ? BaseItemKind.Series : BaseItemKind.Movie,
                TmdbId = c.Tmdb,
                ImdbId = c.Imdb,
                Composers = credits?.Composers ?? Array.Empty<string>(),
                Theme = credits?.Theme,
                Countries = credits?.Countries ?? Array.Empty<string>(),
            };

            // ---- ThemerrDB: the per-title record, and whether its link still plays.
            var (themerrUrl, themerrAlive) = await Themerr(http, c);

            // ---- the search ladder, as SearchAndRankAsync walks it.
            var context = new ScoringContext { Identity = identity, Configuration = config, FindExistingAssignment = _ => null };
            var plan = planner.Plan(identity, config);
            var best = new Dictionary<string, ScoreResult>(StringComparer.Ordinal);
            var ran = new List<string>();
            var auto = Math.Max(config.AutoAssignThreshold, config.ReviewThreshold);

            foreach (var query in plan)
            {
                ran.Add(query.Text);
                var hits = await search.SearchAsync(query, config.SearchResultsPerQuery, CancellationToken.None);
                if (hits.Count == 0)
                {
                    continue;
                }

                var shortlist = engine.Rank(hits, context)
                    .Where(r => !r.IsVetoed)
                    .Take(Math.Max(1, config.HydrateTopCandidates))
                    .Select(r => r.Candidate)
                    .ToList();
                if (shortlist.Count == 0)
                {
                    continue;
                }

                var hydrated = await search.HydrateAsync(shortlist, CancellationToken.None);
                foreach (var r in engine.Rank(hydrated, context))
                {
                    if (!best.TryGetValue(r.Candidate.Id, out var had) || r.Total > had.Total)
                    {
                        best[r.Candidate.Id] = r;
                    }
                }

                if (config.StopLadderOnConfidentHit && best.Values.Any(r => r.CanAutoAssign(auto)))
                {
                    break;
                }
            }

            var ranked = best.Values.OrderByDescending(r => r.Total).ToList();
            var decision = policy.Decide(ranked, config);
            var top = decision.Best ?? ranked.FirstOrDefault(r => !r.IsVetoed);
            var verdict = top is null ? "none" : Grade(c, top.Candidate) ? "RIGHT" : "WRONG";

            _out.WriteLine(
                $"{(c.Series ? "TV  " : "FILM")} {c.Title,-26} themerr={(themerrUrl is null ? "-" : themerrAlive ? "alive" : "DEAD"),-6} " +
                $"composers=[{string.Join(", ", identity.Composers.Take(2))}] theme={(identity.Theme?.ToString() ?? "-")}");
            _out.WriteLine(
                $"      {decision.Outcome,-10} {verdict,-5} {top?.Total,5:F1}  {top?.Candidate.Title} | {top?.Candidate.Channel} | {top?.Candidate.DurationSeconds}s");

            report.Add(new Dictionary<string, object?>
            {
                ["title"] = c.Title,
                ["series"] = c.Series,
                ["composers"] = identity.Composers,
                ["theme"] = identity.Theme?.ToString(),
                ["countries"] = identity.Countries,
                ["themerrUrl"] = themerrUrl,
                ["themerrAlive"] = themerrAlive,
                ["queries"] = ran,
                ["outcome"] = decision.Outcome.ToString(),
                ["reason"] = decision.Reason,
                ["chosen"] = top?.Candidate.Title,
                ["verdict"] = verdict,
                ["top"] = ranked.Take(5).Select(r => new Dictionary<string, object?>
                {
                    ["title"] = r.Candidate.Title,
                    ["channel"] = r.Candidate.Channel,
                    ["duration"] = r.Candidate.DurationSeconds,
                    ["views"] = r.Candidate.ViewCount,
                    ["url"] = r.Candidate.Url,
                    ["total"] = Math.Round(r.Total, 1),
                    ["vetoed"] = r.IsVetoed,
                    ["reviewOnly"] = r.IsReviewOnly,
                    ["right"] = Grade(c, r.Candidate),
                    ["breakdown"] = r.Breakdown
                        .Where(s => Math.Abs(s.Contribution) > 0.01 || s.IsVeto)
                        .Select(s => $"{s.Rule} {s.Contribution:+0.0;-0.0}{(s.IsVeto ? " VETO" : string.Empty)}: {s.Reason}")
                        .ToList(),
                }).ToList(),
            });
        }

        var path = Environment.GetEnvironmentVariable("THEMEFORGE_BENCHMARK_OUT") ?? "benchmark.json";
        File.WriteAllText(path, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));

        var right = report.Count(r => (string)r["verdict"]! == "RIGHT");
        var autoRight = report.Count(r => (string)r["verdict"]! == "RIGHT" && (string)r["outcome"]! == "AutoAssign");
        var autoWrong = report.Count(r => (string)r["verdict"]! == "WRONG" && (string)r["outcome"]! == "AutoAssign");
        _out.WriteLine($"\nsearch picked the right theme for {right}/{report.Count}; auto-assigned right {autoRight}, auto-assigned WRONG {autoWrong}");
    }

    /// <summary>Whether a pick is one a person would accept as this title's theme.</summary>
    private static bool Grade(Case c, Candidate candidate)
    {
        var text = (candidate.Title + " " + candidate.Channel).ToLowerInvariant();
        return c.Accept.Any(word => text.Contains(word, StringComparison.Ordinal));
    }

    private static async Task<(string? Url, bool Alive)> Themerr(IHttpClientFactory http, Case c)
    {
        var kind = c.Series ? "tv_shows" : "movies";
        using var client = http.CreateClient(string.Empty);
        try
        {
            using var response = await client.GetAsync($"https://app.lizardbyte.dev/ThemerrDB/{kind}/themoviedb/{c.Tmdb}.json");
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("youtube_theme_url", out var u) || u.GetString() is not { } url)
            {
                return (null, false);
            }

            using var probe = await client.GetAsync("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(url));
            return (url, probe.IsSuccessStatusCode);
        }
        catch (HttpRequestException)
        {
            return (null, false);
        }
    }
}
