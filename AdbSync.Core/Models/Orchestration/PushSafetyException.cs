namespace AdbSync.Core.Models.Orchestration;

public sealed class PushSafetyException(string message) : Exception(message);
