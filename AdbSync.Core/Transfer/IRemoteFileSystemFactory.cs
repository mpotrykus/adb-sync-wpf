namespace AdbSync.Core.Transfer;

public interface IRemoteFileSystemFactory
{
    IRemoteFileSystem Create(string serial);
}
