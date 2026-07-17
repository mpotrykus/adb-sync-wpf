using AdbSync.Core.Models.Transfer;
using AdbSync.Core.Services.Transfer;
using System.Text;

namespace AdbSync.Core.Tests.Transfer;

/// <summary>In-memory stand-in for a real device's filesystem, keyed by normalized "a/b/c" paths (no leading/trailing slash).</summary>
public sealed class FakeRemoteFileSystem : IRemoteFileSystem
{
    private sealed record Node(bool IsDirectory, byte[] Content, DateTimeOffset ModifiedUtc);

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);
    public List<string> Calls { get; } = [];

    private static string Normalize(string path) => path.Trim('/');

    private void EnsureAncestorDirectories(string normalizedPath, DateTimeOffset modifiedUtc)
    {
        var segments = normalizedPath.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var ancestor = string.Join('/', segments[..i]);
            if (!_nodes.ContainsKey(ancestor))
                _nodes[ancestor] = new Node(true, [], modifiedUtc);
        }
    }

    public void AddDirectory(string path) => _nodes[Normalize(path)] = new Node(true, [], DateTimeOffset.UnixEpoch);

    public void AddFile(string path, string content, DateTimeOffset modifiedUtc)
    {
        var normalized = Normalize(path);
        EnsureAncestorDirectories(normalized, modifiedUtc);
        _nodes[normalized] = new Node(false, Encoding.UTF8.GetBytes(content), modifiedUtc);
    }

    public bool FileExists(string path) => _nodes.TryGetValue(Normalize(path), out var n) && !n.IsDirectory;
    public bool DirectoryExists(string path) => _nodes.TryGetValue(Normalize(path), out var n) && n.IsDirectory;
    public string ReadFile(string path) => Encoding.UTF8.GetString(_nodes[Normalize(path)].Content);

    public Task<IReadOnlyList<RemoteFileInfo>> ListDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        Calls.Add($"list:{remotePath}");
        var prefix = Normalize(remotePath);
        var results = new List<RemoteFileInfo>();

        foreach (var (path, node) in _nodes)
        {
            if (path == prefix)
                continue;
            if (prefix.Length > 0 && !path.StartsWith(prefix + "/", StringComparison.Ordinal))
                continue;

            var relative = prefix.Length == 0 ? path : path[(prefix.Length + 1)..];
            if (relative.Contains('/'))
                continue;

            results.Add(new RemoteFileInfo(relative, node.IsDirectory, node.Content.Length, node.ModifiedUtc));
        }

        return Task.FromResult<IReadOnlyList<RemoteFileInfo>>(results);
    }

    public Task PullFileAsync(string remotePath, string localPath, CancellationToken ct = default)
    {
        Calls.Add($"pull:{remotePath}");
        File.WriteAllBytes(localPath, _nodes[Normalize(remotePath)].Content);
        return Task.CompletedTask;
    }

    public Task PushFileAsync(string localPath, string remotePath, DateTimeOffset modifiedUtc, CancellationToken ct = default)
    {
        Calls.Add($"push:{remotePath}");
        var normalized = Normalize(remotePath);
        EnsureAncestorDirectories(normalized, modifiedUtc);
        _nodes[normalized] = new Node(false, File.ReadAllBytes(localPath), modifiedUtc);
        return Task.CompletedTask;
    }

    public Task DeleteFileAsync(string remotePath, CancellationToken ct = default)
    {
        Calls.Add($"rm:{remotePath}");
        _nodes.Remove(Normalize(remotePath));
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryRecursiveAsync(string remotePath, CancellationToken ct = default)
    {
        Calls.Add($"rmrf:{remotePath}");
        var prefix = Normalize(remotePath);
        foreach (var key in _nodes.Keys.Where(k => k == prefix || k.StartsWith(prefix + "/", StringComparison.Ordinal)).ToList())
            _nodes.Remove(key);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string remotePath, CancellationToken ct = default)
    {
        Calls.Add($"mkdir:{remotePath}");
        var normalized = Normalize(remotePath);
        if (normalized.Length > 0 && !_nodes.ContainsKey(normalized))
            _nodes[normalized] = new Node(true, [], DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
