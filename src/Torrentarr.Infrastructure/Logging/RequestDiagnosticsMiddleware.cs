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
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            var status = failure == null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched route";
            var method = context.Request.Method.Replace("\r", "").Replace("\n", "");
            var requestId = context.TraceIdentifier.Replace("\r", "").Replace("\n", "");
            var level = failure != null || status >= 500 ? LogEventLevel.Error : status >= 400 ? LogEventLevel.Warning : LogEventLevel.Debug;
            (logger ?? Log.Logger).Write(level,
                "HTTP {Method} {Route} returned {StatusCode} in {ElapsedMs:F1} ms | RequestId={RequestId} | Error={Error}",
                DiagnosticText.Safe(method), DiagnosticText.Safe(route), status, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                DiagnosticText.Safe(requestId), failure == null ? "none" : DiagnosticText.Error(failure));
        }
    }
}
