namespace AdbSync.Core.Models.Merge;

/// <summary>A file changed on a device and in master since their last sync that nobody has picked a version for yet. A null modified time means that side deleted it.</summary>
public sealed class PendingConflict
{
    public string Id { get; set; } = string.Empty;
    public string JobName { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public DateTimeOffset DetectedAtUtc { get; set; }
    public long? DeviceSize { get; set; }
    public DateTimeOffset? DeviceModifiedUtc { get; set; }
    public long? MasterSize { get; set; }
    public DateTimeOffset? MasterModifiedUtc { get; set; }
}

public sealed class PendingConflictsFile
{
    public List<PendingConflict> Items { get; set; } = [];
}
