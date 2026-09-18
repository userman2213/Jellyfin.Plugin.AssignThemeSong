using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>One work to find the music credits for.</summary>
/// <param name="Key">The key its answer is stored under.</param>
/// <param name="Keys">Every key it is known by.</param>
/// <param name="ImdbId">The IMDb id, when known.</param>
/// <param name="TmdbId">The TMDB id, when known.</param>
/// <param name="IsSeries">Whether it is a series.</param>
/// <param name="Label">A human-readable name, for the log.</param>
/// <param name="ReleaseGroupId">A MusicBrainz release group an earlier source supplied, when it did.</param>
public sealed record CreditsRequest(
    string Key,
    IReadOnlyList<string> Keys,
    string? ImdbId,
    string? TmdbId,
    bool IsSeries,
    string Label,
    string? ReleaseGroupId = null);

/// <summary>
/// A public database that can say who wrote a work's music.
/// </summary>
/// <remarks>
/// <para>
/// Batch-shaped rather than one item at a time, because the difference between the sources is
/// precisely how many works they will answer about at once: one of them takes a hundred ids in a
/// single query, the other insists on one request per second. Each decides for itself how to spend
/// the batch it is given.
/// </para>
/// <para>
/// Sources are asked in <see cref="Order"/>, and each is given only the works the ones before it
/// could not answer.
/// </para>
/// </remarks>
public interface ICreditsSource
{
    /// <summary>Gets a short name, recorded against whatever it answers.</summary>
    string Name { get; }

    /// <summary>Gets the position in the queue of sources, lowest asked first.</summary>
    int Order { get; }

    /// <summary>Reports whether the user has turned this source on.</summary>
    /// <param name="configuration">The settings.</param>
    /// <returns><see langword="true"/> when it may be used.</returns>
    bool IsEnabled(PluginConfiguration configuration);

    /// <summary>
    /// Looks up as many of these works as it can.
    /// </summary>
    /// <param name="batch">The works to ask about.</param>
    /// <param name="configuration">The settings.</param>
    /// <param name="progress">Progress reporter, 0 to 100 across this batch, or null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What was found, keyed on <see cref="CreditsRequest.Key"/>. Works it could not answer are simply absent.</returns>
    Task<IReadOnlyDictionary<string, ResearchedCredits>> LookUpAsync(
        IReadOnlyList<CreditsRequest> batch,
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
