namespace AdbSync.Web.Services;

/// <summary>
/// Keeps the /status snapshot current while a page watches it. The server's event stream says when something changed,
/// and each change triggers a refresh; a slow poll covers a stream that is down or quiet.
/// </summary>
public sealed class StatusStore(ApiClient api) : IDisposable
{
    private static readonly TimeSpan FallbackPoll = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private CancellationTokenSource? _loop;
    private int _watchers;
    private string? _lastEventId;
    private int _refreshQueued;

    public StatusSnapshotDto? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public bool StreamConnected { get; private set; }
    public event Action? Changed;

    /// <summary>Raised for every server event except log lines, with the event type, e.g. "run.finished".</summary>
    public event Action<string>? EventReceived;

    public void Watch()
    {
        if (_watchers++ == 0)
        {
            _loop = new CancellationTokenSource();
            _ = PollLoopAsync(_loop.Token);
            _ = StreamLoopAsync(_loop.Token);
        }
    }

    public void Unwatch()
    {
        if (--_watchers == 0)
        {
            _loop?.Cancel();
            _loop = null;
            StreamConnected = false;
        }
    }

    public async Task RefreshAsync()
    {
        try
        {
            Snapshot = await api.GetAsync<StatusSnapshotDto>("status");
            Error = null;
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException)
        {
            Error = ex.Message;
        }
        Changed?.Invoke();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        await RefreshAsync();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(StreamConnected ? FallbackPoll : TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            await RefreshAsync();
        }
    }

    private async Task StreamLoopAsync(CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await api.OpenStreamAsync("events", _lastEventId, ct);
                await using var body = await response.Content.ReadAsStreamAsync(ct);
                using var reader = new StreamReader(body);
                StreamConnected = true;
                failures = 0;
                await RefreshAsync();

                string? id = null, type = null;
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Length == 0)
                    {
                        if (type is not null)
                            OnEvent(type);
                        if (id is not null)
                            _lastEventId = id;
                        id = type = null;
                    }
                    else if (line.StartsWith("id:", StringComparison.Ordinal))
                        id = line[3..].Trim();
                    else if (line.StartsWith("event:", StringComparison.Ordinal))
                        type = line[6..].Trim();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is ApiException or HttpRequestException or IOException or OperationCanceledException)
            {
                if (ex.IsSignedOut())
                    return;
            }

            StreamConnected = false;
            failures++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures, 5))), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void OnEvent(string type)
    {
        if (type == "resync")
            _lastEventId = null;
        if (type != "run.log")
            EventReceived?.Invoke(type);
        if (type is "run.log" or "run.steps")
            return;
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 0)
            _ = DebouncedRefreshAsync();
    }

    private async Task DebouncedRefreshAsync()
    {
        await Task.Delay(Debounce);
        Interlocked.Exchange(ref _refreshQueued, 0);
        await RefreshAsync();
    }

    public void Dispose() => _loop?.Cancel();
}
