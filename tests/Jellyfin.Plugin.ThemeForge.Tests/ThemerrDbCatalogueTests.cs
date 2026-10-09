using System;
using System.Linq;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Covers reading ThemerrDB's published index.
/// </summary>
/// <remarks>
/// The payloads here are the shapes the live service actually serves, captured from it. They are
/// the only part of the plugin whose format lives on somebody else's server, so the parsing is
/// pinned separately from the live check that the format has not changed.
/// </remarks>
public class ThemerrDbCatalogueTests
{
    [Fact]
    public void ThePageCountIsReadFromTheIndexHeader() =>
        Assert.Equal(439, ThemerrDbCatalogue.ParsePageCount("""{"count": 4382, "pages": 439, "imdb_count": 4378}"""));

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"count": 4382}""")]
    [InlineData("[]")]
    public void AnIndexHeaderThatSaysNothingCountsAsNoPages(string json) =>
        Assert.Equal(0, ThemerrDbCatalogue.ParsePageCount(json));

    [Fact]
    public void AFilmPageCarriesBothIds()
    {
        var entries = ThemerrDbCatalogue.ParsePage(
            """[{"id": 252178, "imdb_id": "tt2614684", "title": "'71"}, {"id": 8202, "imdb_id": "tt0402022", "title": "Æon Flux"}]""");

        Assert.Equal(2, entries.Count);
        Assert.Equal("252178", entries[0].Id);
        Assert.Equal("tt2614684", entries[0].ImdbId);
        Assert.Equal("'71", entries[0].Title);
        Assert.Equal("Æon Flux", entries[1].Title);
    }

    [Fact]
    public void AShowPageCarriesOnlyTheTmdbId()
    {
        // ThemerrDB has no TheTVDB path, which is why a series with only a TVDB id has to be
        // matched by name instead.
        var entries = ThemerrDbCatalogue.ParsePage("""[{"id": 1396, "title": "Breaking Bad"}]""");

        Assert.Equal("1396", Assert.Single(entries).Id);
        Assert.Null(entries[0].ImdbId);
    }

    [Fact]
    public void AnEntryWithNoIdIsIgnoredRatherThanGuessedAt() =>
        Assert.Empty(ThemerrDbCatalogue.ParsePage("""[{"title": "Nameless"}, "not an object", 42]"""));

    [Fact]
    public void ACollectionYieldsItsThemeAndItsMembers()
    {
        var (url, members) = ThemerrDbCatalogue.ParseCollection(
            """
            {
              "id": 8091,
              "name": "Alien Collection",
              "youtube_theme_url": "https://www.youtube.com/watch?v=abcdefghijk",
              "parts": [{"id": 348, "title": "Alien"}, {"id": 679, "title": "Aliens"}]
            }
            """);

        Assert.Equal("https://www.youtube.com/watch?v=abcdefghijk", url);
        Assert.Equal(new[] { "348", "679" }, members);
    }

    [Fact]
    public void ACollectionWithAnUnusableThemeLinkYieldsNone()
    {
        var (url, members) = ThemerrDbCatalogue.ParseCollection(
            """{"id": 1, "youtube_theme_url": "https://example.invalid/x.mp3", "parts": [{"id": 2}]}""");

        Assert.Null(url);
        Assert.Single(members);
    }

    [Fact]
    public void MembershipMapsBackToTheCollection()
    {
        var snapshot = new ThemerrDbSnapshot
        {
            Collections =
            {
                new CatalogueCollection("8091", "Alien Collection", "https://youtu.be/a", new[] { "348", "679" }),
                new CatalogueCollection("1565", "28 Days Later Collection", "https://youtu.be/b", new[] { "170" }),
            },
        };

        Assert.Equal("Alien Collection", snapshot.CollectionContaining("679")!.Title);
        Assert.Equal("28 Days Later Collection", snapshot.CollectionContaining("170")!.Title);
        Assert.Null(snapshot.CollectionContaining("99999"));
        Assert.Null(snapshot.CollectionContaining(null));
    }

    [Fact]
    public void LookupsAreCaseInsensitiveAndRejectNothing()
    {
        var snapshot = new ThemerrDbSnapshot
        {
            MovieTmdbIds = { "78" },
            MovieImdbIds = { "tt0083658" },
            TvShows = { new CatalogueTitle("1396", "Breaking Bad") },
        };

        Assert.True(snapshot.HasMovie("78"));
        Assert.True(snapshot.HasMovieByImdb("TT0083658"));
        Assert.True(snapshot.HasShow("1396"));

        Assert.False(snapshot.HasMovie("79"));
        Assert.False(snapshot.HasMovie(null));
        Assert.False(snapshot.HasMovie("  "));
    }

    [Fact]
    public void AnEmptySnapshotIsNotWorthConsulting()
    {
        // The difference between "the database has nothing for you" and "we have not looked yet"
        // decides whether a miss is free or costs a request.
        Assert.False(new ThemerrDbSnapshot().IsUsable);
        Assert.True(new ThemerrDbSnapshot { MovieTmdbIds = { "78" } }.IsUsable);
        Assert.True(new ThemerrDbSnapshot { TvShows = { new CatalogueTitle("1", "x") } }.IsUsable);
    }

    [Fact]
    public void AgeIsMeasuredFromWhenItWasRead()
    {
        var snapshot = new ThemerrDbSnapshot { UpdatedUtc = DateTime.UtcNow.AddHours(-30) };
        Assert.InRange(snapshot.Age.TotalHours, 29.9, 30.1);
    }

    [Fact]
    public void ASnapshotSurvivesBeingSavedAndReadBack()
    {
        // It is persisted as JSON between runs, and the records here have positional constructors
        // and a read-only list, both of which a serializer can quietly fail to reconstruct.
        var original = new ThemerrDbSnapshot
        {
            UpdatedUtc = new DateTime(2026, 9, 6, 13, 0, 0, DateTimeKind.Utc),
            MovieTmdbIds = { "78", "348" },
            MovieImdbIds = { "tt0083658" },
            TvShows = { new CatalogueTitle("1396", "Breaking Bad") },
            Collections = { new CatalogueCollection("8091", "Alien Collection", "https://youtu.be/a", new[] { "348", "679" }) },
        };

        var restored = System.Text.Json.JsonSerializer.Deserialize<ThemerrDbSnapshot>(
            System.Text.Json.JsonSerializer.Serialize(original))!;

        Assert.Equal(original.UpdatedUtc, restored.UpdatedUtc);
        Assert.Equal(new[] { "78", "348" }, restored.MovieTmdbIds);
        Assert.Equal("Breaking Bad", Assert.Single(restored.TvShows).Title);

        var collection = Assert.Single(restored.Collections);
        Assert.Equal("Alien Collection", collection.Title);
        Assert.Equal(new[] { "348", "679" }, collection.MemberIds);

        // And the lookups still work off the restored lists.
        Assert.True(restored.HasMovie("78"));
        Assert.True(restored.HasShow("1396"));
        Assert.Equal("Alien Collection", restored.CollectionContaining("679")!.Title);
    }

    [Fact]
    public void TheEndpointsAreTheOnesTheServicePublishes()
    {
        // Pinned so a careless edit to a format string shows up here rather than as an entire
        // library quietly finding nothing.
        Assert.Equal(
            "https://app.lizardbyte.dev/ThemerrDB/movies/themoviedb/78.json",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, ThemerrDbCatalogue.MoviesByTmdb, "78"));

        Assert.Equal(
            "https://app.lizardbyte.dev/ThemerrDB/movies/imdb/tt0083658.json",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, ThemerrDbCatalogue.MoviesByImdb, "tt0083658"));

        Assert.Equal(
            "https://app.lizardbyte.dev/ThemerrDB/tv_shows/themoviedb/1396.json",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, ThemerrDbCatalogue.ShowsByTmdb, "1396"));
    }
}
