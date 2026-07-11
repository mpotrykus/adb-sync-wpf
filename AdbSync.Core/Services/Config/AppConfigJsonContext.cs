using AdbSync.Core.Models.Config;
using System.Text.Json;
using System.Text.Json.Serialization;
using AdbSync.Core.Models.Config.Legacy;
using AdbSync.Core.Services.Config.Legacy;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Services.Merge;
using AdbSync.Core.Models.Orchestration;
using AdbSync.Core.Services.Orchestration;

namespace AdbSync.Core.Services.Config;

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
