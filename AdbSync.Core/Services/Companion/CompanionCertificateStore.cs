using AdbSync.Core.Services.Config;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdbSync.Core.Services.Companion;

/// <summary>Self-signed TLS certificate for the companion server, created once and kept next to the config.
/// The phone pins its SHA-256 fingerprint (delivered over adb), so no CA or hostname matching is involved.</summary>
public sealed class CompanionCertificateStore(AppPaths paths)
{
    private readonly Lazy<X509Certificate2> _certificate = new(() => LoadOrCreate(paths.CompanionCertificateFile));

    public X509Certificate2 Certificate => _certificate.Value;

    public string Sha256Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    private static X509Certificate2 LoadOrCreate(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var existing = new X509Certificate2(File.ReadAllBytes(path));
                if (existing.HasPrivateKey && existing.NotAfter > DateTime.Now.AddDays(30))
                    return existing;
            }
            catch (CryptographicException)
            {
            }
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=AdbSync Companion", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        using var created = request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(10));

        var pfx = created.Export(X509ContentType.Pfx);
        WritePrivate(path, pfx);
        // Re-import rather than returning `created`: SChannel can't use the ephemeral key a fresh CreateSelfSigned holds.
        return new X509Certificate2(pfx);
    }

    /// <summary>
    /// Written to a temporary file and moved into place, so a crash can't leave a truncated key. The PFX has no
    /// password, so on Linux it's readable by the owner only rather than the default 0644.
    /// </summary>
    private static void WritePrivate(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.tmp-{Guid.NewGuid():N}";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path, overwrite: true);
    }
}
