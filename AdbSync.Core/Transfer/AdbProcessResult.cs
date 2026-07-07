namespace AdbSync.Core.Transfer;

public sealed record AdbProcessResult(int ExitCode, string StandardOutput, string StandardError);
