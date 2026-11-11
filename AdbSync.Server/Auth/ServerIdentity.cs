using AdbSync.Core.Services.Companion;
using AdbSync.Core.Services.Config;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace AdbSync.Server.Auth;

/// <summary>
/// The TLS certificate and the address clients reach the server at. By default the certificate is self-signed and
/// clients pin its SHA-256 fingerprint (it arrives in the pairing link). <c>ADBSYNC_TLS_CERT</c> and
/// <c>ADBSYNC_TLS_KEY</c> (PEM files, for example from <c>tailscale cert</c>) replace it with one clients can verify.
/// </summary>
public sealed class ServerIdentity
{
    public const string CertVariable = "ADBSYNC_TLS_CERT";
    public const string KeyVariable = "ADBSYNC_TLS_KEY";
    public const string PublicUrlVariable = "ADBSYNC_PUBLIC_URL";

    public X509Certificate2 Certificate { get; }
    public string PublicUrl { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Certificate.RawData)).ToLowerInvariant();

    public ServerIdentity(AppPaths paths, ServerOptions options)
    {
        var certPath = Environment.GetEnvironmentVariable(CertVariable);
        var keyPath = Environment.GetEnvironmentVariable(KeyVariable);
        Certificate = !string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(keyPath)
            ? X509Certificate2.CreateFromPemFile(certPath, keyPath)
            : new CompanionCertificateStore(paths).Certificate;
        PublicUrl = (Environment.GetEnvironmentVariable(PublicUrlVariable) is { Length: > 0 } url
            ? url
            : $"https://{Environment.MachineName.ToLowerInvariant()}:{options.Port}").TrimEnd('/');
    }

    public string PairingPayload(string code) =>
        $"adbsync://pair?url={Uri.EscapeDataString(PublicUrl)}&code={Uri.EscapeDataString(code)}&fp={Fingerprint}";
}
