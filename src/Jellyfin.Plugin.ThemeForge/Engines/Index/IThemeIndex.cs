using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThemeForge.Engines.Index;

/// <summary>
/// The plugin's memory of what it has decided, and what humans have decided over it.
/// </summary>
/// <remarks>
/// Everything that makes repeated runs safe lives here. Without it every run would search the
/// whole library again, re-offer candidates a human already rejected, and overwrite themes
/// somebody chose by hand.
/// </remarks>
public interface IThemeIndex
{
    /// <summary>Gets the entry for an item, if one exists.</summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <returns>The entry, or null.</returns>
    ThemeIndexEntry? Get(Guid itemId);

    /// <summary>
    /// Gets the entry for an item, falling back to a provider-anchored lookup so records survive
    /// a library being removed and re-added, which regenerates every item id.
    /// </summary>
    /// <param name="itemId">The Jellyfin item id.</param>
    /// <param name="stableKey">The provider-anchored key.</param>
    /// <returns>The entry, or null.</returns>
    ThemeIndexEntry? Resolve(Guid itemId, string stableKey);

    /// <summary>Gets every entry.</summary>
    /// <returns>All entries, in no particular order.</returns>
    IReadOnlyCollection<ThemeIndexEntry> All();

    /// <summary>Gets the entries awaiting human review.</summary>
    /// <returns>Entries in the review queue, best score first.</returns>
    IReadOnlyList<ThemeIndexEntry> ReviewQueue();

    /// <summary>Adds or replaces an entry and schedules a save.</summary>
    /// <param name="entry">The entry to store.</param>
    void Put(ThemeIndexEntry entry);

    /// <summary>Removes an entry.</summary>
    /// <param name="itemId">The item to forget.</param>
    /// <returns><c>true</c> if an entry was removed.</returns>
    bool Remove(Guid itemId);

    /// <summary>
    /// Finds the item, if any, that already uses a given source video.
    /// </summary>
    /// <param name="videoId">The candidate's source id.</param>
    /// <param name="excludingItem">The item currently being considered, which is not a clash with itself.</param>
    /// <returns>The other item's label, or null.</returns>
    string? FindAssignmentOwner(string videoId, Guid excludingItem);

    /// <summary>Writes any pending changes to disk.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the index is durable.</returns>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>Loads the index from disk.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes once the index is in memory.</returns>
    Task LoadAsync(CancellationToken cancellationToken);
}
