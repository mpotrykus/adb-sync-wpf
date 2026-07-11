using AdbSync.Core.Models.Transfer;

namespace AdbSync.Core.Services.Transfer;

public interface IRemoteFileSystemFactory
{
    IRemoteFileSystem Create(string serial, TransferPolicy? policy = null);
}
