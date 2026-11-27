using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>One composer credit as IMDb's <c>title.principals</c> file lists it.</summary>
/// <param name="Title">The title's IMDb number, the digits of its <c>tt</c> id.</param>
/// <param name="Ordering">Where IMDb bills the credit among the title's principals.</param>
/// <param name="Name">The person's IMDb number, the digits of their <c>nm</c> id.</param>
internal readonly record struct CrewCredit(int Title, int Ordering, int Name);

/// <summary>Every title's composers, by IMDb number, before the names are read.</summary>
/// <param name="Titles">The titles, ascending.</param>
/// <param name="First">Where each title's people start in <paramref name="People"/>, with one more marking the end.</param>
/// <param name="People">The people credited, title by title.</param>
/// <param name="SeriesCount">How many of the titles are series whose composers came from their episodes.</param>
internal sealed record ComposedCredits(int[] Titles, int[] First, int[] People, int SeriesCount);

/// <summary>What ThemeForge keeps from IMDb's dataset files: who composed what, and nothing else.</summary>
internal sealed class ImdbCrewTable
{
    /// <summary>Gets or sets the titles, by IMDb number, ascending.</summary>
    public int[] Titles { get; set; } = Array.Empty<int>();

    /// <summary>Gets or sets where each title's credits start, with one more entry marking the end.</summary>
    public int[] FirstCredit { get; set; } = { 0 };

    /// <summary>Gets or sets every credit, as a position in <see cref="Names"/>, title by title in billing order.</summary>
    public int[] Credits { get; set; } = Array.Empty<int>();

    /// <summary>Gets or sets the names of the people credited.</summary>
    public string[] Names { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the highest title number anywhere in the files, so a newer title can be told from one with no composer.</summary>
    public int NewestTitle { get; set; }

    /// <summary>Gets or sets how many of the titles are series whose composers were gathered from their episodes.</summary>
    public int SeriesCount { get; set; }

    /// <summary>Gets or sets when IMDb published the files.</summary>
    public DateTime PublishedUtc { get; set; }
}

/// <summary>
/// Reads IMDb's published dataset files into the composer table, and writes and reads that table.
/// </summary>
/// <remarks>
/// <para>
/// IMDb publishes its credits at <c>datasets.imdbws.com</c>, refreshed daily, for personal and
/// non-commercial use. Three files are read: <c>title.principals</c> for the credits,
/// <c>title.episode</c> to fold an episode's composer into its series -- IMDb credits a series'
/// composer on each episode, never on the series itself -- and <c>name.basics</c> for the names.
/// </para>
/// <para>
/// They are read as bytes, a line at a time: <c>title.principals</c> runs to a hundred million
/// lines, of which three in a hundred are composer credits, and a string for each would be most of
/// the work. Measured on the files of 1 November 2026: 3.2 million composer credits, two thirds of
/// them on episodes, leaving about 860,000 films, shorts and specials and the series built from the
/// rest; 375,000 composers, whose names come to 5 MB.
/// </para>
/// </remarks>
internal static class ImdbCrewDataset
{
    /// <summary>Where IMDb publishes the files.</summary>
    public const string BaseUrl = "https://datasets.imdbws.com/";

    /// <summary>The credits, one line per person per title.</summary>
    public const string PrincipalsFile = "title.principals.tsv.gz";

    /// <summary>Which series each episode belongs to.</summary>
    public const string EpisodesFile = "title.episode.tsv.gz";

    /// <summary>Everybody's name.</summary>
    public const string NamesFile = "name.basics.tsv.gz";

    /// <summary>How many of a series' composers are kept: whoever scored most of it, and a partner.</summary>
    public const int MaxSeriesComposers = 2;

    /// <summary>The fewest episodes a series must credit somebody on for them to be its composer.</summary>
    public const int MinSeriesEpisodes = 2;

    /// <summary>The file format's first four bytes.</summary>
    internal static readonly byte[] Magic = "TFIC"u8.ToArray();

    /// <summary>The file format's version.</summary>
    internal const int Version = 1;

    /// <summary>The size of the header.</summary>
    internal const int HeaderSize = 64;

    /// <summary>Reads the digits of an IMDb id such as <c>tt0137523</c> or <c>nm0343935</c>.</summary>
    /// <param name="id">The id.</param>
    /// <param name="first">The first letter of its prefix.</param>
    /// <param name="second">The second letter of its prefix.</param>
    /// <returns>The number, or -1 when it is not such an id.</returns>
    public static int ParseId(ReadOnlySpan<byte> id, byte first, byte second)
    {
        if (id.Length < 3 || id.Length > 12 || id[0] != first || id[1] != second)
        {
            return -1;
        }

        long number = 0;
        foreach (var digit in id[2..])
        {
            if (digit < (byte)'0' || digit > (byte)'9')
            {
                return -1;
            }

            number = (number * 10) + (digit - '0');
        }

        return number > int.MaxValue ? -1 : (int)number;
    }

    /// <summary>Reads the digits of a title's IMDb id as Jellyfin stores it.</summary>
    /// <param name="imdbId">The id, such as <c>tt19035928</c>.</param>
    /// <returns>The number, or -1 when there is no usable id.</returns>
    public static int ParseTitleId(string? imdbId)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return -1;
        }

        var trimmed = imdbId.Trim();
        Span<byte> ascii = stackalloc byte[16];
        if (trimmed.Length > ascii.Length)
        {
            return -1;
        }

        for (var index = 0; index < trimmed.Length; index++)
        {
            var character = char.ToLowerInvariant(trimmed[index]);
            if (character > 127)
            {
                return -1;
            }

            ascii[index] = (byte)character;
        }

        return ParseId(ascii[..trimmed.Length], (byte)'t', (byte)'t');
    }

    /// <summary>Reads every composer credit out of <c>title.principals</c>.</summary>
    /// <param name="principals">The file, decompressed.</param>
    /// <param name="newestTitle">The highest title number in the file, credited or not.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="expected">
    /// Roughly how many credits to expect. A list of millions that grows by doubling leaves each
    /// array it outgrew on the large object heap, which is most of what such a read costs in memory.
    /// </param>
    /// <returns>The composer credits, sorted by title and billing order.</returns>
    /// <exception cref="InvalidDataException">The file is not laid out as IMDb documents it.</exception>
    public static List<CrewCredit> ReadComposerCredits(Stream principals, out int newestTitle, CancellationToken cancellationToken, int expected = 0)
    {
        ArgumentNullException.ThrowIfNull(principals);

        var reader = new TsvLineReader(principals);
        ExpectHeader(reader, PrincipalsFile, "tconst", "ordering", "nconst", "category");

        var credits = new List<CrewCredit>(Math.Max(expected, 1 << 10));
        var newest = 0;
        var lines = 0;

        while (reader.TryReadLine(out var line))
        {
            if ((++lines & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!TrySplit(line, out var titleField, out var orderingField, out var nameField, out var categoryField))
            {
                continue;
            }

            var title = ParseId(titleField, (byte)'t', (byte)'t');
            if (title < 0)
            {
                continue;
            }

            if (title > newest)
            {
                newest = title;
            }

            if (!categoryField.SequenceEqual("composer"u8))
            {
                continue;
            }

            var name = ParseId(nameField, (byte)'n', (byte)'m');
            if (name < 0)
            {
                continue;
            }

            credits.Add(new CrewCredit(title, ParseNumber(orderingField), name));
        }

        newestTitle = newest;

        // The file comes in this order; sorted regardless, since everything after depends on it.
        CollectionsMarshal.AsSpan(credits).Sort(CompareCredits);
        return credits;
    }

    /// <summary>
    /// Reads <c>title.episode</c> for the series of each credited episode.
    /// </summary>
    /// <param name="episodes">The file, decompressed.</param>
    /// <param name="credits">The composer credits, sorted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each credited episode's number and its series' number, sorted by episode.</returns>
    /// <exception cref="InvalidDataException">The file is not laid out as IMDb documents it.</exception>
    public static List<(int Episode, int Series)> ReadSeriesOfCreditedEpisodes(
        Stream episodes,
        IReadOnlyList<CrewCredit> credits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(credits);

        var credited = DistinctTitles(credits);
        var reader = new TsvLineReader(episodes);
        ExpectHeader(reader, EpisodesFile, "tconst", "parentTconst");

        // At most one for each credited title, so it never has to grow.
        var found = new List<(int Episode, int Series)>(credited.Length);
        var lines = 0;
        while (reader.TryReadLine(out var line))
        {
            if ((++lines & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var tab = line.IndexOf((byte)'\t');
            if (tab < 0)
            {
                continue;
            }

            var episode = ParseId(line[..tab], (byte)'t', (byte)'t');
            if (episode < 0 || Array.BinarySearch(credited, episode) < 0)
            {
                continue;
            }

            var rest = line[(tab + 1)..];
            var end = rest.IndexOf((byte)'\t');
            var series = ParseId(end < 0 ? rest : rest[..end], (byte)'t', (byte)'t');
            if (series >= 0)
            {
                found.Add((episode, series));
            }
        }

        CollectionsMarshal.AsSpan(found).Sort((left, right) => left.Episode.CompareTo(right.Episode));
        return found;
    }

    /// <summary>
    /// Works out every title's composers: its own credits, or for a series, those gathered from
    /// its episodes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A series' composer is whoever is credited on at least a quarter of the episodes that credit
    /// anybody, and on at least <see cref="MinSeriesEpisodes"/>, most episodes first, at most
    /// <see cref="MaxSeriesComposers"/>: the person who scored the show rather than one who scored a
    /// single episode. Measured on the files of 1 November 2026: Breaking Bad gives Dave Porter (62
    /// of 62 episodes), Lost Michael Giacchino, Game of Thrones Ramin Djawadi, Battlestar Galactica
    /// Bear McCreary (71 of 73; Richard Gibbs, on 2, is left out). A title credited in its own right
    /// keeps its own credits.
    /// </para>
    /// <para>
    /// Built as flat arrays rather than an object per title: there are nearly a million titles, and
    /// this runs on servers that are not large.
    /// </para>
    /// </remarks>
    /// <param name="credits">The composer credits, sorted.</param>
    /// <param name="episodeSeries">The series of each credited episode, sorted by episode.</param>
    /// <returns>The titles and their composers' IMDb numbers.</returns>
    public static ComposedCredits Compose(
        IReadOnlyList<CrewCredit> credits,
        IReadOnlyList<(int Episode, int Series)> episodeSeries)
    {
        ArgumentNullException.ThrowIfNull(credits);
        ArgumentNullException.ThrowIfNull(episodeSeries);

        var episodeCount = episodeSeries.Count;

        // Sized up front, for the reason given on ReadComposerCredits.
        var titles = new List<int>(Math.Max(16, credits.Count - episodeCount));
        var first = new List<int>(Math.Max(16, credits.Count - episodeCount + 1));
        var people = new List<int>(Math.Max(16, credits.Count - episodeCount));

        // One entry per credited episode, naming its series; one per episode and person, naming both.
        var seriesEpisodes = new List<int>(Math.Max(16, episodeCount));
        var seriesPeople = new List<long>(Math.Max(16, episodeCount + (episodeCount / 4)));

        var scratch = new List<int>(8);
        var start = 0;
        while (start < credits.Count)
        {
            var title = credits[start].Title;
            scratch.Clear();

            var end = start;
            while (end < credits.Count && credits[end].Title == title)
            {
                if (!scratch.Contains(credits[end].Name))
                {
                    scratch.Add(credits[end].Name);
                }

                end++;
            }

            var at = FindEpisode(episodeSeries, title);
            if (at >= 0)
            {
                var series = episodeSeries[at].Series;
                seriesEpisodes.Add(series);
                foreach (var person in scratch)
                {
                    seriesPeople.Add(((long)series << 32) | (uint)person);
                }
            }
            else
            {
                titles.Add(title);
                first.Add(people.Count);
                people.AddRange(scratch);
            }

            start = end;
        }

        first.Add(people.Count);

        return Merge(titles, first, people, SeriesComposers(seriesEpisodes, seriesPeople));
    }

    /// <summary>Finds an episode among the sorted pairs, or returns -1.</summary>
    private static int FindEpisode(IReadOnlyList<(int Episode, int Series)> pairs, int episode)
    {
        var low = 0;
        var high = pairs.Count - 1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var found = pairs[middle].Episode;
            if (found == episode)
            {
                return middle;
            }

            if (found < episode)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>Picks each series' composers from the episode counts.</summary>
    private static (List<int> Titles, List<int> First, List<int> People) SeriesComposers(List<int> seriesEpisodes, List<long> seriesPeople)
    {
        CollectionsMarshal.AsSpan(seriesEpisodes).Sort();
        CollectionsMarshal.AsSpan(seriesPeople).Sort();

        var titles = new List<int>();
        var first = new List<int>();
        var people = new List<int>();
        var counted = new List<(int Person, int Episodes)>();

        var episodeAt = 0;
        var index = 0;
        while (index < seriesPeople.Count)
        {
            var series = (int)(seriesPeople[index] >> 32);

            // How many of its episodes credit anybody.
            while (episodeAt < seriesEpisodes.Count && seriesEpisodes[episodeAt] < series)
            {
                episodeAt++;
            }

            var credited = 0;
            while (episodeAt < seriesEpisodes.Count && seriesEpisodes[episodeAt] == series)
            {
                credited++;
                episodeAt++;
            }

            // How many credit each person.
            counted.Clear();
            while (index < seriesPeople.Count && (int)(seriesPeople[index] >> 32) == series)
            {
                var person = (int)(uint)seriesPeople[index];
                var episodes = 0;
                while (index < seriesPeople.Count && seriesPeople[index] == (((long)series << 32) | (uint)person))
                {
                    episodes++;
                    index++;
                }

                counted.Add((person, episodes));
            }

            // Two episodes at least: The Sopranos credits a composer on one episode of 86, and he
            // is not the composer of The Sopranos.
            var chosen = counted
                .Where(count => count.Episodes >= MinSeriesEpisodes && count.Episodes * 4 >= credited)
                .OrderByDescending(count => count.Episodes)
                .ThenBy(count => count.Person)
                .Take(MaxSeriesComposers)
                .Select(count => count.Person)
                .ToList();

            if (chosen.Count > 0)
            {
                titles.Add(series);
                first.Add(people.Count);
                people.AddRange(chosen);
            }
        }

        first.Add(people.Count);
        return (titles, first, people);
    }

    /// <summary>Merges the titles credited in their own right with the series, in title order.</summary>
    private static ComposedCredits Merge(
        List<int> titles,
        List<int> first,
        List<int> people,
        (List<int> Titles, List<int> First, List<int> People) series)
    {
        // Counted first, so the result is written straight into arrays of the right size.
        var titleCount = titles.Count;
        var peopleCount = people.Count;
        for (int own = 0, gathered = 0; gathered < series.Titles.Count; gathered++)
        {
            while (own < titles.Count && titles[own] < series.Titles[gathered])
            {
                own++;
            }

            if (own >= titles.Count || titles[own] != series.Titles[gathered])
            {
                titleCount++;
                peopleCount += series.First[gathered + 1] - series.First[gathered];
            }
        }

        var mergedTitles = new int[titleCount];
        var mergedFirst = new int[titleCount + 1];
        var mergedPeople = new int[peopleCount];
        var written = 0;
        var placed = 0;
        var seriesCount = 0;

        var next = 0;
        var nextSeries = 0;
        while (next < titles.Count || nextSeries < series.Titles.Count)
        {
            var takeOwn = nextSeries >= series.Titles.Count
                || (next < titles.Count && titles[next] <= series.Titles[nextSeries]);

            if (takeOwn)
            {
                // A title credited in its own right keeps its own credits.
                if (nextSeries < series.Titles.Count && series.Titles[nextSeries] == titles[next])
                {
                    nextSeries++;
                }

                mergedTitles[written] = titles[next];
                mergedFirst[written++] = placed;
                for (var at = first[next]; at < first[next + 1]; at++)
                {
                    mergedPeople[placed++] = people[at];
                }

                next++;
            }
            else
            {
                mergedTitles[written] = series.Titles[nextSeries];
                mergedFirst[written++] = placed;
                for (var at = series.First[nextSeries]; at < series.First[nextSeries + 1]; at++)
                {
                    mergedPeople[placed++] = series.People[at];
                }

                seriesCount++;
                nextSeries++;
            }
        }

        mergedFirst[written] = placed;

        return new ComposedCredits(mergedTitles, mergedFirst, mergedPeople, seriesCount);
    }

    /// <summary>Reads the names of the people wanted out of <c>name.basics</c>.</summary>
    /// <param name="names">The file, decompressed.</param>
    /// <param name="wanted">The IMDb numbers of the people wanted, sorted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Each wanted person's name, by number.</returns>
    /// <exception cref="InvalidDataException">The file is not laid out as IMDb documents it.</exception>
    public static Dictionary<int, string> ReadNames(Stream names, int[] wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(names);
        ArgumentNullException.ThrowIfNull(wanted);

        var reader = new TsvLineReader(names);
        ExpectHeader(reader, NamesFile, "nconst", "primaryName");

        var found = new Dictionary<int, string>(wanted.Length);
        var lines = 0;
        while (reader.TryReadLine(out var line))
        {
            if ((++lines & 0xFFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var tab = line.IndexOf((byte)'\t');
            if (tab < 0)
            {
                continue;
            }

            var person = ParseId(line[..tab], (byte)'n', (byte)'m');
            if (person < 0 || Array.BinarySearch(wanted, person) < 0)
            {
                continue;
            }

            var rest = line[(tab + 1)..];
            var end = rest.IndexOf((byte)'\t');
            var name = Encoding.UTF8.GetString(end < 0 ? rest : rest[..end]).Trim();
            if (name.Length > 0 && name != "\\N")
            {
                found[person] = name;
            }
        }

        return found;
    }

    /// <summary>Puts the pieces together into the table that is kept.</summary>
    /// <param name="composed">Each title's composers, by IMDb number.</param>
    /// <param name="names">The names of the people.</param>
    /// <param name="newestTitle">The highest title number in the files.</param>
    /// <param name="publishedUtc">When IMDb published the files.</param>
    /// <returns>The table.</returns>
    public static ImdbCrewTable Assemble(
        ComposedCredits composed,
        IReadOnlyDictionary<int, string> names,
        int newestTitle,
        DateTime publishedUtc)
    {
        ArgumentNullException.ThrowIfNull(composed);
        ArgumentNullException.ThrowIfNull(names);

        var people = composed.People.Where(names.ContainsKey).Distinct().ToArray();
        Array.Sort(people);

        var titles = new List<int>(composed.Titles.Length);
        var first = new List<int>(composed.Titles.Length + 1);
        var credits = new List<int>(composed.People.Length);

        for (var index = 0; index < composed.Titles.Length; index++)
        {
            var before = credits.Count;
            for (var at = composed.First[index]; at < composed.First[index + 1]; at++)
            {
                // Somebody name.basics does not list is left out rather than shown as a number.
                var position = Array.BinarySearch(people, composed.People[at]);
                if (position >= 0)
                {
                    credits.Add(position);
                }
            }

            if (credits.Count > before)
            {
                titles.Add(composed.Titles[index]);
                first.Add(before);
            }
        }

        first.Add(credits.Count);

        return new ImdbCrewTable
        {
            Titles = titles.ToArray(),
            FirstCredit = first.ToArray(),
            Credits = credits.ToArray(),
            Names = people.Select(person => names[person]).ToArray(),
            NewestTitle = newestTitle,
            SeriesCount = composed.SeriesCount,
            PublishedUtc = publishedUtc,
        };
    }

    /// <summary>Writes the table in the form <see cref="ImdbCrewFile"/> reads.</summary>
    /// <param name="table">The table.</param>
    /// <param name="output">Where to write it.</param>
    /// <param name="builtUtc">When it was built.</param>
    public static void Write(ImdbCrewTable table, Stream output, DateTime builtUtc)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(output);

        var nameBytes = table.Names.Select(name => Encoding.UTF8.GetBytes(name)).ToArray();
        var blobLength = nameBytes.Sum(bytes => (long)bytes.Length);

        Span<byte> header = stackalloc byte[HeaderSize];
        header.Clear();
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt64LittleEndian(header[8..], table.PublishedUtc.Ticks);
        BinaryPrimitives.WriteInt64LittleEndian(header[16..], builtUtc.Ticks);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], table.NewestTitle);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], table.Titles.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[32..], table.Credits.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header[36..], table.Names.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header[40..], blobLength);
        BinaryPrimitives.WriteInt32LittleEndian(header[48..], table.SeriesCount);
        output.Write(header);

        using var writer = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        // The titles, each with where its credits start, then one past the last as the end mark.
        for (var index = 0; index < table.Titles.Length; index++)
        {
            writer.Write(table.Titles[index]);
            writer.Write(table.FirstCredit[index]);
        }

        writer.Write(int.MaxValue);
        writer.Write(table.Credits.Length);

        foreach (var credit in table.Credits)
        {
            writer.Write(credit);
        }

        var offset = 0;
        foreach (var bytes in nameBytes)
        {
            writer.Write(offset);
            offset += bytes.Length;
        }

        writer.Write(offset);

        foreach (var bytes in nameBytes)
        {
            writer.Write(bytes);
        }

        writer.Flush();
    }

    /// <summary>Orders credits by title, then billing.</summary>
    internal static int CompareCredits(CrewCredit left, CrewCredit right) =>
        left.Title != right.Title ? left.Title.CompareTo(right.Title) : left.Ordering.CompareTo(right.Ordering);

    private static int[] DistinctTitles(IReadOnlyList<CrewCredit> credits)
    {
        var count = 0;
        for (var index = 0; index < credits.Count; index++)
        {
            if (index == 0 || credits[index].Title != credits[index - 1].Title)
            {
                count++;
            }
        }

        var titles = new int[count];
        var written = 0;
        for (var index = 0; index < credits.Count; index++)
        {
            if (index == 0 || credits[index].Title != credits[index - 1].Title)
            {
                titles[written++] = credits[index].Title;
            }
        }

        return titles;
    }

    /// <summary>Checks a file's first line names the columns this reads, where it reads them.</summary>
    private static void ExpectHeader(TsvLineReader reader, string file, params string[] columns)
    {
        if (!reader.TryReadLine(out var line))
        {
            throw new InvalidDataException(file + " is empty.");
        }

        var actual = Encoding.UTF8.GetString(line).Split('\t');
        for (var index = 0; index < columns.Length; index++)
        {
            if (index >= actual.Length || !string.Equals(actual[index], columns[index], StringComparison.Ordinal))
            {
                throw new InvalidDataException(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} is not laid out as expected: column {1} is \"{2}\", not \"{3}\". IMDb may have changed the file.",
                    file,
                    index + 1,
                    index < actual.Length ? actual[index] : string.Empty,
                    columns[index]));
            }
        }
    }

    /// <summary>Splits off the first four fields of a <c>title.principals</c> line.</summary>
    private static bool TrySplit(
        ReadOnlySpan<byte> line,
        out ReadOnlySpan<byte> first,
        out ReadOnlySpan<byte> second,
        out ReadOnlySpan<byte> third,
        out ReadOnlySpan<byte> fourth)
    {
        first = second = third = fourth = default;

        var tab = line.IndexOf((byte)'\t');
        if (tab < 0)
        {
            return false;
        }

        first = line[..tab];
        line = line[(tab + 1)..];

        tab = line.IndexOf((byte)'\t');
        if (tab < 0)
        {
            return false;
        }

        second = line[..tab];
        line = line[(tab + 1)..];

        tab = line.IndexOf((byte)'\t');
        if (tab < 0)
        {
            return false;
        }

        third = line[..tab];
        line = line[(tab + 1)..];

        tab = line.IndexOf((byte)'\t');
        fourth = tab < 0 ? line : line[..tab];
        return true;
    }

    private static int ParseNumber(ReadOnlySpan<byte> field)
    {
        var number = 0;
        foreach (var digit in field)
        {
            if (digit < (byte)'0' || digit > (byte)'9' || number > 100_000_000)
            {
                return int.MaxValue;
            }

            number = (number * 10) + (digit - '0');
        }

        return number;
    }
}

/// <summary>Reads a tab-separated file a line at a time, as bytes.</summary>
/// <remarks>A line is valid only until the next is read.</remarks>
internal sealed class TsvLineReader
{
    private readonly Stream _stream;
    private byte[] _buffer = new byte[1 << 20];
    private int _start;
    private int _end;
    private bool _finished;

    /// <summary>Initializes a new instance of the <see cref="TsvLineReader"/> class.</summary>
    /// <param name="stream">The file, decompressed.</param>
    public TsvLineReader(Stream stream) => _stream = stream;

    /// <summary>Reads the next line, without its line ending.</summary>
    /// <param name="line">The line, valid until the next call.</param>
    /// <returns><see langword="false"/> at the end of the file.</returns>
    public bool TryReadLine(out ReadOnlySpan<byte> line)
    {
        while (true)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            var newline = pending.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                line = WithoutCarriageReturn(pending[..newline]);
                _start += newline + 1;
                return true;
            }

            if (_finished)
            {
                if (pending.Length > 0)
                {
                    line = WithoutCarriageReturn(pending);
                    _start = _end;
                    return true;
                }

                line = default;
                return false;
            }

            Fill();
        }
    }

    private static ReadOnlySpan<byte> WithoutCarriageReturn(ReadOnlySpan<byte> line) =>
        line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;

    private void Fill()
    {
        var left = _end - _start;
        if (_start > 0)
        {
            _buffer.AsSpan(_start, left).CopyTo(_buffer);
            _start = 0;
            _end = left;
        }

        if (_end == _buffer.Length)
        {
            // One line longer than the whole buffer.
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }

        var read = _stream.Read(_buffer, _end, _buffer.Length - _end);
        if (read == 0)
        {
            _finished = true;
        }
        else
        {
            _end += read;
        }
    }
}
