using AdbSync.Core.Services.Config;
using Serilog;

namespace AdbSync.Core.Services.Logging;

/// <summary>
/// Rolling, size/age-bounded file logging - replaces the old tool's two logging gaps: a transcript that was
/// overwritten (not appended) every run, and per-device transfer logs that grew forever with no rotation.
/// Returns a raw Serilog logger; callers bridge it into Microsoft.Extensions.Logging via
/// ILoggingBuilder.AddSerilog(logger, dispose: true) (App) or LoggerFactory.Create(b => b.AddSerilog(...)) (Cli).
/// </summary>
public static class AdbSyncLogging
{
    public static Serilog.ILogger CreateFileLogger(AppPaths paths, int retentionDays, long maxBytesPerFile) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogsDir, "transcript-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: Math.Max(1, retentionDays),
                fileSizeLimitBytes: Math.Max(1, maxBytesPerFile),
                rollOnFileSizeLimit: true,
                outputTemplate: "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff}] [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
}
