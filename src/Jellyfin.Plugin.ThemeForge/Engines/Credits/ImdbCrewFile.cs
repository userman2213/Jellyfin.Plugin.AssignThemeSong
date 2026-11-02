using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>
/// Looks up a title's composers in the table <see cref="ImdbCrewDataset.Write"/> wrote, reading
/// only the few bytes each lookup needs.
/// </summary>
/// <remarks>
/// <para>
/// The table holds nearly a million titles. Only the header is kept in memory; a lookup is a
/// binary search over fixed-size records on disk, which the operating system caches. Positional
/// reads keep it safe to call from any number of threads at once.
/// </para>
/// <para>
/// The layout, little-endian: a 64-byte header; one 8-byte record per title (its number, where
/// its credits start) plus an end record; one 4-byte position per credit; one 4-byte offset per
/// name plus an end offset; then the names, in UTF-8. A file whose length does not add up is not
/// used at all.
/// </para>
/// </remarks>
internal sealed class ImdbCrewFile : IDisposable
{
    private const int RecordSize = 8;

    private readonly SafeFileHandle _handle;
    private readonly long _creditsAt;
    private readonly long _offsetsAt;
    private readonly long _namesAt;

    private ImdbCrewFile(SafeFileHandle handle, ReadOnlySpan<byte> header, long length)
    {
        _handle = handle;
        PublishedUtc = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header[8..]), DateTimeKind.Utc);
        BuiltUtc = new DateTime(BinaryPrimitives.ReadInt64LittleEndian(header[16..]), DateTimeKind.Utc);
        NewestTitle = BinaryPrimitives.ReadInt32LittleEndian(header[24..]);
        TitleCount = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
        CreditCount = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
        NameCount = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
        SeriesCount = BinaryPrimitives.ReadInt32LittleEndian(header[48..]);
        Length = length;

        _creditsAt = ImdbCrewDataset.HeaderSize + ((TitleCount + 1L) * RecordSize);
        _offsetsAt = _creditsAt + (CreditCount * 4L);
        _namesAt = _offsetsAt + ((NameCount + 1L) * 4);
    }

    /// <summary>Gets when IMDb published the files the table was built from.</summary>
    public DateTime PublishedUtc { get; }

    /// <summary>Gets when the table was built.</summary>
    public DateTime BuiltUtc { get; }

    /// <summary>Gets the highest title number in the files.</summary>
    public int NewestTitle { get; }

    /// <summary>Gets how many titles have a composer.</summary>
    public int TitleCount { get; }

    /// <summary>Gets how many of those are series whose composers came from their episodes.</summary>
    public int SeriesCount { get; }

    /// <summary>Gets how many credits there are.</summary>
    public int CreditCount { get; }

    /// <summary>Gets how many people are credited.</summary>
    public int NameCount { get; }

    /// <summary>Gets the size of the file, in bytes.</summary>
    public long Length { get; }

    /// <summary>Opens a table, or explains why it cannot be used.</summary>
    /// <param name="path">The file.</param>
    /// <param name="problem">Why it cannot be used, when it cannot.</param>
    /// <returns>The table, or null when the file is missing or not a whole, valid table.</returns>
    public static ImdbCrewFile? Open(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path))
        {
            problem = "it has not been downloaded";
            return null;
        }

        // Shared for deletion, so a newer copy can take its place while a lookup is under way.
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            var length = RandomAccess.GetLength(handle);
            Span<byte> header = stackalloc byte[ImdbCrewDataset.HeaderSize];
            if (length < header.Length || RandomAccess.Read(handle, header, 0) != header.Length)
            {
                problem = "the file is incomplete";
                handle.Dispose();
                return null;
            }

            if (!header[..4].SequenceEqual(ImdbCrewDataset.Magic)
                || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != ImdbCrewDataset.Version)
            {
                problem = "the file is not one this version of ThemeForge wrote";
                handle.Dispose();
                return null;
            }

            var titles = BinaryPrimitives.ReadInt32LittleEndian(header[28..]);
            var credits = BinaryPrimitives.ReadInt32LittleEndian(header[32..]);
            var names = BinaryPrimitives.ReadInt32LittleEndian(header[36..]);
            var nameBytes = BinaryPrimitives.ReadInt64LittleEndian(header[40..]);
            var expected = ImdbCrewDataset.HeaderSize
                + ((titles + 1L) * RecordSize)
                + (credits * 4L)
                + ((names + 1L) * 4)
                + nameBytes;

            if (titles < 0 || credits < 0 || names < 0 || nameBytes < 0 || expected != length)
            {
                problem = "the file is incomplete";
                handle.Dispose();
                return null;
            }

            return new ImdbCrewFile(handle, header, length);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Finds the composers IMDb credits on a title.</summary>
    /// <param name="title">The title's IMDb number.</param>
    /// <returns>The composers, in IMDb's billing order or, for a series, most episodes first.</returns>
    public IReadOnlyList<string> Composers(int title)
    {
        var low = 0;
        var high = TitleCount - 1;
        Span<byte> record = stackalloc byte[RecordSize * 2];

        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            var at = ImdbCrewDataset.HeaderSize + ((long)middle * RecordSize);
            ReadExactly(record, at);

            var found = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (found < title)
            {
                low = middle + 1;
            }
            else if (found > title)
            {
                high = middle - 1;
            }
            else
            {
                // The next record, the end record at worst, says where this title's credits stop.
                var first = BinaryPrimitives.ReadInt32LittleEndian(record[4..]);
                var end = BinaryPrimitives.ReadInt32LittleEndian(record[12..]);
                return Names(first, end);
            }
        }

        return Array.Empty<string>();
    }

    /// <inheritdoc />
    public void Dispose() => _handle.Dispose();

    private IReadOnlyList<string> Names(int first, int end)
    {
        // IMDb lists ten principals a title at most; anything more is a damaged file.
        if (first < 0 || end > CreditCount || end <= first || end - first > 32)
        {
            return Array.Empty<string>();
        }

        var count = end - first;
        Span<byte> positions = stackalloc byte[count * 4];
        ReadExactly(positions, _creditsAt + (first * 4L));

        var names = new List<string>(count);
        Span<byte> offsets = stackalloc byte[8];
        for (var index = 0; index < count; index++)
        {
            var position = BinaryPrimitives.ReadInt32LittleEndian(positions[(index * 4)..]);
            if (position < 0 || position >= NameCount)
            {
                continue;
            }

            ReadExactly(offsets, _offsetsAt + (position * 4L));
            var start = BinaryPrimitives.ReadInt32LittleEndian(offsets);
            var stop = BinaryPrimitives.ReadInt32LittleEndian(offsets[4..]);
            if (start < 0 || stop < start || stop - start > 1024)
            {
                continue;
            }

            var bytes = new byte[stop - start];
            ReadExactly(bytes, _namesAt + start);
            names.Add(Encoding.UTF8.GetString(bytes));
        }

        return names;
    }

    private void ReadExactly(Span<byte> buffer, long offset)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var got = RandomAccess.Read(_handle, buffer[read..], offset + read);
            if (got == 0)
            {
                throw new EndOfStreamException("The IMDb crew table ended early.");
            }

            read += got;
        }
    }
}
