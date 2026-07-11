using System.Net;

namespace AdbSync.Core.Models.Devices;

public sealed record MdnsAnnouncement(string InstanceName, string Hostname, int Port, IReadOnlyList<IPAddress> Addresses);
