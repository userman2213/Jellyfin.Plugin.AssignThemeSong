using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Jellyfin.Plugin.ThemeForge.Engines.Credits;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins how IMDb's dataset files become the composer table, and the table's file format.
/// </summary>
/// <remarks>
/// The rows are written the way IMDb's files are laid out -- the same columns, <c>\N</c> for
/// nothing, the characters column as JSON -- and carry the real credits for the titles that
/// matter here, which were checked against the files of 1 November 2026. They are not excerpts of
/// IMDb's files, which are licensed for personal use.
/// </remarks>
public sealed class ImdbCrewDatasetTests : IDisposable
{
    internal const string PrincipalsHeader = "tconst\tordering\tnconst\tcategory\tjob\tcharacters";
    internal const string EpisodesHeader = "tconst\tparentTconst\tseasonNumber\tepisodeNumber";
    internal const string NamesHeader = "nconst\tprimaryName\tbirthYear\tdeathYear\tprimaryProfession\tknownForTitles";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "themeforge-crew-" + Guid.NewGuid().ToString("N"));

    public ImdbCrewDatasetTests() => Directory.CreateDirectory(_root);

    /// <summary>Gets title.principals as IMDb lays it out, for the titles the tests are about.</summary>
    internal static string Principals => string.Join('\n', new[]
    {
        PrincipalsHeader,

        // Fight Club: the Dust Brothers, credited as a duo and as each member, in billing order.
        "tt0137523\t1\tnm0000093\tactor\t\\N\t[\"Tyler\"]",
        "tt0137523\t17\tnm1906503\tcomposer\tcomposer\t\\N",
        "tt0137523\t18\tnm0454887\tcomposer\tcomposer\t\\N",
        "tt0137523\t19\tnm0801117\tcomposer\tcomposer\t\\N",

        // Alien.
        "tt0078748\t17\tnm0000025\tcomposer\tcomposer\t\\N",

        // Lucky Strike (2026), whose soundtrack listing names only a western song.
        "tt19035928\t1\tnm9999901\tactor\t\\N\t[\"Somebody\"]",
        "tt19035928\t18\tnm0343935\tcomposer\t\\N\t\\N",

        // Breaking Bad's episodes credit Dave Porter; one also credits a guest composer.
        "tt0959621\t15\tnm1726663\tcomposer\t\\N\t\\N",
        "tt1054724\t15\tnm1726663\tcomposer\t\\N\t\\N",
        "tt1054725\t15\tnm1726663\tcomposer\t\\N\t\\N",
        "tt1054725\t16\tnm9999902\tcomposer\t\\N\t\\N",
        "tt1054726\t15\tnm1726663\tcomposer\t\\N\t\\N",

        // A show with one credited episode: that composer is not the show's.
        "tt0705891\t12\tnm1192859\tcomposer\t\\N\t\\N",

        // A title numbered higher than any credited one, credited with nothing that matters here.
        "tt40000000\t1\tnm9999903\tdirector\t\\N\t\\N",
    }) + '\n';

    /// <summary>Gets title.episode for the episodes above, and one uncredited episode.</summary>
    internal static string Episodes => string.Join('\n', new[]
    {
        EpisodesHeader,
        "tt0959621\ttt0903747\t1\t1",
        "tt1054724\ttt0903747\t1\t2",
        "tt1054725\ttt0903747\t1\t3",
        "tt1054726\ttt0903747\t1\t4",
        "tt1054727\ttt0903747\t1\t5",
        "tt0705891\ttt0141842\t1\t1",
        "tt0705892\ttt0141842\t1\t2",
    }) + '\n';

    /// <summary>Gets name.basics for the people above.</summary>
    internal static string Names => string.Join('\n', new[]
    {
        NamesHeader,
        "nm0000025\tJerry Goldsmith\t1929\t2004\tmusic_department,composer,actor\ttt0078748",
        "nm0000093\tBrad Pitt\t1963\t\\N\tproducer,actor,executive\ttt0137523",
        "nm0343935\tLarry Groupé\t\\N\t\\N\tcomposer,music_department\ttt19035928",
        "nm0454887\tJohn King\t\\N\t\\N\tcomposer,music_department\ttt0137523",
        "nm0801117\tMichael Simpson\t\\N\t\\N\tcomposer,music_department\ttt0137523",
        "nm1192859\tWill Edwards\t\\N\t\\N\tcomposer\ttt0141842",
        "nm1726663\tDave Porter\t\\N\t\\N\tcomposer,music_department\ttt0903747",
        "nm1906503\tDust Brothers\t\\N\t\\N\tcomposer,music_department\ttt0137523",
        "nm9999902\tA Guest Composer\t\\N\t\\N\tcomposer\t\\N",
    }) + '\n';

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

    // ---- ids ------------------------------------------------------------------------------

    [Theory]
    [InlineData("tt0137523", 137523)]
    [InlineData("tt19035928", 19035928)]
    [InlineData("TT0137523", 137523)]
    [InlineData(" tt0137523 ", 137523)]
    [InlineData("tt", -1)]
    [InlineData("nm0137523", -1)]
    [InlineData("tt01375x3", -1)]
    [InlineData("\\N", -1)]
    [InlineData("", -1)]
    [InlineData(null, -1)]
    [InlineData("tt99999999999", -1)]
    public void ReadsATitleIdAsJellyfinStoresIt(string? id, int expected) =>
        Assert.Equal(expected, ImdbCrewDataset.ParseTitleId(id));

    // ---- title.principals ----------------------------------------------------------------------

    [Fact]
    public void KeepsOnlyComposerCreditsInBillingOrder()
    {
        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(Principals), out _, CancellationToken.None);

        Assert.DoesNotContain(credits, credit => credit.Name == 93); // Brad Pitt, an actor
        Assert.Equal(
            new[] { 1906503, 454887, 801117 },
            credits.Where(credit => credit.Title == 137523).Select(credit => credit.Name));
        Assert.Equal(credits.OrderBy(c => c.Title).ThenBy(c => c.Ordering), credits);
    }

    [Fact]
    public void NotesTheNewestTitleInTheFileWhetherOrNotItCreditsAComposer()
    {
        ImdbCrewDataset.ReadComposerCredits(Stream(Principals), out var newest, CancellationToken.None);

        Assert.Equal(40000000, newest);
    }

    [Fact]
    public void ReadsWindowsLineEndingsAndALastLineWithoutOne()
    {
        var text = PrincipalsHeader + "\r\n" + "tt0078748\t17\tnm0000025\tcomposer\tcomposer\t\\N\r\n" + "tt0137523\t17\tnm1906503\tcomposer\tcomposer\t\\N";

        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(text), out _, CancellationToken.None);

        Assert.Equal(new[] { 78748, 137523 }, credits.Select(credit => credit.Title));
    }

    [Fact]
    public void RefusesAFileWhoseColumnsHaveMoved()
    {
        // Reading by position from a file IMDb had rearranged would credit actors as composers.
        var text = "tconst\tnconst\tordering\tcategory\tjob\tcharacters\ntt0078748\tnm0000025\t17\tcomposer\t\\N\t\\N\n";

        var error = Assert.Throws<InvalidDataException>(() => ImdbCrewDataset.ReadComposerCredits(Stream(text), out _, CancellationToken.None));

        Assert.Contains("title.principals", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SkipsALineThatIsNotACredit()
    {
        var text = PrincipalsHeader + "\n" + "garbage\n" + "tt0078748\t17\tnm0000025\tcomposer\t\\N\t\\N\n" + "ttX\t1\tnm1\tcomposer\t\\N\t\\N\n";

        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(text), out _, CancellationToken.None);

        Assert.Equal(new[] { 78748 }, credits.Select(credit => credit.Title));
    }

    [Fact]
    public void ReadsALineLongerThanItsBuffer()
    {
        // The characters column is free text; one enormous line must not lose the next.
        var huge = "tt0000001\t1\tnm0000001\tactor\t\\N\t[\"" + new string('x', 3 << 20) + "\"]";
        var text = PrincipalsHeader + "\n" + huge + "\n" + "tt0078748\t17\tnm0000025\tcomposer\t\\N\t\\N\n";

        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(text), out _, CancellationToken.None);

        Assert.Equal(new[] { 78748 }, credits.Select(credit => credit.Title));
    }

    // ---- folding episodes into their series ----------------------------------------------------

    [Fact]
    public void FindsTheSeriesOfOnlyTheEpisodesThatCreditSomebody()
    {
        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(Principals), out _, CancellationToken.None);

        var series = ImdbCrewDataset.ReadSeriesOfCreditedEpisodes(Stream(Episodes), credits, CancellationToken.None);

        Assert.Equal(5, series.Count);
        Assert.DoesNotContain(series, pair => pair.Episode == 1054727 || pair.Episode == 705892);
        Assert.All(series.Where(pair => pair.Episode != 705891), pair => Assert.Equal(903747, pair.Series));
    }

    [Fact]
    public void GivesASeriesTheComposerOfMostOfItsEpisodes()
    {
        var composed = Compose();

        Assert.Equal(new[] { 1726663 }, PeopleOf(composed, 903747));
    }

    [Fact]
    public void LeavesOutAComposerOfOneEpisode()
    {
        // The guest on one of Breaking Bad's four credited episodes, and The Sopranos' composer of
        // one episode out of 86, are nobody's idea of the show's composer.
        var composed = Compose();

        Assert.DoesNotContain(9999902, PeopleOf(composed, 903747));
        Assert.Empty(PeopleOf(composed, 141842));
    }

    [Fact]
    public void KeepsEpisodesOutOfTheTable()
    {
        var composed = Compose();

        Assert.DoesNotContain(959621, composed.Titles);
        Assert.Equal(1, composed.SeriesCount);
    }

    [Fact]
    public void KeepsAFilmsComposersInBillingOrder()
    {
        var composed = Compose();

        Assert.Equal(new[] { 1906503, 454887, 801117 }, PeopleOf(composed, 137523));
        Assert.Equal(new[] { 343935 }, PeopleOf(composed, 19035928));
    }

    [Fact]
    public void KeepsTwoComposersOfASeriesScoredByAPair()
    {
        var credits = new List<CrewCredit>();
        var episodes = new List<(int, int)>();
        for (var episode = 1; episode <= 10; episode++)
        {
            credits.Add(new CrewCredit(1000 + episode, 1, 77));
            credits.Add(new CrewCredit(1000 + episode, 2, 88));
            if (episode <= 2)
            {
                credits.Add(new CrewCredit(1000 + episode, 3, 99));
            }

            episodes.Add((1000 + episode, 5));
        }

        var composed = ImdbCrewDataset.Compose(credits, episodes);

        Assert.Equal(new[] { 77, 88 }, PeopleOf(composed, 5));
    }

    [Fact]
    public void ATitleCreditedInItsOwnRightKeepsItsOwnCredits()
    {
        var credits = new List<CrewCredit>
        {
            new(5, 1, 11),
            new(6, 1, 22),
            new(7, 1, 22),
        };

        var composed = ImdbCrewDataset.Compose(credits, new[] { (6, 5), (7, 5) });

        Assert.Equal(new[] { 11 }, PeopleOf(composed, 5));
        Assert.Equal(0, composed.SeriesCount);
    }

    // ---- the table on disk --------------------------------------------------------------------

    [Fact]
    public void LooksUpWhatWasWritten()
    {
        using var file = WriteAndOpen(Build());

        Assert.Equal(new[] { "Larry Groupé" }, file.Composers(19035928));
        Assert.Equal(new[] { "Dust Brothers", "John King", "Michael Simpson" }, file.Composers(137523));
        Assert.Equal(new[] { "Dave Porter" }, file.Composers(903747));
        Assert.Equal(new[] { "Jerry Goldsmith" }, file.Composers(78748));
    }

    [Fact]
    public void FindsNothingForATitleWithNoComposer()
    {
        using var file = WriteAndOpen(Build());

        Assert.Empty(file.Composers(141842));
        Assert.Empty(file.Composers(1));
        Assert.Empty(file.Composers(int.MaxValue));
    }

    [Fact]
    public void RecordsWhatTheFilesWereAndHowMuchTheyHeld()
    {
        using var file = WriteAndOpen(Build());

        Assert.Equal(new DateTime(2026, 11, 1, 0, 46, 33, DateTimeKind.Utc), file.PublishedUtc);
        Assert.Equal(40000000, file.NewestTitle);
        Assert.Equal(4, file.TitleCount);
        Assert.Equal(1, file.SeriesCount);
    }

    [Fact]
    public void FindsEveryTitleInALargeTable()
    {
        var titles = Enumerable.Range(0, 5000).Select(index => (index * 7) + 3).ToArray();
        var table = new ImdbCrewTable
        {
            Titles = titles,
            FirstCredit = Enumerable.Range(0, titles.Length + 1).ToArray(),
            Credits = Enumerable.Range(0, titles.Length).Select(index => index % 3).ToArray(),
            Names = new[] { "Ann", "Bea", "Cy" },
            NewestTitle = 99_999,
        };

        using var file = WriteAndOpen(table);

        for (var index = 0; index < titles.Length; index++)
        {
            Assert.Equal(table.Names[index % 3], Assert.Single(file.Composers(titles[index])));
            Assert.Empty(file.Composers(titles[index] + 1));
        }
    }

    [Fact]
    public void WillNotUseAFileThatWasCutShort()
    {
        var path = Path.Combine(_root, "short.bin");
        using (var output = File.Create(path))
        {
            ImdbCrewDataset.Write(Build(), output, DateTime.UtcNow);
        }

        using (var stream = new FileStream(path, FileMode.Open))
        {
            stream.SetLength(stream.Length - 3);
        }

        Assert.Null(ImdbCrewFile.Open(path, out var problem));
        Assert.Equal("the file is incomplete", problem);
    }

    [Fact]
    public void WillNotUseAFileItDidNotWrite()
    {
        var path = Path.Combine(_root, "other.bin");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)1, 200).ToArray());

        Assert.Null(ImdbCrewFile.Open(path, out var problem));
        Assert.Contains("not one this version", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysWhenThereIsNoCopy()
    {
        Assert.Null(ImdbCrewFile.Open(Path.Combine(_root, "missing.bin"), out var problem));
        Assert.Equal("it has not been downloaded", problem);
    }

    [Fact]
    public void LeavesOutSomebodyTheNameFileDoesNotList()
    {
        var composed = new ComposedCredits(new[] { 10, 20 }, new[] { 0, 1, 2 }, new[] { 1, 2 }, 0);
        var names = new Dictionary<int, string> { [1] = "Known Person" };

        var table = ImdbCrewDataset.Assemble(composed, names, 20, DateTime.UtcNow);

        Assert.Equal(new[] { 10 }, table.Titles);
        Assert.Equal(new[] { "Known Person" }, table.Names);
    }

    internal static MemoryStream Stream(string text) => new(Encoding.UTF8.GetBytes(text));

    internal static ImdbCrewTable Build()
    {
        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(Principals), out var newest, CancellationToken.None);
        var series = ImdbCrewDataset.ReadSeriesOfCreditedEpisodes(Stream(Episodes), credits, CancellationToken.None);
        var composed = ImdbCrewDataset.Compose(credits, series);
        var wanted = composed.People.Distinct().OrderBy(person => person).ToArray();
        var names = ImdbCrewDataset.ReadNames(Stream(Names), wanted, CancellationToken.None);
        return ImdbCrewDataset.Assemble(composed, names, newest, new DateTime(2026, 11, 1, 0, 46, 33, DateTimeKind.Utc));
    }

    private static ComposedCredits Compose()
    {
        var credits = ImdbCrewDataset.ReadComposerCredits(Stream(Principals), out _, CancellationToken.None);
        var series = ImdbCrewDataset.ReadSeriesOfCreditedEpisodes(Stream(Episodes), credits, CancellationToken.None);
        return ImdbCrewDataset.Compose(credits, series);
    }

    private static int[] PeopleOf(ComposedCredits composed, int title)
    {
        var at = Array.IndexOf(composed.Titles, title);
        return at < 0 ? Array.Empty<int>() : composed.People[composed.First[at]..composed.First[at + 1]];
    }

    private ImdbCrewFile WriteAndOpen(ImdbCrewTable table)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".bin");
        using (var output = File.Create(path))
        {
            ImdbCrewDataset.Write(table, output, DateTime.UtcNow);
        }

        return ImdbCrewFile.Open(path, out var problem) ?? throw new InvalidOperationException(problem);
    }
}
