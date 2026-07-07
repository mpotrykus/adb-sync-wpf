namespace AdbSync.Core.Devices;

public interface IMdnsBrowser
{
    /// <summary>Browses for <paramref name="serviceType"/> (e.g. "_adb-tls-connect._tcp") for a fixed window and returns whatever was seen.</summary>
    Task<IReadOnlyList<MdnsAnnouncement>> BrowseAsync(string serviceType, TimeSpan timeout, CancellationToken ct = default);
}
