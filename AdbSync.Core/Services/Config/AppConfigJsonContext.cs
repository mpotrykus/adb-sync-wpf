using AdbSync.Core.Models.Config;
using AdbSync.Core.Models.Config.Legacy;
using AdbSync.Core.Models.Merge;
using AdbSync.Core.Models.Orchestration;
using System.Text.Json.Serialization;

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
[JsonSerializable(typeof(DashboardUiState))]
internal partial class AppConfigJsonContext : JsonSerializerContext;
