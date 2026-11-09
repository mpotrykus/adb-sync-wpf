namespace AdbSync.Core.Models.Merge;

/// <summary>A conflict the merge deliberately did not resolve. A null modified time means that side no longer has the file.</summary>
public sealed record HeldConflict(string RelativePath, long? StagingSize, DateTimeOffset? StagingModifiedUtc, long? MasterSize, DateTimeOffset? MasterModifiedUtc);
