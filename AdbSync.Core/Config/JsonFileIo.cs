using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace AdbSync.Core.Config;

/// <summary>Shared read/atomic-write helpers for every JSON file the app persists (config, checkpoint, manifests).</summary>
internal static class JsonFileIo
{
    public static async Task<T?> ReadAsync<T>(string path, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return default;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, ct);
    }

    /// <summary>Writes via a temp file + rename so a crash mid-write never leaves a corrupt/partial file in place.</summary>
    public static async Task WriteAtomicAsync<T>(string path, T value, JsonTypeInfo<T> typeInfo, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var tempPath = $"{path}.tmp-{Guid.NewGuid():N}";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, value, typeInfo, ct);
        }
        File.Move(tempPath, path, overwrite: true);
    }
}
