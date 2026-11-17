using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Infrastructure.Endpoints;

public static class StatisticsEndpoints
{
    public static void MapStatisticsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/web/free-space/stats", async (IFreeSpaceService storage, CancellationToken ct) =>
            Results.Ok(await storage.GetFreeSpaceStatsAsync(ct)));
        app.MapGet("/web/stats/events", async (HttpContext context, StatisticsEvents events) =>
        {
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache, no-store";
            context.Response.Headers["X-Accel-Buffering"] = "no";
            var (id, reader) = events.Subscribe();
            try
            {
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    using var heartbeat = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
                    heartbeat.CancelAfter(TimeSpan.FromSeconds(15));
                    try
                    {
                        if (!await reader.WaitToReadAsync(heartbeat.Token)) break;
                        while (reader.TryRead(out var revision))
                            await context.Response.WriteAsync($"data: {{\"revision\":{revision}}}\n\n", context.RequestAborted);
                    }
                    catch (OperationCanceledException) when (!context.RequestAborted.IsCancellationRequested)
                    {
                        await context.Response.WriteAsync(": keepalive\n\n", context.RequestAborted);
                    }
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            finally { events.Unsubscribe(id); }
        });
    }
}
