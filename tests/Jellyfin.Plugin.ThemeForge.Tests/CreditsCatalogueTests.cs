using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers how the catalogue settles each question, and above all what it does when it cannot ask.
/// </summary>
/// <remarks>
/// Until 2.6 a Wikidata outage was recorded as "nobody knows" for every title in the library, for
/// a fortnight -- after sending every one of them to MusicBrainz at one request a second. These
/// tests pin the rule that replaced it: a question a source could not ask is left open, and only
/// one every applicable source actually asked, and could not answer, is a miss.
/// </remarks>
public sealed class CreditsCatalogueTests : IDisposable
{
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

    private static readonly CreditsRequest Battlestar =
        new("imdb:tt0407362", new[] { "imdb:tt0407362", "tmdbtv:1972" }, "tt0407362", "1972", true, "Battlestar Galactica");

    private static readonly CreditsRequest Sopranos =
        new("imdb:tt0141842", new[] { "imdb:tt0141842" }, "tt0141842", null, true, "The Sopranos");

    private static CreditsAnswer Finds(string key, ResearchedCredits credits) =>
        new(new Dictionary<string, ResearchedCredits> { [key] = credits }, new HashSet<string>());

    private static Source Wikidata(Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> answer) =>
        new("Wikidata", 0, CreditsQuestion.Composers | CreditsQuestion.Theme, answer);

    private static Source MusicBrainz(Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> answer) =>
        new("MusicBrainz", 10, CreditsQuestion.Composers, answer);

    private static Source Wikipedia(Func<IReadOnlyList<CreditsRequest>, CreditsAnswer> answer) =>
        new("Wikipedia", 20, CreditsQuestion.Theme, answer);

    private ComposerCatalogue Catalogue(params ICreditsSource[] sources) =>
        new(sources, NullThemeForgeLogger<ComposerCatalogue>.Instance, SnapshotPath);

    [Fact]
    public async Task ASourceThatCouldNotBeAskedIsNotTakenForOneThatKnowsNothing()
    {
        var wikidata = Wikidata(CreditsAnswer.FailedFor);
        var catalogue = Catalogue(wikidata);

        var snapshot = await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Null(snapshot.Find(Battlestar.Keys));
    }

    [Fact]
    public async Task WhatCouldNotBeAskedIsAskedAgainNextTime()
    {
        var wikidata = Wikidata(CreditsAnswer.FailedFor);
        var catalogue = Catalogue(wikidata);

        await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);
        await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Equal(2, wikidata.Asked.Count);
    }

    [Fact]
    public async Task ASourceThatThrowsCouldNotBeAsked()
    {
        var broken = new Source("Wikidata", 0, CreditsQuestion.Composers | CreditsQuestion.Theme, _ => throw new InvalidOperationException("down"));
        var snapshot = await Catalogue(broken).SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Null(snapshot.Find(Battlestar.Keys));
    }

    [Fact]
    public async Task AWikidataOutageDoesNotBecomeAMusicBrainzCrawl()
    {
        // MusicBrainz is one request a second. Whatever Wikidata could not ask about waits for the
        // next run, where it is asked about in order, rather than all going to MusicBrainz now.
        var musicBrainz = MusicBrainz(_ => CreditsAnswer.Nothing);

        await Catalogue(Wikidata(CreditsAnswer.FailedFor), musicBrainz).SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Empty(musicBrainz.Asked);
    }

    [Fact]
    public async Task AWorkEveryoneWasAskedAboutAndNobodyKnowsIsAMiss()
    {
        var catalogue = Catalogue(Wikidata(_ => CreditsAnswer.Nothing), MusicBrainz(_ => CreditsAnswer.Nothing), Wikipedia(_ => CreditsAnswer.Nothing));

        var snapshot = await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);
        var record = snapshot.Find(Battlestar.Keys);

        Assert.NotNull(record);
        Assert.Equal("nobody", record!.Source);
        Assert.Equal("nobody", record.ThemeSource);
        Assert.False(snapshot.NeedsLookUp(Battlestar.Keys, DateTime.UtcNow.AddDays(13)));
    }

    [Fact]
    public async Task APartAnsweredIsKeptWhenAnotherPartCouldNotBeAsked()
    {
        // Wikidata names the composer; Wikipedia is rate limited. The composer is kept, and only the
        // theme is left open.
        var catalogue = Catalogue(
            Wikidata(_ => Finds(Sopranos.Key, new ResearchedCredits(new[] { "Alabama 3" }, null, null) { WikipediaTitle = "The Sopranos" })),
            Wikipedia(CreditsAnswer.FailedFor));

        var snapshot = await catalogue.SyncAsync(new[] { Sopranos }, null, CancellationToken.None);

        Assert.Equal(new[] { "Alabama 3" }, snapshot.Find(Sopranos.Keys)!.Composers);
        Assert.False(snapshot.NeedsComposers(Sopranos.Keys, DateTime.UtcNow));
        Assert.True(snapshot.NeedsTheme(Sopranos.Keys, DateTime.UtcNow));
    }

    [Fact]
    public async Task TheArticleWikidataFoundIsHandedToWikipedia()
    {
        var wikipedia = Wikipedia(batch => Finds(
            batch[0].Key,
            ResearchedCredits.None with { Theme = new ThemeSong("Woke Up This Morning", "Alabama 3") }));

        var catalogue = Catalogue(
            Wikidata(_ => Finds(Sopranos.Key, ResearchedCredits.None with { WikipediaTitle = "The Sopranos" })),
            wikipedia);

        var snapshot = await catalogue.SyncAsync(new[] { Sopranos }, null, CancellationToken.None);

        Assert.Equal("The Sopranos", Assert.Single(Assert.Single(wikipedia.Asked)).WikipediaTitle);
        Assert.Equal(new ThemeSong("Woke Up This Morning", "Alabama 3"), catalogue.Known(Sopranos.Keys).Theme);
        Assert.Equal("Wikipedia", snapshot.Find(Sopranos.Keys)!.ThemeSource);
    }

    [Fact]
    public async Task AThemeWikidataKnowsIsNotAskedOfWikipedia()
    {
        var wikipedia = Wikipedia(_ => CreditsAnswer.Nothing);
        var catalogue = Catalogue(
            Wikidata(_ => Finds(Battlestar.Key, ResearchedCredits.None with { Theme = new ThemeSong("Main Title", "Bear McCreary"), WikipediaTitle = "Battlestar Galactica (2004 TV series)" })),
            wikipedia);

        await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Empty(wikipedia.Asked);
    }

    [Fact]
    public async Task ACacheWrittenBy25StillLoadsAndOnlyTheNewQuestionIsAsked()
    {
        // 2.5 wrote composers with no theme fields. Its answers stand; the theme, which it never
        // asked about, is asked once -- and MusicBrainz, the slow source, is not troubled again.
        Directory.CreateDirectory(_directory);
        var asked = DateTime.UtcNow.AddDays(-3).ToString("O");
        File.WriteAllText(SnapshotPath, $$"""
            {"UpdatedUtc":"{{asked}}","Entries":[
              {"Key":"imdb:tt0407362","Composers":["Bear McCreary"],"Artist":null,"ReleaseGroupId":null,"Source":"Wikidata","LookedUpUtc":"{{asked}}"},
              {"Key":"tmdbtv:1972","Composers":["Bear McCreary"],"Artist":null,"ReleaseGroupId":null,"Source":"Wikidata","LookedUpUtc":"{{asked}}"}]}
            """);

        var wikidata = Wikidata(_ => CreditsAnswer.Nothing);
        var musicBrainz = MusicBrainz(_ => CreditsAnswer.Nothing);
        var catalogue = Catalogue(wikidata, musicBrainz);

        Assert.Equal(new[] { "Bear McCreary" }, catalogue.Known(Battlestar.Keys).Composers);

        await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Single(wikidata.Asked);
        Assert.Empty(musicBrainz.Asked);
        Assert.Equal(new[] { "Bear McCreary" }, catalogue.Known(Battlestar.Keys).Composers);
    }

    [Fact]
    public async Task ASearchDuringASyncReadsTheCacheAsItWas()
    {
        // The sync works on a copy and swaps it in at the end. A search that resolves an item while
        // a source is being asked must see yesterday's cache, whole, and never a half-written one.
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SnapshotPath, """
            {"UpdatedUtc":"2020-01-01T00:00:00Z","Entries":[
              {"Key":"imdb:tt0407362","Composers":["Richard Gibbs"],"Source":"Wikidata","LookedUpUtc":"2020-01-01T00:00:00Z"}]}
            """);

        ComposerCatalogue? catalogue = null;
        IReadOnlyList<string>? seenMeanwhile = null;
        var wikidata = Wikidata(_ =>
        {
            seenMeanwhile = catalogue!.Known(Battlestar.Keys).Composers;
            return Finds(Battlestar.Key, new ResearchedCredits(new[] { "Bear McCreary" }, null, null));
        });

        catalogue = Catalogue(wikidata);
        await catalogue.SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Equal(new[] { "Richard Gibbs" }, seenMeanwhile);
        Assert.Equal(new[] { "Bear McCreary" }, catalogue.Known(Battlestar.Keys).Composers);
    }

    [Fact]
    public async Task ASyncThatCouldAskNobodyDoesNotLookLikeARefresh()
    {
        // The cache's age is what tells a run to refresh it first. A sync that settled nothing must
        // not reset it, or a run would trust a refresh that never happened.
        var snapshot = await Catalogue(Wikidata(CreditsAnswer.FailedFor)).SyncAsync(new[] { Battlestar }, null, CancellationToken.None);

        Assert.Equal(default, snapshot.UpdatedUtc);
    }
}
