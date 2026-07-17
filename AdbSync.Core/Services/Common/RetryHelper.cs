namespace AdbSync.Core.Services.Common;

internal static class RetryHelper
{
    /// <summary>Runs <paramref name="action"/>, retrying up to <paramref name="maxAttempts"/> times total with <paramref name="backoff"/> between attempts. The exception from the final attempt propagates.</summary>
    public static async Task ExecuteAsync(Func<Task> action, int maxAttempts, TimeSpan backoff, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception) when (attempt < maxAttempts && !ct.IsCancellationRequested)
            {
                await Task.Delay(backoff, ct);
            }
        }
    }
}
