namespace AdbSync.Core.Models.Transfer;

/// <summary>Per-file retry/throttle behavior applied by a transfer engine. <see cref="None"/> disables both.
/// <see cref="SkipPull"/> lets a pull leave out remote files the caller already knows are unchanged; the engine reports
/// them in <see cref="TransferResult.SkippedUnchanged"/> instead of transferring them. Engines that cannot honor it
/// simply pull everything.</summary>
public sealed record TransferPolicy(
    int RetryMaxAttempts = 1, TimeSpan RetryBackoff = default, int? BandwidthThrottleKBps = null, Func<FileEntry, bool>? SkipPull = null)
{
    public static readonly TransferPolicy None = new();
}
