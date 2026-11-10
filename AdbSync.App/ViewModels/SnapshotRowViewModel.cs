using AdbSync.Core.Models.Orchestration;

namespace AdbSync.App.ViewModels;

public sealed class SnapshotRowViewModel(SnapshotInfo snapshot)
{
    public SnapshotInfo Snapshot { get; } = snapshot;
    public string CreatedText { get; } = snapshot.CreatedAt.LocalDateTime.ToString("g");
    public string KindText { get; } = snapshot.Kind == SnapshotKind.RollbackPoint ? "Before sync" : "Manual";
    public string DevicesText { get; } = snapshot.DeviceNames.Count > 0
        ? string.Join(", ", snapshot.DeviceNames)
        : snapshot.Kind == SnapshotKind.RollbackPoint ? "(master only)" : "(no devices captured)";
}
