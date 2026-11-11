using AdbSync.Server.Events;
using System.Collections.Concurrent;

namespace AdbSync.Server.Api;

/// <summary>Thrown inside an operation to fail it with a contract error code.</summary>
public sealed class OperationFailedException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Long-running actions other than sync runs (snapshots, close-and-sync, moving the projects directory): each returns
/// 202 with an Operation right away, then reports progress through <c>operation.updated</c>. Kept in memory; finished
/// operations are dropped after a day.
/// </summary>
public sealed class OperationRegistry(EventBus bus, ILogger<OperationRegistry> logger)
{
    private static readonly TimeSpan KeepFinished = TimeSpan.FromDays(1);
    private readonly ConcurrentDictionary<string, OperationDto> _operations = new();

    public OperationDto? Get(string id) => _operations.GetValueOrDefault(id);

    public IReadOnlyList<OperationDto> List() => _operations.Values.OrderByDescending(o => o.CreatedAt).ToList();

    /// <summary>Starts <paramref name="work"/> in the background; it reports progress through the callback and returns the result.</summary>
    public OperationDto Start(string kind, Guid? jobId, Func<Action<string, double?>, CancellationToken, Task<object?>> work)
    {
        Prune();
        var operation = new OperationDto(Guid.NewGuid().ToString("N"), kind, "running", jobId, DateTimeOffset.UtcNow, null, null, null, null, null);
        Update(operation);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work((message, fraction) => Update(_operations[operation.Id] with
                {
                    Progress = new OperationProgressDto(message, fraction),
                }), CancellationToken.None);
                Update(_operations[operation.Id] with { Status = "succeeded", FinishedAt = DateTimeOffset.UtcNow, Result = result });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Operation {Kind} failed", kind);
                Update(_operations[operation.Id] with
                {
                    Status = "failed",
                    FinishedAt = DateTimeOffset.UtcNow,
                    ErrorCode = ex is OperationFailedException failed ? failed.Code : "internal_error",
                    Message = ex.Message,
                });
            }
        });
        return operation;
    }

    private void Update(OperationDto operation)
    {
        _operations[operation.Id] = operation;
        bus.Publish("operation.updated", operation);
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - KeepFinished;
        foreach (var old in _operations.Values.Where(o => o.FinishedAt < cutoff))
            _operations.TryRemove(old.Id, out _);
    }
}
