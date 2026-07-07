using System.Text.Json;
using System.Text.Json.Serialization;
using AdbSync.Core.Config.Legacy;
using AdbSync.Core.Merge;
using AdbSync.Core.Orchestration;

namespace AdbSync.Core.Config;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GlobalSettings))]
[JsonSerializable(typeof(DevicesFile))]
[JsonSerializable(typeof(ProjectsFile))]
[JsonSerializable(typeof(LegacyDevicesFile))]
[JsonSerializable(typeof(LegacyProjectsFile))]
[JsonSerializable(typeof(SyncManifest))]
[JsonSerializable(typeof(SyncCheckpoint))]
[JsonSerializable(typeof(PushSafetyHistory))]
internal partial class AppConfigJsonContext : JsonSerializerContext;
