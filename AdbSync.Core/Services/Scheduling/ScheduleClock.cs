using AdbSync.Core.Models.Config;

namespace AdbSync.Core.Services.Scheduling;

/// <summary>
/// The wall clock that "daily at HH:mm" schedules and quiet hours are read in: the <see cref="GlobalSettings.TimeZone"/>
/// setting, or the host's local zone when it's unset. A container's local zone is usually UTC, so a server needs the
/// setting (or TZ) for a 07:00 schedule to mean 07:00 where the user is.
/// </summary>
public static class ScheduleClock
{
    public static TimeZoneInfo Zone(GlobalSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.TimeZone))
            return TimeZoneInfo.Local;
        return TimeZoneInfo.TryFindSystemTimeZoneById(settings.TimeZone.Trim(), out var zone) ? zone : TimeZoneInfo.Local;
    }

    public static DateTimeOffset Now(GlobalSettings settings) => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone(settings));

    /// <summary>True when <paramref name="timeZone"/> is empty (host local) or a zone this system knows.</summary>
    public static bool IsValidZone(string? timeZone) =>
        string.IsNullOrWhiteSpace(timeZone) || TimeZoneInfo.TryFindSystemTimeZoneById(timeZone.Trim(), out _);
}
