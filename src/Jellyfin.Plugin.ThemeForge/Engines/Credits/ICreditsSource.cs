using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Configuration;

namespace Jellyfin.Plugin.ThemeForge.Engines.Credits;

/// <summary>The questions a source can answer about a work's music.</summary>
[Flags]
public enum CreditsQuestion
{
    /// <summary>Nothing.</summary>
    None = 0,

    /// <summary>Who wrote the music.</summary>
    Composers = 1,

    /// <summary>What the theme is called, who performs it, and who wrote it.</summary>
    Theme = 2,
}

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
    string? ReleaseGroupId = null)
{
    /// <summary>Gets the English Wikipedia article an earlier source found for the work, when it did.</summary>
    public string? WikipediaTitle { get; init; }
}

/// <summary>What a source found, and which works it could not ask about at all.</summary>
/// <remarks>
/// The distinction is the point. A work the source asked about and found nothing for is a miss,
/// remembered for a fortnight so it is not asked about again tomorrow. A work it could not ask
/// about — the service was down, or refused — is not a miss, and recording it as one would hide it
/// for a fortnight for a reason that had nothing to do with the work. Anything in the batch that
/// is neither found nor failed was asked, and has no answer here.
/// </remarks>
/// <param name="Found">What was found, keyed on <see cref="CreditsRequest.Key"/>.</param>
/// <param name="Failed">The keys of works that could not be asked about, and should be tried again.</param>
public sealed record CreditsAnswer(
    IReadOnlyDictionary<string, ResearchedCredits> Found,
    IReadOnlySet<string> Failed)
{
    /// <summary>Gets an answer that found nothing and failed at nothing.</summary>
    public static CreditsAnswer Nothing { get; } = new(
        new Dictionary<string, ResearchedCredits>(),
        new HashSet<string>());

    /// <summary>Makes an answer for a batch that could not be asked about at all.</summary>
    /// <param name="batch">The works.</param>
    /// <returns>The answer.</returns>
    public static CreditsAnswer FailedFor(IEnumerable<CreditsRequest> batch) => new(
        new Dictionary<string, ResearchedCredits>(),
        batch.Select(work => work.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// A public database that can say something about a work's music.
/// </summary>
/// <remarks>
/// <para>
/// Batch-shaped rather than one item at a time, because the difference between the sources is
/// precisely how many works they will answer about at once: one of them takes fifty ids in a
/// single query, another insists on one request per second. Each decides for itself how to spend
/// the batch it is given, and which works in it it can ask about at all.
/// </para>
/// <para>
/// Sources are asked in <see cref="Order"/>, and each is given only the works still in need of a
/// question it answers — and none that an earlier source failed to ask about, since those are
/// tried again, in order, on the next run.
/// </para>
/// </remarks>
public interface ICreditsSource
{
    /// <summary>Gets a short name, recorded against whatever it answers.</summary>
    string Name { get; }

    /// <summary>Gets the position in the queue of sources, lowest asked first.</summary>
    int Order { get; }

    /// <summary>Gets the questions this source can answer.</summary>
    CreditsQuestion Answers { get; }

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
    /// <returns>What was found, and which works could not be asked about.</returns>
    Task<CreditsAnswer> LookUpAsync(
        IReadOnlyList<CreditsRequest> batch,
        PluginConfiguration configuration,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
