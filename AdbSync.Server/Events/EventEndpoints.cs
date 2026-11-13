using AdbSync.Server.Auth;
using System.Text.Json;

namespace AdbSync.Server.Events;

public static class EventEndpoints
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void MapEventEndpoints(this IEndpointRouteBuilder api)
    {
        api.MapGet("/events", async (HttpContext http, EventBus bus, string? topics, CancellationToken ct) =>
        {
            var lastSeq = long.TryParse(http.Request.Headers["Last-Event-ID"].ToString(), out var last) ? last : (long?)null;
            var topicSet = (topics ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
            using var subscription = bus.Subscribe(http.CurrentClientId()!.Value, lastSeq, topicSet);

            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.Body.FlushAsync(ct);

            if (subscription.ResyncReason is { } reason)
                await WriteAsync(http, new ServerEvent(bus.CurrentSeq, "resync", DateTimeOffset.UtcNow, new { reason }, null, null), ct);
            foreach (var evt in subscription.Replay)
                await WriteAsync(http, evt, ct);

            while (!ct.IsCancellationRequested)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                wait.CancelAfter(PingInterval);
                bool open;
                try
                {
                    open = await subscription.Live.WaitToReadAsync(wait.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    await http.Response.WriteAsync(": ping\n\n", ct);
                    await http.Response.Body.FlushAsync(ct);
                    continue;
                }
                if (!open)
                    break;
                while (subscription.Live.TryRead(out var evt))
                    await WriteAsync(http, evt, ct);
            }
        });
    }

    private static async Task WriteAsync(HttpContext http, ServerEvent evt, CancellationToken ct)
    {
        var envelope = JsonSerializer.Serialize(new { seq = evt.Seq, type = evt.Type, at = evt.At, data = evt.Data }, Json);
        await http.Response.WriteAsync($"id: {evt.Seq}\nevent: {evt.Type}\ndata: {envelope}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
