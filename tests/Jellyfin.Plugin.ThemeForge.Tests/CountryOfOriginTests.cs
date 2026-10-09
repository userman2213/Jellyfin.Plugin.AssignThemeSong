using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers where the country a work was made in comes from, which is what keeps the American
/// Office from being given the British one's theme.
/// </summary>
/// <remarks>
/// Jellyfin records production countries for films, from TMDB, and records none for a series, so
/// for the shows where this matters most it has to come from research. Wikidata records it for
/// both: asked live, The Office (2005) came back <c>US</c> and The Office (2001) <c>GB</c>.
/// </remarks>
public sealed class CountryOfOriginTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 12, 0, 0, DateTimeKind.Utc);

    private static readonly CreditsRequest AmericanOffice =
        new("imdb:tt0386676", new[] { "imdb:tt0386676", "tmdbtv:2316" }, "tt0386676", "2316", true, "The Office");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "themeforge-tests", Guid.NewGuid().ToString("N"));

    private string SnapshotPath => Path.Combine(_directory, "composers.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    // ---- Wikidata ----

    [Fact]
    public void WikidataIsAskedWhereEachWorkWasMade()
    {
        var query = WikidataCreditsSource.BuildQuery(new[] { AmericanOffice });

        Assert.NotNull(query);
        Assert.Contains("wdt:P495 ?country", query, StringComparison.Ordinal);
        Assert.Contains("?country wdt:P297 ?origin", query, StringComparison.Ordinal);
        Assert.True(new WikidataCreditsSource(null!, NullThemeForgeLogger<WikidataCreditsSource>.Instance)
            .Answers.HasFlag(CreditsQuestion.Origin));
    }

    [Fact]
    public void EveryCountryOfACoProductionIsKeptOnce()
    {
        // The Fall (2006) is recorded as American, British and Indian, and comes back as a row for
        // each, repeated for every composer.
        var found = WikidataCreditsSource.Parse(Rows(
            ("tt0460791", "Krishna Levy", "US"),
            ("tt0460791", "Krishna Levy", "GB"),
            ("tt0460791", "Krishna Levy", "in"),
            ("tt0460791", "Krishna Levy", "US")));

        Assert.Equal(new[] { "US", "GB", "IN" }, found["imdb:tt0460791"].Countries);
    }

    [Fact]
    public void AWorkKnownOnlyByItsCountryIsStillAnAnswer()
    {
        var found = WikidataCreditsSource.Parse(Rows(("tt0386676", null, "US")));

        Assert.Equal(new[] { "US" }, found["imdb:tt0386676"].Countries);
        Assert.False(found["imdb:tt0386676"].Any);
    }

    [Fact]
    public void SomethingThatIsNotACountryCodeIsNotKept()
    {
        var found = WikidataCreditsSource.Parse(Rows(("tt0386676", "Jay Ferguson", "USA"), ("tt0386676", "Jay Ferguson", "Q30")));

        Assert.Empty(found["imdb:tt0386676"].Countries);
    }

    // ---- The catalogue ----

    [Fact]
    public async Task ARecordFromBeforeCountriesWereAskedIsAskedOnceAndOnlyOfWikidata()
    {
        // Composer and theme were settled by 2.9; only the new question is open. IMDb, which is a
        // page at a time, must not be asked again for a library's worth of titles because of it.
        WriteSnapshot(new ComposerCredits
        {
            Key = "imdb:tt0386676",
            Composers = new List<string> { "Jay Ferguson" },
            Source = "IMDb",
            LookedUpUtc = DateTime.UtcNow,
            ThemeSource = "nobody",
            ThemeLookedUpUtc = DateTime.UtcNow,
        });

        var imdb = new Source("IMDb", -10, CreditsQuestion.Composers | CreditsQuestion.Theme, _ => CreditsAnswer.Nothing);
        var wikipedia = new Source("Wikipedia", 20, CreditsQuestion.Theme, _ => CreditsAnswer.Nothing);
        var wikidata = Wikidata(batch => Finds(batch[0].Key, new ResearchedCredits(new[] { "Somebody Else" }, null, null) { Countries = new[] { "US" } }));

        var snapshot = await Catalogue(imdb, wikidata, wikipedia).SyncAsync(new[] { AmericanOffice }, null, CancellationToken.None);
        var record = snapshot.Find(AmericanOffice.Keys)!;

        Assert.Empty(imdb.Asked);
        Assert.Empty(wikipedia.Asked);
        Assert.Single(wikidata.Asked);
        Assert.Equal(new[] { "US" }, record.Countries);
        Assert.Equal(new[] { "Jay Ferguson" }, record.Composers);
        Assert.Equal("IMDb", record.Source);
        Assert.False(snapshot.NeedsLookUp(AmericanOffice.Keys, DateTime.UtcNow.AddDays(13)));
    }

    [Fact]
    public async Task WhenIMDbCannotBeAskedWikidatasComposersDoNotSettleItsQuestion()
    {
        // Wikidata is asked where the work was made whatever happened to IMDb, and it answers
        // with composers too. Taking them would settle a question IMDb is meant to answer first,
        // for two months, because IMDb happened to be unreachable one night.
        var imdb = new Source("IMDb", -10, CreditsQuestion.Composers | CreditsQuestion.Theme, CreditsAnswer.FailedFor);
        var wikidata = Wikidata(batch => Finds(batch[0].Key, new ResearchedCredits(new[] { "Jay Ferguson" }, null, null) { Countries = new[] { "US" } }));

        var snapshot = await Catalogue(imdb, wikidata).SyncAsync(new[] { AmericanOffice }, null, CancellationToken.None);

        Assert.Single(wikidata.Asked);
        Assert.Equal(new[] { "US" }, snapshot.Find(AmericanOffice.Keys)!.Countries);
        Assert.True(snapshot.NeedsComposers(AmericanOffice.Keys, DateTime.UtcNow));
        Assert.True(snapshot.NeedsTheme(AmericanOffice.Keys, DateTime.UtcNow));
        Assert.False(snapshot.NeedsOrigin(AmericanOffice.Keys, DateTime.UtcNow));
    }

    [Fact]
    public async Task NowhereRecordedIsAMissAndAnOutageIsNot()
    {
        var nowhere = await Catalogue(Wikidata(_ => CreditsAnswer.Nothing)).SyncAsync(new[] { AmericanOffice }, null, CancellationToken.None);
        Assert.False(nowhere.NeedsOrigin(AmericanOffice.Keys, DateTime.UtcNow.AddDays(13)));
        Assert.True(nowhere.NeedsOrigin(AmericanOffice.Keys, DateTime.UtcNow.AddDays(15)));

        Directory.Delete(_directory, recursive: true);

        var down = await Catalogue(Wikidata(CreditsAnswer.FailedFor)).SyncAsync(new[] { AmericanOffice }, null, CancellationToken.None);
        Assert.True(down.NeedsOrigin(AmericanOffice.Keys, DateTime.UtcNow));
    }

    [Fact]
    public void ACacheWrittenBefore210StillLoadsAndAsksAboutCountries()
    {
        var snapshot = JsonSerializer.Deserialize<ComposerSnapshot>(
            """{"UpdatedUtc":"2026-09-01T00:00:00Z","Entries":[{"Key":"imdb:tt0386676","Composers":["Jay Ferguson"],"Source":"Wikidata","LookedUpUtc":"2026-09-01T00:00:00Z","ThemeSource":"nobody","ThemeLookedUpUtc":"2026-09-01T00:00:00Z"}]}""")!;

        Assert.Empty(snapshot.Find(AmericanOffice.Keys)!.Countries);
        Assert.False(snapshot.NeedsComposers(AmericanOffice.Keys, Now));
        Assert.True(snapshot.NeedsOrigin(AmericanOffice.Keys, Now));
    }

    // ---- Into the identity ----

    [Fact]
    public void ResearchSaysWhereASeriesWasMadeWhenJellyfinDoesNot()
    {
        var series = new Series { Name = "The Office" };
        series.SetProviderId(MetadataProvider.Imdb, "tt0386676");

        var lookup = new ResearchedPeopleLookup(Jellyfin(), new Knows("US"));
        var identity = new MediaIdentityResolver(lookup).Resolve(series)!;

        Assert.Equal(new[] { "US" }, identity.Countries);
        Assert.NotNull(CountryEdition.Contradiction("The Office (UK) Opening Theme and Closing Credits", identity));
        Assert.Null(CountryEdition.Contradiction("The Office (US) - Intro", identity));
    }

    [Fact]
    public void JellyfinsOwnCountriesWin()
    {
        var film = new Movie { Name = "The Fall", ProductionLocations = new[] { "India", "United States of America" } };
        film.SetProviderId(MetadataProvider.Imdb, "tt0460791");

        var lookup = new ResearchedPeopleLookup(Jellyfin(), new Knows("GB"));

        Assert.Equal(new[] { "India", "United States of America" }, lookup.Countries(film));
    }

    [Theory]
    [InlineData("GB", "The Office (US) - Intro", "American")]
    [InlineData("GB", "The Office (UK) Opening Theme", null)]
    [InlineData("US", "The Office (UK) Opening Theme", "British")]
    [InlineData("AU", "Kath and Kim (US) Opening", "American")]
    public void AResearchedCodeCountsAsMuchAsACountrysName(string code, string upload, string? contradicts)
    {
        var title = upload.StartsWith("Kath", StringComparison.Ordinal) ? "Kath and Kim" : "The Office";
        var identity = new MediaIdentity
        {
            ItemId = Guid.NewGuid(),
            Title = title,
            NormalizedTitle = TitleNormalizer.Normalize(title),
            Year = 2005,
            Kind = BaseItemKind.Series,
            Countries = new[] { code },
        };

        var found = CountryEdition.Contradiction(upload, identity);

        if (contradicts is null)
        {
            Assert.Null(found);
        }
        else
        {
            Assert.Contains(contradicts, found, StringComparison.Ordinal);
        }
    }

    // ---- helpers ----

    private static string Rows(params (string Imdb, string? Composer, string? Origin)[] rows)
    {
        var bindings = rows.Select(row =>
        {
            var cells = new Dictionary<string, object> { ["imdb"] = new { type = "literal", value = row.Imdb } };
            if (row.Composer is not null)
            {
                cells["composerLabel"] = new { type = "literal", value = row.Composer };
            }

            if (row.Origin is not null)
            {
                cells["origin"] = new { type = "literal", value = row.Origin };
            }

            return cells;
        });

        return JsonSerializer.Serialize(new { head = new { vars = Array.Empty<string>() }, results = new { bindings } });
    }

    private void WriteSnapshot(params ComposerCredits[] entries)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SnapshotPath, JsonSerializer.Serialize(new ComposerSnapshot { UpdatedUtc = DateTime.UtcNow, Entries = entries.ToList() }));
    }

    private ComposerCatalogue Catalogue(params ICreditsSource[] sources) =>
        new(sources, NullThemeForgeLogger<ComposerCatalogue>.Instance, SnapshotPath);

    private static CreditsAnswer Finds(string key, ResearchedCredits credits) =>
        new(new Dictionary<string, ResearchedCredits> { [key] = credits }, new HashSet<string>());

    private static Source Wikidata(Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> answer) =>
        new("Wikidata", 0, CreditsQuestion.Composers | CreditsQuestion.Theme | CreditsQuestion.Origin, answer);

    private sealed class Source : ICreditsSource
    {
        private readonly Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> _answer;

        public Source(string name, int order, CreditsQuestion answers, Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> answer)
        {
            Name = name;
            Order = order;
            Answers = answers;
            _answer = answer;
        }

        public string Name { get; }

        public int Order { get; }

        public CreditsQuestion Answers { get; }

        public List<IReadOnlyList<CreditsRequest>> Asked { get; } = new();

        public bool IsEnabled(PluginConfiguration configuration) => true;

        public Task<CreditsAnswer> LookUpAsync(IReadOnlyList<CreditsRequest> batch, PluginConfiguration configuration, IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Asked.Add(batch);
            return Task.FromResult(_answer(batch));
        }
    }

    /// <summary>Knows where a work was made, and nothing else.</summary>
    private sealed class Knows : IComposerCatalogue
    {
        private readonly ResearchedCredits _credits;

        public Knows(params string[] countries) => _credits = ResearchedCredits.None with { Countries = countries };

        public Task<ComposerSnapshot> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new ComposerSnapshot());

        public Task<ComposerSnapshot> SyncAsync(IReadOnlyList<CreditsRequest> works, IProgress<double>? progress, CancellationToken cancellationToken) =>
            GetAsync(cancellationToken);

        public ResearchedCredits Known(IReadOnlyList<string> keys) => _credits;
    }

    /// <summary>Jellyfin's own lookup. Countries are read off the item, so it needs no library.</summary>
    private static JellyfinPeopleLookup Jellyfin() => new(null!, NullThemeForgeLogger<JellyfinPeopleLookup>.Instance);
}
