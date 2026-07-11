using AdbSync.Core.Models.Transfer;
using System.Diagnostics;

namespace AdbSync.Core.Services.Transfer;

public sealed class AdbProcessRunner(string adbExecutablePath = "adb") : IAdbProcessRunner
{
    public async Task<AdbProcessResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(adbExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{adbExecutablePath}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        return new AdbProcessResult(process.ExitCode, await stdOutTask, await stdErrTask);
    }
}
