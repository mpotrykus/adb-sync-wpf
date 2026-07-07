namespace AdbSync.Core.Transfer;

public sealed record TransferResult(int FilesCopied, int FilesDeleted, IReadOnlyList<string> Errors)
{
    public bool AnyChange => FilesCopied > 0 || FilesDeleted > 0;
}
