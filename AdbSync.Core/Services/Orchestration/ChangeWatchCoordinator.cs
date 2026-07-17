using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Devices;
using Microsoft.Extensions.Logging;

namespace AdbSync.Core.Services.Orchestration;

/// <summary>
/// Owns the OnChange watch for one job: one live-or-polling loop per device binding, all funneling into a single
/// shared debounce timer so a burst of changes across any bound device triggers one job run, not many. Only ever
/// decides *when* to call <paramref name="onTriggered"/> - the run itself still goes through the normal pipeline
/// (locking, app-running guard, push-safety, etc).
/// </summary>
public sealed class ChangeWatchCoordinator(
    string jobName,
    IReadOnlyList<ChangeWatchBinding> bindings,
    IDeviceChangeWatcher watcher,
    IAdbDeviceResolver deviceResolver,
    ISyncEventSink events,
    TimeSpan debounceWindow,
    TimeSpan rescanInterval,
    Func<Task> onTriggered,
    TimeSpan? pollInterval = null,
    TimeSpan? reconnectBackoff = null,
    TimeSpan? postTriggerSuppression = null,
    ILogger? logger = null) : IAsyncDisposable
{
    private static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultReconnectBackoff = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DefaultPostTriggerSuppression = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _pollInterval = pollInterval ?? DefaultPollInterval;
    private readonly TimeSpan _reconnectBackoff = reconnectBackoff ?? DefaultReconnectBackoff;
    private readonly TimeSpan _postTriggerSuppression = postTriggerSuppression ?? DefaultPostTriggerSuppression;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _bindingLoops = [];
    private readonly SemaphoreSlim _debounceGate = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private volatile bool _suppressed;

    public void Start()
    {
        foreach (var binding in bindings)
            _bindingLoops.Add(Task.Run(() => RunBindingLoopAsync(binding, _cts.Token)));
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await Task.WhenAll(_bindingLoops);
        }
        catch (OperationCanceledException)
        {
        }

        foreach (var binding in bindings)
            events.WatchStopped(jobName, binding.Device.Name);

        _cts.Dispose();
        _debounceCts?.Dispose();
        _debounceGate.Dispose();
    }

    private async Task RunBindingLoopAsync(ChangeWatchBinding binding, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var serial = await deviceResolver.EnsureConnectedAsync(binding.Device, ct);
                var availability = await watcher.CheckAvailabilityAsync(serial, binding.RemotePath, binding.Exclude, ct);

                if (availability.LiveWatchSupported)
                {
                    events.WatchStarted(jobName, binding.Device.Name, liveWatch: true);
                    await RunLiveLoopAsync(binding, serial, ct);
                }
                else
                {
                    events.WatchStarted(jobName, binding.Device.Name, liveWatch: false);
                    events.WatchDegraded(jobName, binding.Device.Name, availability.Detail);
                    await RunPollLoopAsync(binding, serial, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                events.WatchDegraded(jobName, binding.Device.Name, ex.Message);
                await DelayAsync(_reconnectBackoff, ct);
            }
        }
    }

    private async Task RunLiveLoopAsync(ChangeWatchBinding binding, string serial, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var paths = await watcher.EnumerateSubdirectoriesAsync(serial, binding.RemotePath, binding.Exclude, ct);
            if (!paths.Contains(binding.RemotePath))
                paths = [binding.RemotePath, .. paths];

            using var rescanCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            rescanCts.CancelAfter(rescanInterval);
            try
            {
                await foreach (var _ in watcher.WatchAsync(serial, paths, rescanCts.Token))
                {
                    if (_suppressed)
                        continue;

                    events.ChangeDetected(jobName, binding.Device.Name);
                    await SignalChangeAsync(ct);
                }
            }
            catch (OperationCanceledException) when (rescanCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Rescan interval elapsed, not a real cancellation - loop back around to re-enumerate
                // subdirectories (picks up folders created since the watch was last seeded) and re-watch.
            }
        }
    }

    private async Task RunPollLoopAsync(ChangeWatchBinding binding, string serial, CancellationToken ct)
    {
        var lastSnapshot = await watcher.SnapshotAsync(serial, binding.RemotePath, ct);
        while (!ct.IsCancellationRequested)
        {
            await DelayAsync(_pollInterval, ct);
            if (ct.IsCancellationRequested)
                return;

            var snapshot = await watcher.SnapshotAsync(serial, binding.RemotePath, ct);
            if (snapshot == lastSnapshot)
                continue;

            lastSnapshot = snapshot;
            if (_suppressed)
                continue;

            events.ChangeDetected(jobName, binding.Device.Name);
            await SignalChangeAsync(ct);
        }
    }

    /// <summary>Resets the one shared per-job debounce timer; only the most recent signal survives to fire <see cref="onTriggered"/>.</summary>
    private async Task SignalChangeAsync(CancellationToken ct)
    {
        CancellationToken debounceToken;
        await _debounceGate.WaitAsync(ct);
        try
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            debounceToken = _debounceCts.Token;
        }
        finally
        {
            _debounceGate.Release();
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceWindow, debounceToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _suppressed = true;
            try
            {
                await onTriggered();
            }
            catch (Exception ex)
            {
                logger?.LogWarning(ex, "OnChange trigger for job '{Job}' failed before the run started", jobName);
            }
            finally
            {
                await Task.Delay(_postTriggerSuppression, CancellationToken.None);
                _suppressed = false;
            }
        }, CancellationToken.None);
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
