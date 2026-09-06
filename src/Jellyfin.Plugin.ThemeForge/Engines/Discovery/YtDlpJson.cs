using System;
using System.Collections.Generic;
using System.Globalization;
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
                Description = GetString(root, "description"),
                Tags = GetStringArray(root, "tags"),
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
