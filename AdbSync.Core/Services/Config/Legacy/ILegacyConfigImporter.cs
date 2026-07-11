using AdbSync.Core.Models.Config.Legacy;
using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Config.Legacy;

public interface ILegacyConfigImporter
{
    /// <summary>Reads the old PowerShell tool's devices.json/projects.json and maps them onto the new <see cref="AppConfig"/> shape.</summary>
    Task<AppConfig> ImportAsync(string legacyDevicesJsonPath, string legacyProjectsJsonPath, CancellationToken ct = default);
}
