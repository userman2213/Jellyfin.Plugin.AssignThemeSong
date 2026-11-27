using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>Runs only when asked to, because it downloads IMDb's files: about 1.2 GB.</summary>
public sealed class ImdbDatasetFactAttribute : FactAttribute
{
    public ImdbDatasetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("THEMEFORGE_IMDB_DATASET_TESTS") != "1")
        {
            Skip = "Set THEMEFORGE_IMDB_DATASET_TESTS=1 to download IMDb's dataset files (about 1.2 GB) and build the real table.";
        }
    }
}

/// <summary>
/// Builds the composer table from IMDb's real files, the way the scheduled task does, and checks
/// it names the composers it should.
/// </summary>
public sealed class ImdbCrewLiveTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-crewlive-" + Guid.NewGuid().ToString("N"));

    public ImdbCrewLiveTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [ImdbDatasetFact]
    public async Task BuildsTheRealTableAndNamesTheComposersItShould()
    {
        using var crew = new ImdbCrewData(new RealHttpClientFactory(), new NullThemeForgeLogger<ImdbCrewData>(), Path.Combine(_root, "imdb-composers.bin"));
        GC.Collect();
        var before = Process.GetCurrentProcess().WorkingSet64;
        var clock = Stopwatch.StartNew();

        Assert.True(await crew.RefreshAsync(null, CancellationToken.None), crew.Status(new PluginConfiguration()).LastError);

        var status = crew.Status(new PluginConfiguration());
        _output.WriteLine(
            "built in {0:N0} s: {1:N0} titles, {2:N0} of them series, {3:N1} MB, published {4:u}; working set {6:N0} MB before, peak {5:N0} MB",
            clock.Elapsed.TotalSeconds,
            status.Titles,
            status.Series,
            status.FileBytes / 1048576.0,
            status.PublishedUtc,
            Process.GetCurrentProcess().PeakWorkingSet64 / 1048576.0,
            before / 1048576.0);

        // The film this was built for: its soundtrack listing names only a western song.
        Assert.Contains("Larry Groupé", crew.Composers("tt19035928"));

        Assert.Contains("Jerry Goldsmith", crew.Composers("tt0078748"));
        Assert.Contains("James Horner", crew.Composers("tt0120338"));
        Assert.Contains("Thomas Newman", crew.Composers("tt1074638"));
        Assert.Contains("Hans Zimmer", crew.Composers("tt1856101"));

        // Series, through their episodes.
        Assert.Equal("Dave Porter", crew.Composers("tt0903747")[0]);
        Assert.Equal("Michael Giacchino", crew.Composers("tt0411008")[0]);
        Assert.Equal("Ramin Djawadi", crew.Composers("tt0944947")[0]);
        Assert.Equal("Bear McCreary", crew.Composers("tt0407362")[0]);

        // One episode of 86 is not The Sopranos' composer.
        Assert.Empty(crew.Composers("tt0141842"));

        Assert.InRange(status.Titles, 500_000, 2_000_000);
        Assert.InRange(status.FileBytes, 5L << 20, 60L << 20);

        foreach (var (id, label) in new[]
        {
            ("tt19035928", "Lucky Strike"), ("tt0137523", "Fight Club"), ("tt0068646", "The Godfather"),
            ("tt0133093", "The Matrix"), ("tt0482571", "The Prestige"), ("tt0303461", "Firefly"),
            ("tt0773262", "Dexter"), ("tt0108778", "Friends"),
        })
        {
            _output.WriteLine("{0,-16} {1}", label, string.Join(", ", crew.Composers(id)));
        }
    }
}
