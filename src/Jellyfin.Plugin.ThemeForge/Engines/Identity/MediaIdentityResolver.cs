using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.ThemeForge.Engines.Identity;

/// <summary>Turns a Jellyfin library item into the identity the pipeline reasons about.</summary>
public interface IMediaIdentityResolver
{
    /// <summary>Resolves an item's identity.</summary>
    /// <param name="item">The library item.</param>
    /// <returns>The identity, or null when the item is of a kind ThemeForge does not handle.</returns>
    MediaIdentity? Resolve(BaseItem item);
}

/// <summary>
/// Reads titles, year and provider ids off a library item and normalises them once, so no
/// other engine needs to know anything about Jellyfin's entity model.
/// </summary>
public sealed class MediaIdentityResolver : IMediaIdentityResolver
{
    /// <inheritdoc />
    public MediaIdentity? Resolve(BaseItem item)
    {
        var kind = item switch
        {
            Series => BaseItemKind.Series,
            Movie => BaseItemKind.Movie,
            _ => (BaseItemKind?)null,
        };

        if (kind is null || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        var normalized = TitleNormalizer.Normalize(item.Name);
        if (normalized.Length == 0)
        {
            return null;
        }

        return new MediaIdentity
        {
            ItemId = item.Id,
            Title = item.Name,
            NormalizedTitle = normalized,
            OriginalTitle = item.OriginalTitle,
            AlternateTitles = BuildAlternateTitles(item, normalized),
            Year = item.ProductionYear,
            Kind = kind.Value,
            TvdbId = NullIfEmpty(item.GetProviderId(MetadataProvider.Tvdb)),
            TmdbId = NullIfEmpty(item.GetProviderId(MetadataProvider.Tmdb)),
            ImdbId = NullIfEmpty(item.GetProviderId(MetadataProvider.Imdb)),
        };
    }

    /// <summary>
    /// Collects other titles worth searching under. Only forms that normalise to something
    /// different from the primary title are kept, so the query ladder is not padded with duplicates.
    /// </summary>
    private static IReadOnlyList<string> BuildAlternateTitles(BaseItem item, string primaryNormalized)
    {
        var candidates = new List<string?> { item.OriginalTitle, item.ForcedSortName };

        return candidates
            .Select(TitleNormalizer.Normalize)
            .Where(t => t.Length > 0 && !string.Equals(t, primaryNormalized, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
