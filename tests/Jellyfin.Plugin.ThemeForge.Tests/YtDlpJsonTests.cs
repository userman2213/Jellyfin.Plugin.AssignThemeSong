using System;
using System.IO;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Parsing is pinned against captured yt-dlp output because its JSON shape drifts between
/// releases, and the parser must degrade rather than throw when it does.
/// </summary>
public class YtDlpJsonTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void ParsesEveryEntryOfAFlatSearchListing()
    {
        var candidates = YtDlpJson.ParseLines(Fixture("ytdlp-flat-search.jsonl"), TestData.DefaultQuery, hydrated: false);

        Assert.Equal(3, candidates.Count);
        Assert.Equal("Firefly - Main Title Theme", candidates[0].Title);
        Assert.Equal("aBcDeFgHiJk", candidates[0].Id);
        Assert.Equal(63.0, candidates[0].DurationSeconds);
        Assert.Equal(1_840_221, candidates[0].ViewCount);
        Assert.Equal("Greg Edmonson - Topic", candidates[0].Channel);
        Assert.False(candidates[0].IsHydrated);
    }

    [Fact]
    public void BuildsAWatchUrlWhenOnlyAnIdIsGiven()
    {
        // Older yt-dlp releases put a bare video id in "url" rather than a full link.
        var candidates = YtDlpJson.ParseLines(Fixture("ytdlp-flat-search.jsonl"), TestData.DefaultQuery, hydrated: false);
        Assert.Equal("https://www.youtube.com/watch?v=WxYz01234Ab", candidates[2].Url);
    }

    [Fact]
    public void ReadsFullMetadataIncludingStringEncodedNumbers()
    {
        var candidate = YtDlpJson.ParseOne(Fixture("ytdlp-full-metadata.json"), TestData.DefaultQuery, hydrated: true);

        Assert.NotNull(candidate);
        Assert.Equal(1_840_221, candidate!.ViewCount);
        Assert.Equal(new DateTime(2009, 11, 14), candidate.UploadDate);
        Assert.Equal(3, candidate.Tags.Count);
        Assert.Equal("public", candidate.Availability);
        Assert.False(candidate.IsLive);
        Assert.True(candidate.IsHydrated);
    }

    [Fact]
    public void IgnoresNonJsonNoiseInterleavedWithResults()
    {
        // yt-dlp mixes warnings and progress lines into stdout on some code paths.
        var output = "WARNING: unable to extract something\n"
                     + "{\"id\":\"abcdefghijk\",\"title\":\"Real Result\"}\n"
                     + "[download] Finished\n";

        var candidates = YtDlpJson.ParseLines(output, TestData.DefaultQuery, hydrated: false);

        Assert.Single(candidates);
        Assert.Equal("Real Result", candidates[0].Title);
    }

    [Fact]
    public void SkipsEntriesWithoutAnIdOrTitle()
    {
        var output = "{\"id\":\"abcdefghijk\"}\n{\"title\":\"No id\"}\n{\"id\":\"zzzzzzzzzzz\",\"title\":\"Good\"}\n";
        var candidates = YtDlpJson.ParseLines(output, TestData.DefaultQuery, hydrated: false);

        Assert.Single(candidates);
        Assert.Equal("Good", candidates[0].Title);
    }

    [Fact]
    public void ReturnsNullForMalformedJsonRatherThanThrowing() =>
        Assert.Null(YtDlpJson.ParseOne("{not json", TestData.DefaultQuery, hydrated: false));

    [Fact]
    public void HandlesEmptyOutput() =>
        Assert.Empty(YtDlpJson.ParseLines(string.Empty, TestData.DefaultQuery, hydrated: false));

    [Fact]
    public void MissingOptionalFieldsBecomeNullRatherThanFailing()
    {
        var candidate = YtDlpJson.ParseOne(
            "{\"id\":\"abcdefghijk\",\"title\":\"Sparse\"}",
            TestData.DefaultQuery,
            hydrated: false);

        Assert.NotNull(candidate);
        Assert.Null(candidate!.DurationSeconds);
        Assert.Null(candidate.ViewCount);
        Assert.Null(candidate.UploadDate);
        Assert.Empty(candidate.Tags);
    }

    [Fact]
    public void DetectsAnUpcomingStreamAsLive()
    {
        var candidate = YtDlpJson.ParseOne(
            "{\"id\":\"abcdefghijk\",\"title\":\"Premiere\",\"live_status\":\"is_upcoming\"}",
            TestData.DefaultQuery,
            hydrated: false);

        Assert.True(candidate!.IsLive);
    }
}
