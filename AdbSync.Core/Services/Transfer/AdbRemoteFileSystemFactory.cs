using AdbSync.Core.Models.Transfer;
using AdvancedSharpAdbClient;

namespace AdbSync.Core.Services.Transfer;

public sealed class AdbRemoteFileSystemFactory(IAdbClient adbClient) : IRemoteFileSystemFactory
{
    public IRemoteFileSystem Create(string serial) => new AdbSyncRemoteFileSystem(adbClient, serial);
}
