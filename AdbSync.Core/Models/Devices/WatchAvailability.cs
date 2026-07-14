namespace AdbSync.Core.Models.Devices;

/// <summary><paramref name="Warning"/> is set when live watch is supported but the tree is large enough that the
/// connection may still drop under load - unlike <paramref name="Detail"/>, it doesn't change what the caller does,
/// only what gets shown to the user.</summary>
public sealed record WatchAvailability(bool LiveWatchSupported, string Detail, string? Warning = null);

/// <summary>Marker for "something changed" - inotifyd's per-line detail isn't useful to callers, only its occurrence.</summary>
public readonly record struct ChangeSignal;
