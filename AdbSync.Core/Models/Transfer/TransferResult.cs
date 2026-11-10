namespace AdbSync.Core.Models.Transfer;

/// <summary>
/// CopiedPaths/DeletedPaths carry the relative paths behind the FilesCopied/FilesDeleted counts, so a caller
/// mirroring the same content to multiple devices can de-duplicate rather than summing per-device counts.
/// FailedPaths names the files behind per-file Errors; an error with no matching path means the transfer as a whole
/// is unreliable.
/// </summary>
public sealed record TransferResult(
    int FilesCopied,
    int FilesDeleted,
    long BytesCopied,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> CopiedPaths,
    IReadOnlyList<string> DeletedPaths,
    IReadOnlyList<string>? FailedPaths = null)
{
    public bool HasUnattributedErrors => Errors.Count > (FailedPaths?.Count ?? 0);

    public bool AnyChange => FilesCopied > 0 || FilesDeleted > 0;
}
