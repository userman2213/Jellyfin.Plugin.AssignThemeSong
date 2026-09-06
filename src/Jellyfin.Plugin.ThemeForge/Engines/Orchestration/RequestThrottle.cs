using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.ThemeForge.Engines.Orchestration;

/// <summary>
/// Spaces out outbound requests across all worker threads.
/// </summary>
/// <remarks>
/// A library scan can involve thousands of searches. Issuing them as fast as the machine allows
/// is the reliable way to get rate limited, at which point every subsequent request fails and
/// the run produces nothing. A small enforced gap, jittered so requests do not fall into
/// lockstep, costs little and keeps the run alive.
/// </remarks>
public sealed class RequestThrottle : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Random _jitter = new();
    private DateTime _nextAllowedUtc = DateTime.MinValue;

    /// <summary>Waits until the next request may be issued.</summary>
    /// <param name="minimumDelay">The configured minimum gap between requests.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the caller may proceed.</returns>
    public async Task WaitAsync(TimeSpan minimumDelay, CancellationToken cancellationToken)
    {
        if (minimumDelay <= TimeSpan.Zero)
        {
            return;
        }

        TimeSpan wait;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            wait = _nextAllowedUtc > now ? _nextAllowedUtc - now : TimeSpan.Zero;

            // Up to 25% extra, so concurrent workers spread out instead of synchronising.
            var jittered = minimumDelay + TimeSpan.FromMilliseconds(_jitter.Next(0, (int)(minimumDelay.TotalMilliseconds / 4) + 1));
            _nextAllowedUtc = now + wait + jittered;
        }
        finally
        {
            _gate.Release();
        }

        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _gate.Dispose();
}
