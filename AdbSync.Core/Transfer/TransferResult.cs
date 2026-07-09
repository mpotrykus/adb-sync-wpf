namespace AdbSync.Core.Transfer;

/// <summary>
/// CopiedPaths/DeletedPaths carry the relative paths behind the FilesCopied/FilesDeleted counts, so a caller
/// mirroring the same content to multiple devices can de-duplicate rather than summing per-device counts.
/// </summary>
public sealed record TransferResult(
    int FilesCopied,
    int FilesDeleted,
    long BytesCopied,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> CopiedPaths,
    IReadOnlyList<string> DeletedPaths)
{
    public bool AnyChange => FilesCopied > 0 || FilesDeleted > 0;
}
