using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;

namespace AdbSync.Core.Tests.Companion;

public sealed class CompanionCertificateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "adbsync-cert-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void CreatesCertificateOnce_AndReloadsTheSameOne()
    {
        var paths = new AppPaths(_root);
        var first = new CompanionCertificateStore(paths);

        Assert.True(first.Certificate.HasPrivateKey);
        Assert.True(File.Exists(paths.CompanionCertificateFile));
        Assert.Matches("^[0-9a-f]{64}$", first.Sha256Fingerprint);

        var second = new CompanionCertificateStore(paths);
        Assert.Equal(first.Sha256Fingerprint, second.Sha256Fingerprint);
    }

    [Fact]
    public void RegeneratesWhenFileIsCorrupt()
    {
        var paths = new AppPaths(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(paths.CompanionCertificateFile, [1, 2, 3]);

        var store = new CompanionCertificateStore(paths);

        Assert.True(store.Certificate.HasPrivateKey);
    }
}
