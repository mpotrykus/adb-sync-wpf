using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Models.Orchestration;

public sealed record ChangeWatchBinding(DeviceConfig Device, string RemotePath);
