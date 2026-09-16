using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// Reads yt-dlp's JSON output into <see cref="Candidate"/> values.
/// </summary>
/// <remarks>
/// yt-dlp is a moving target: fields appear, disappear and change type between releases, and a
/// flat search listing carries far less than a full metadata dump. Every read here is therefore
/// tolerant — a field that is missing, null or of an unexpected type yields null rather than an
/// exception, and the scoring rules that depend on it abstain. Being strict would mean one
/// upstream change stops the whole plugin finding anything.
/// </remarks>
public static class YtDlpJson
{
    /// <summary>
    /// Parses yt-dlp output, which prints one JSON object per line.
    /// </summary>
    /// <param name="output">Raw stdout from yt-dlp.</param>
    /// <param name="foundBy">The query these results came from.</param>
    /// <param name="hydrated">Whether this output came from a full metadata pass.</param>
    /// <returns>The candidates that could be parsed.</returns>
    public static IReadOnlyList<Candidate> ParseLines(string output, Query.SearchQuery foundBy, bool hydrated)
    {
        var candidates = new List<Candidate>();
        if (string.IsNullOrWhiteSpace(output))
        {
            return candidates;
        }

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
            {
                continue;
            }

            var candidate = ParseOne(trimmed, foundBy, hydrated);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>Parses a single JSON object.</summary>
    /// <param name="json">One JSON object.</param>
    /// <param name="foundBy">The query this result came from.</param>
    /// <param name="hydrated">Whether this came from a full metadata pass.</param>
    /// <returns>The candidate, or null when the entry has no usable id or title.</returns>
    public static Candidate? ParseOne(string json, Query.SearchQuery foundBy, bool hydrated)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var id = GetString(root, "id");
            var title = GetString(root, "title");
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title))
            {
                return null;
            }

            var description = GetString(root, "description");
            var credits = ReadMusicCredits(description);

            return new Candidate
            {
                Id = id,
                Title = title,
                Url = GetString(root, "webpage_url")
                      ?? GetString(root, "original_url")
                      ?? BuildWatchUrl(GetString(root, "url"), id),
                Channel = GetString(root, "channel") ?? GetString(root, "uploader"),
                ChannelId = GetString(root, "channel_id") ?? GetString(root, "uploader_id"),
                DurationSeconds = GetDouble(root, "duration"),
                ViewCount = GetLong(root, "view_count"),
                UploadDate = GetUploadDate(root),
                Description = description,
                Tags = GetStringArray(root, "tags"),

                // yt-dlp exposes release metadata for music uploads directly; older releases and
                // some uploads carry it only in the description's distributor block.
                Album = GetString(root, "album") ?? credits.Album,
                Artist = GetString(root, "artist") ?? GetString(root, "creator") ?? credits.Artist,
                Track = GetString(root, "track") ?? credits.Track,
                IsLive = GetBool(root, "is_live") ?? IsLiveStatus(GetString(root, "live_status")),
                Availability = GetString(root, "availability"),
                FoundBy = foundBy,
                IsHydrated = hydrated,
            };
        }
        catch (JsonException)
        {
            // yt-dlp interleaves progress and warning text with JSON on some code paths.
            return null;
        }
    }

    /// <summary>
    /// Reads the track, artist and album out of the block a distributor's upload starts with.
    /// </summary>
    /// <remarks>
    /// Every auto-generated music upload begins the same way:
    /// <c>Provided to YouTube by …</c>, a blank line, <c>Track · Artist</c>, a blank line, then the
    /// album. It is the only place a rights-holder upload titled by track names the work it
    /// belongs to, which is what lets "Main Title" be recognised as a theme at all.
    /// </remarks>
    /// <param name="description">The upload's description.</param>
    /// <returns>Whatever the block supplied; each part null when absent.</returns>
    public static (string? Track, string? Artist, string? Album) ReadMusicCredits(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)
            || !description.TrimStart().StartsWith("Provided to YouTube by", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null, null);
        }

        var lines = description
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToList();

        string? track = null;
        string? artist = null;
        string? album = null;

        if (lines.Count > 1)
        {
            var credit = lines[1].Split(" \u00b7 ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            track = credit.Length > 0 ? credit[0] : null;
            artist = credit.Length > 1 ? string.Join(", ", credit.Skip(1)) : null;
        }

        if (lines.Count > 2
            && !lines[2].StartsWith("\u2117", StringComparison.Ordinal)
            && !lines[2].StartsWith("Released on", StringComparison.OrdinalIgnoreCase)
            && !lines[2].StartsWith("Auto-generated", StringComparison.OrdinalIgnoreCase))
        {
            album = lines[2];
        }

        return (Blank(track), Blank(artist), Blank(album));
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Builds a watch URL. Flat search listings put a full URL in "url", but older yt-dlp
    /// releases put just the video id there, so both shapes are handled.
    /// </summary>
    private static string BuildWatchUrl(string? url, string id) =>
        !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://www.youtube.com/watch?v=" + id;

    private static bool IsLiveStatus(string? liveStatus) =>
        string.Equals(liveStatus, "is_live", StringComparison.OrdinalIgnoreCase)
        || string.Equals(liveStatus, "is_upcoming", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? GetDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static long? GetLong(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => number,
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? GetBool(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    /// <summary>Reads yt-dlp's "upload_date", which is an unpunctuated YYYYMMDD string.</summary>
    private static DateTime? GetUploadDate(JsonElement root)
    {
        var raw = GetString(root, "upload_date");
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        return DateTime.TryParseExact(raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    private static IReadOnlyList<string> GetStringArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var items = new List<string>();
        foreach (var element in value.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var text = element.GetString();
                if (!string.IsNullOrEmpty(text))
                {
                    items.Add(text);
                }
            }
        }

        return items;
    }
}
