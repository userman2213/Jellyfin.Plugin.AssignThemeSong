using Jellyfin.Plugin.ThemeForge.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.ThemeForge.Engines.Index;
using Jellyfin.Plugin.ThemeForge.Engines.Orchestration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ThemeForge.Tasks;

/// <summary>
/// Gives newly added movies and series a theme without waiting for the nightly run.
/// </summary>
/// <remarks>
/// <para>
/// Newly added media is exactly when a missing theme is most noticeable, so waiting up to a day
/// for the scheduled task is a poor experience. Items are queued and drained on a single
/// background worker rather than processed inline, because a library scan that adds a hundred
/// series would otherwise fire a hundred concurrent searches from inside Jellyfin's own event
/// handler and get the server rate limited.
/// </para>
/// <para>
/// A settling delay before draining lets a scan finish adding items first, so a burst is
/// handled as one batch.
/// </para>
/// </remarks>
public sealed class NewItemWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan SettlingDelay = TimeSpan.FromMinutes(2);

    private readonly ILibraryManager _libraryManager;
    private readonly IThemeOrchestrator _orchestrator;
    private readonly IThemeIndex _index;
    private readonly IThemeForgeLogger<NewItemWatcher> _logger;

    private readonly ConcurrentQueue<Guid> _pending = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _worker;

    /// <summary>Initializes a new instance of the <see cref="NewItemWatcher"/> class.</summary>
    /// <param name="libraryManager">Raises the item-added event.</param>
    /// <param name="orchestrator">Processes an individual item.</param>
    /// <param name="index">Persists the outcome.</param>
    /// <param name="logger">Logger.</param>
    public NewItemWatcher(
        ILibraryManager libraryManager,
        IThemeOrchestrator orchestrator,
        IThemeIndex index,
        IThemeForgeLogger<NewItemWatcher> logger)
    {
        _libraryManager = libraryManager;
        _orchestrator = orchestrator;
        _index = index;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        _worker = Task.Run(() => DrainAsync(_shutdown.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_worker is not null)
        {
            // Wait for the worker to actually stop, but never hold up server shutdown for it.
            // Awaiting here matters because Dispose frees the semaphore and token source the
            // worker is still using; letting it race produced spurious faults during shutdown.
            using var grace = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            grace.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                await _worker.WaitAsync(grace.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Cancelled, timed out, or the worker faulted on its way down. Either way the
                // event handler is already detached and there is nothing left to wait for.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _shutdown.Dispose();
        _signal.Dispose();
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs eventArgs)
    {
        if (eventArgs?.Item is not (Movie or Series))
        {
            return;
        }

        _pending.Enqueue(eventArgs.Item.Id);
        _signal.Release();
    }

    private async Task DrainAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

                // Let the rest of a scan's additions arrive before starting work.
                await Task.Delay(SettlingDelay, cancellationToken).ConfigureAwait(false);

                // The signal count no longer matters; everything queued is drained in one pass.
                while (_signal.CurrentCount > 0)
                {
                    await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                await ProcessPendingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ThemeForge: the new-item watcher hit an error; it will keep running.");
            }
        }
    }

    private async Task ProcessPendingAsync(CancellationToken cancellationToken)
    {
        if (_orchestrator.IsRunning)
        {
            // A full run is already covering the library; queued items will be picked up there.
            _logger.LogDebug("ThemeForge: a run is already in progress, so newly added items are left to it.");
            while (_pending.TryDequeue(out _))
            {
                // Discard: the in-flight run enumerates the library itself.
            }

            return;
        }

        var configuration = Plugin.Config.ShallowCopy();
        var report = new RunReport { WasDryRun = configuration.DryRun };
        var processed = 0;

        while (_pending.TryDequeue(out var itemId))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                continue;
            }

            await _orchestrator.ProcessItemAsync(item, report, configuration, cancellationToken).ConfigureAwait(false);
            processed++;
        }

        if (processed > 0)
        {
            await _index.FlushAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("ThemeForge: processed {Count} newly added items — {Summary}", processed, report.ToString());
        }
    }
}
