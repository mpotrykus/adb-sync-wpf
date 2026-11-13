using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Services.Persistence;

/// <summary>
/// Makes one process the only writer of a data folder. Held as an exclusive open file for the life of the process, so
/// the operating system releases it when the process exits or crashes; nothing depends on PIDs, which repeat in
/// containers.
/// </summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;

    private InstanceLock(FileStream stream) => _stream = stream;

    /// <summary>The lock, or null when another process holds it.</summary>
    public static InstanceLock? TryAcquire(AppPaths paths, string owner)
    {
        Directory.CreateDirectory(paths.Root);
        FileStream stream;
        try
        {
            stream = new FileStream(paths.InstanceLockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }

        stream.SetLength(0);
        using (var writer = new StreamWriter(stream, leaveOpen: true))
            writer.Write($"{owner} on {Environment.MachineName}, started {DateTimeOffset.UtcNow:O}");
        stream.Flush();
        return new InstanceLock(stream);
    }

    public void Dispose() => _stream.Dispose();
}
