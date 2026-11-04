using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;
using Serilog.Events;
using Torrentarr.Core.Services;

namespace Torrentarr.Infrastructure.Logging;

public sealed class RequestDiagnosticsMiddleware(RequestDelegate next, Serilog.ILogger? logger = null)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();
        Exception? failure = null;
        try { await next(context); }
        catch (Exception ex) when (context.RequestAborted.IsCancellationRequested && ex is OperationCanceledException or IOException)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception ex) when (!context.Response.HasStarted && ex is TimeoutException or OperationCanceledException or HttpRequestException)
        {
            failure = ex;
            context.Response.StatusCode = ex is TimeoutException or OperationCanceledException
                ? StatusCodes.Status504GatewayTimeout : StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = context.Response.StatusCode == 504
                    ? "An upstream service timed out. Retry shortly." : "An upstream service is unavailable. Retry shortly."
            }, context.RequestAborted);
        }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            var aborted = context.RequestAborted.IsCancellationRequested;
            var status = failure != null && context.Response.StatusCode < 500
                ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            if (aborted) status = 499;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched route";
            var method = context.Request.Method.Replace("\r", "").Replace("\n", "");
            var requestId = context.TraceIdentifier.Replace("\r", "").Replace("\n", "");
            var level = aborted ? LogEventLevel.Debug : failure != null || status >= 500 ? LogEventLevel.Error : status >= 400 ? LogEventLevel.Warning : LogEventLevel.Debug;
            (logger ?? Log.Logger).Write(level,
                "HTTP {Method} {Route} returned {StatusCode} in {ElapsedMs:F1} ms | RequestId={RequestId} | Error={Error}",
                DiagnosticText.Safe(method), DiagnosticText.Safe(route), status, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                DiagnosticText.Safe(requestId), failure == null ? "none" : DiagnosticText.Error(failure));
        }
    }
}
