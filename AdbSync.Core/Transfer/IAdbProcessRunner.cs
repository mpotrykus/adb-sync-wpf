namespace AdbSync.Core.Transfer;

/// <summary>The one seam that actually talks to adb.exe - isolated so everything else in Transfer/ is unit-testable without a device.</summary>
public interface IAdbProcessRunner
{
    Task<AdbProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default);
}
