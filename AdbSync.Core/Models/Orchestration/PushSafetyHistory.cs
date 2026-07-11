namespace AdbSync.Core.Models.Orchestration;

/// <summary>Per-job record of the highest file count ever seen in each device's pulled mirror, used as the push-safety baseline.</summary>
public sealed class PushSafetyHistory
{
    public Dictionary<string, int> MaxFileCounts { get; set; } = [];
}
