namespace AdbSync.Web.Services;

/// <summary>Polls /status while a page watches it: every 2 s while a job is busy, otherwise every 5 s.</summary>
public sealed class StatusStore(ApiClient api) : IDisposable
{
    private CancellationTokenSource? _loop;
    private int _watchers;

    public StatusSnapshotDto? Snapshot { get; private set; }
    public string? Error { get; private set; }
    public event Action? Changed;

    public void Watch()
    {
        if (_watchers++ == 0)
        {
            _loop = new CancellationTokenSource();
            _ = LoopAsync(_loop.Token);
        }
    }

    public void Unwatch()
    {
        if (--_watchers == 0)
        {
            _loop?.Cancel();
            _loop = null;
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

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await RefreshAsync();
            var busy = Snapshot?.Jobs.Any(j => j.State != "idle") == true;
            try
            {
                await Task.Delay(busy ? 2000 : 5000, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Dispose() => _loop?.Cancel();
}
