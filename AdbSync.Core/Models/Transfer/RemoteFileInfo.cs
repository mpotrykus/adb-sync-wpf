namespace AdbSync.Core.Models.Transfer;

/// <summary>One entry returned by a single-directory remote listing - "Name" is the leaf name, not a full path.</summary>
public sealed record RemoteFileInfo(string Name, bool IsDirectory, long Size, DateTimeOffset ModifiedUtc);
