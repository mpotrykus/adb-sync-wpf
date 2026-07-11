namespace AdbSync.Core.Models.Devices;

public sealed record WatchAvailability(bool LiveWatchSupported, string Detail);

/// <summary>Marker for "something changed" - inotifyd's per-line detail isn't useful to callers, only its occurrence.</summary>
public readonly record struct ChangeSignal;
