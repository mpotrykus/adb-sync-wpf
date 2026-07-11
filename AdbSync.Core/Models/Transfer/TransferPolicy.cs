namespace AdbSync.Core.Models.Transfer;

/// <summary>Per-file retry/throttle behavior applied by a transfer engine. <see cref="None"/> disables both.</summary>
public sealed record TransferPolicy(int RetryMaxAttempts = 1, TimeSpan RetryBackoff = default, int? BandwidthThrottleKBps = null)
{
    public static readonly TransferPolicy None = new();
}
