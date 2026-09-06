using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Query;
using Jellyfin.Plugin.ThemeForge.Engines.Tooling;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Engines.Discovery;

/// <summary>
/// Finds theme song candidates on YouTube using yt-dlp.
/// </summary>
/// <remarks>
/// yt-dlp is driven as a subprocess rather than through a .NET YouTube library on purpose.
/// In-process libraries have to reimplement YouTube's signature cipher, which changes without
/// notice and breaks every consumer until a new library release ships. yt-dlp tracks those
/// changes itself and ThemeForge keeps it updated, so a YouTube change is a background task
/// away from being fixed instead of a plugin release away.
/// </remarks>
public sealed class YtDlpCandidateSource : ICandidateSource
{
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan HydrateTimeout = TimeSpan.FromMinutes(3);

    private readonly IToolProvisioner _toolProvisioner;
    private readonly IProcessRunner _processRunner;
    private readonly ILogger<YtDlpCandidateSource> _logger;

    /// <summary>Initializes a new instance of the <see cref="YtDlpCandidateSource"/> class.</summary>
    /// <param name="toolProvisioner">Supplies the yt-dlp binary.</param>
    /// <param name="processRunner">Runs yt-dlp.</param>
    /// <param name="logger">Logger.</param>
    public YtDlpCandidateSource(
        IToolProvisioner toolProvisioner,
        IProcessRunner processRunner,
        ILogger<YtDlpCandidateSource> logger)
    {
        _toolProvisioner = toolProvisioner;
        _processRunner = processRunner;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "YouTube";

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candidate>> SearchAsync(
        SearchQuery query,
        int maxResults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);
        var count = Math.Clamp(maxResults, 1, 50);

        var arguments = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"ytsearch{count}:{query.Text}"),

            // A flat listing skips one page fetch per result. It is enough to score titles,
            // which is all that is needed to decide which candidates deserve a full look.
            "--flat-playlist",
            "--dump-json",
            "--no-download",
        };
        arguments.AddRange(CommonArguments());

        var result = await _processRunner
            .RunAsync(tools.YtDlp, arguments, SearchTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success && result.StandardOutput.Length == 0)
        {
            _logger.LogWarning("ThemeForge: search for \"{Query}\" failed ({Error}).", query.Text, result.ErrorSummary);
            return Array.Empty<Candidate>();
        }

        var candidates = YtDlpJson.ParseLines(result.StandardOutput, query, hydrated: false);
        _logger.LogDebug("ThemeForge: search for \"{Query}\" returned {Count} candidates.", query.Text, candidates.Count);
        return candidates;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Candidate>> HydrateAsync(
        IReadOnlyList<Candidate> candidates,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return candidates;
        }

        var tools = await _toolProvisioner.EnsureToolsAsync(cancellationToken).ConfigureAwait(false);

        // One invocation for the whole batch: process startup costs more than the extra URLs.
        var arguments = new List<string> { "--dump-json", "--no-download", "--no-playlist" };
        arguments.AddRange(CommonArguments());
        arguments.AddRange(candidates.Select(c => c.Url));

        var result = await _processRunner
            .RunAsync(tools.YtDlp, arguments, HydrateTimeout, cancellationToken)
            .ConfigureAwait(false);

        if (result.StandardOutput.Length == 0)
        {
            _logger.LogWarning(
                "ThemeForge: could not fetch metadata for {Count} candidates ({Error}); scoring them on title alone.",
                candidates.Count,
                result.ErrorSummary);
            return candidates;
        }

        var hydrated = YtDlpJson
            .ParseLines(result.StandardOutput, candidates[0].FoundBy, hydrated: true)
            .ToDictionary(c => c.Id, StringComparer.Ordinal);

        // Preserve the original order and keep the un-hydrated candidate when a fetch failed,
        // so one unavailable video does not remove the rest from consideration.
        return candidates
            .Select(candidate => hydrated.TryGetValue(candidate.Id, out var full)
                ? full with { FoundBy = candidate.FoundBy }
                : candidate)
            .ToList();
    }

    /// <summary>
    /// Arguments shared by both passes: keep the output machine-readable, do not abort the whole
    /// batch because one video is unavailable, and bound the time any single request can take.
    /// </summary>
    private static IEnumerable<string> CommonArguments() => new[]
    {
        "--ignore-errors",
        "--no-warnings",
        "--no-progress",
        "--no-color",
        "--socket-timeout", "20",
        "--retries", "3",
    };
}
