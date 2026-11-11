namespace AdbSync.Core.Models.Orchestration;

/// <summary>
/// Why a run failed or was skipped, for clients to branch on instead of the message text. Maps onto the API's
/// <c>ErrorCode</c> (for example <see cref="PushSafetyBlocked"/> is <c>push_safety_blocked</c>). Stored by name.
/// </summary>
public enum RunErrorCode
{
    InternalError,
    ValidationFailed,
    ProjectFolderUnavailable,
    PushSafetyBlocked,
    AppRunning,
    AlreadyRunning,
    DeviceUnreachable,
}
