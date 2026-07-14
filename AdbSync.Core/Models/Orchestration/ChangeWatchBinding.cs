using AdbSync.Core.Models.Config;
using AdbSync.Core.Services.Transfer;

namespace AdbSync.Core.Models.Orchestration;

public sealed record ChangeWatchBinding(DeviceConfig Device, string RemotePath, IExcludeMatcher Exclude);
