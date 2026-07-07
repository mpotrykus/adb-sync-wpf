using Tmds.MDns;

namespace AdbSync.Core.Devices;

/// <summary>Wraps Tmds.MDns's event-based browsing behind a simple "browse for a fixed window, then return what was seen" call.</summary>
public sealed class MdnsBrowser : IMdnsBrowser
{
    public async Task<IReadOnlyList<MdnsAnnouncement>> BrowseAsync(string serviceType, TimeSpan timeout, CancellationToken ct = default)
    {
        var results = new Dictionary<string, MdnsAnnouncement>();

        void Capture(ServiceAnnouncement a)
        {
            lock (results)
            {
                results[a.Instance] = new MdnsAnnouncement(a.Instance, a.Hostname, a.Port, [.. a.Addresses]);
            }
        }

        var browser = new ServiceBrowser();
        browser.ServiceAdded += (_, e) => Capture(e.Announcement);
        browser.ServiceChanged += (_, e) => Capture(e.Announcement);

        browser.StartBrowse(serviceType);
        try
        {
            await Task.Delay(timeout, ct);
        }
        finally
        {
            browser.StopBrowse();
        }

        lock (results)
        {
            return [.. results.Values];
        }
    }
}
