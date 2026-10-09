using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Query;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// A place theme songs can be found.
/// </summary>
/// <remarks>
/// Search is split into a cheap listing pass and an expensive hydration pass so the pipeline
/// can score a wide net of candidates on title alone, then spend real network time only on the
/// handful worth a closer look. A source that has no such distinction can return fully
/// hydrated candidates from <see cref="SearchAsync"/> and implement hydration as a no-op.
/// </remarks>
public interface ICandidateSource
{
    /// <summary>Gets a short name used in logs and the review queue.</summary>
    string Name { get; }

    /// <summary>
    /// Runs one search, returning candidates that may carry only a title and an id.
    /// </summary>
    /// <param name="query">The search to run.</param>
    /// <param name="maxResults">How many results to ask for.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The candidates found, possibly empty. Never throws for an ordinary search miss.</returns>
    Task<IReadOnlyList<Candidate>> SearchAsync(SearchQuery query, int maxResults, CancellationToken cancellationToken);

    /// <summary>
    /// Fetches full metadata for the given candidates.
    /// </summary>
    /// <param name="candidates">Candidates to enrich.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The enriched candidates; any that could not be fetched are returned unchanged.</returns>
    Task<IReadOnlyList<Candidate>> HydrateAsync(IReadOnlyList<Candidate> candidates, CancellationToken cancellationToken);
}
