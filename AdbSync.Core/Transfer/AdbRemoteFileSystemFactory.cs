using AdvancedSharpAdbClient;

namespace AdbSync.Core.Transfer;

public sealed class AdbRemoteFileSystemFactory(IAdbClient adbClient) : IRemoteFileSystemFactory
{
    public IRemoteFileSystem Create(string serial) => new AdbSyncRemoteFileSystem(adbClient, serial);
}
