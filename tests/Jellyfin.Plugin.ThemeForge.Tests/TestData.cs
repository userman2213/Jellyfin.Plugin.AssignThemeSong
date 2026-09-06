using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Configuration;
using Jellyfin.Plugin.ThemeForge.Engines.Discovery;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Scoring;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>Builders that keep the tests readable by defaulting everything irrelevant.</summary>
internal static class TestData
{
    public static readonly SearchQuery DefaultQuery = new("test query", 0, "{title} theme");

    public static MediaIdentity Series(string title, int? year = 2005, IReadOnlyList<string>? alternates = null) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        AlternateTitles = alternates ?? Array.Empty<string>(),
        Year = year,
        Kind = BaseItemKind.Series,
    };

    public static MediaIdentity Movie(string title, int? year = 2005) => new()
    {
        ItemId = Guid.NewGuid(),
        Title = title,
        NormalizedTitle = TitleNormalizer.Normalize(title),
        Year = year,
        Kind = BaseItemKind.Movie,
    };

    public static Candidate Candidate(
        string title,
        double? duration = 75,
        long? views = 50_000,
        string? channel = "Some Channel",
        DateTime? uploaded = null,
        bool isLive = false,
        string? availability = "public",
        string? id = null,
        SearchQuery? foundBy = null) => new()
    {
        Id = id ?? Guid.NewGuid().ToString("N")[..11],
        Url = "https://www.youtube.com/watch?v=test",
        Title = title,
        Channel = channel,
        DurationSeconds = duration,
        ViewCount = views,
        UploadDate = uploaded ?? new DateTime(2010, 1, 1),
        IsLive = isLive,
        Availability = availability,
        FoundBy = foundBy ?? DefaultQuery,
        IsHydrated = true,
    };

    public static PluginConfiguration Config() => new();

    public static ScoringContext Context(MediaIdentity identity, PluginConfiguration? configuration = null, Func<string, string?>? duplicates = null) => new()
    {
        Identity = identity,
        Configuration = configuration ?? Config(),
        FindExistingAssignment = duplicates,
    };

    /// <summary>Builds the engine with the full production rule set, as the plugin registers it.</summary>
    public static ScoringEngine Engine() => new(new List<IScoringRule>
    {
        new Engines.Scoring.Rules.TitleSimilarityRule(),
        new Engines.Scoring.Rules.KeywordAffinityRule(),
        new Engines.Scoring.Rules.NegativeKeywordRule(),
        new Engines.Scoring.Rules.DurationPlausibilityRule(),
        new Engines.Scoring.Rules.ChannelReputationRule(),
        new Engines.Scoring.Rules.PopularityRule(),
        new Engines.Scoring.Rules.RecencyRule(),
        new Engines.Scoring.Rules.AvailabilityRule(),
        new Engines.Scoring.Rules.DuplicateRule(),
        new Engines.Scoring.Rules.QuerySpecificityRule(),
    });
}
