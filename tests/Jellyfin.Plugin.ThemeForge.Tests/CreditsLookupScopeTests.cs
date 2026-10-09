using Jellyfin.Data.Enums;
using Jellyfin.Plugin.ThemeForge.Engines.Catalogue;
using Jellyfin.Plugin.ThemeForge.Engines.Identity;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using Xunit;

namespace Jellyfin.Plugin.ThemeForge.Tests;

/// <summary>
/// Pins which titles the credits look-up is spent on.
/// </summary>
/// <remarks>
/// ThemerrDB is asked first and gives a theme outright, so a title it covers needs nothing looked
/// up: there is no search to sharpen. Every one left out is a page not fetched from IMDb, which on
/// a library ThemerrDB covers well is most of them.
/// </remarks>
public class CreditsLookupScopeTests
{
    private static MediaIdentity Identity(
        BaseItemKind kind,
        string? tmdbId = null,
        string? imdbId = null) => new()
        {
            ItemId = System.Guid.NewGuid(),
            Title = "Whatever",
            NormalizedTitle = "whatever",
            Kind = kind,
            TmdbId = tmdbId,
            ImdbId = imdbId,
        };

    private static ThemerrDbSnapshot Catalogue()
    {
        var snapshot = new ThemerrDbSnapshot();
        snapshot.MovieTmdbIds.Add("550");
        snapshot.MovieImdbIds.Add("tt0137523");
        snapshot.TvShows.Add(new CatalogueTitle("1396", "Breaking Bad"));
        snapshot.Collections.Add(new CatalogueCollection(
            "1241", "Harry Potter Collection", "https://youtu.be/x", new[] { "671", "672" }));
        return snapshot;
    }

    [Fact]
    public void AFilmThemerrHasAThemeForIsNotLookedUp() =>
        Assert.True(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Movie, "550")));

    [Fact]
    public void AFilmThemerrListsByImdbIdAloneIsNotLookedUpEither() =>
        Assert.True(ThemeOrchestrator.HasThemerrTheme(
            Catalogue(), Identity(BaseItemKind.Movie, tmdbId: null, imdbId: "tt0137523")));

    [Fact]
    public void AFilmCoveredOnlyByItsCollectionIsNotLookedUp()
    {
        // ThemerrDB gives a collection one theme for all of its films, so a film listed nowhere
        // else still comes away with one.
        Assert.True(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Movie, "671")));
    }

    [Fact]
    public void AShowThemerrHasAThemeForIsNotLookedUp() =>
        Assert.True(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Series, "1396")));

    [Fact]
    public void AnythingThemerrDoesNotCoverIsLookedUp()
    {
        Assert.False(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Movie, "99999")));
        Assert.False(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Series, "99999")));
    }

    [Fact]
    public void AShowIsNotMatchedAgainstTheFilmList()
    {
        // 550 is a film in the catalogue. A series that happens to carry the same TMDB number is a
        // different work, and must still be looked up.
        Assert.False(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Series, "550")));
    }

    [Fact]
    public void AnEmptyCatalogueExcusesNothing()
    {
        // A snapshot that has never been synced lists nothing, so it must not be read as covering
        // everything and skipping the whole library.
        var empty = new ThemerrDbSnapshot();

        Assert.False(ThemeOrchestrator.HasThemerrTheme(empty, Identity(BaseItemKind.Movie, "550")));
        Assert.False(ThemeOrchestrator.HasThemerrTheme(empty, Identity(BaseItemKind.Series, "1396")));
    }

    [Fact]
    public void ATitleWithNoIdsIsNotTakenForCovered() =>
        Assert.False(ThemeOrchestrator.HasThemerrTheme(Catalogue(), Identity(BaseItemKind.Movie)));
}
