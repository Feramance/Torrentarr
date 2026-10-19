using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

public static class ConfigMutationMiddleware
{
    public static void UseConfigMutationGuard(this WebApplication app, string path, bool host = false)
    {
        ConfigMutationGuard.EnableProcessCoordination(path);
        app.Use(async (context, next) =>
        {
            try
            {
                var endpoint = context.Request.Path.Value?.TrimEnd('/').ToLowerInvariant();
                if (endpoint == "/web/config/events") { await next(context); return; }
                // Log streams do not use instance state and can stay open indefinitely.
                if (endpoint != null && (endpoint.StartsWith("/web/logs/") || endpoint.StartsWith("/api/logs/"))
                    && endpoint.EndsWith("/stream", StringComparison.Ordinal))
                {
                    await next(context);
                    return;
                }
                var write = HttpMethods.IsPost(context.Request.Method) && endpoint is
                    "/web/config" or "/api/config" or "/web/config/save" or "/web/config/reload" or "/web/auth/set-password";
                using var ownership = write && !host ? HostConfigOwnership.Acquire(path) : null;
                context.RequestAborted.ThrowIfCancellationRequested();
                var exclusive = false;
                if (write && endpoint is "/web/config" or "/api/config")
                {
                    context.Request.EnableBuffering();
                    try
                    {
                        using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                        exclusive = body.RootElement.ValueKind == JsonValueKind.Object
                            && body.RootElement.TryGetProperty("renames", out var renames)
                            && renames.ValueKind == JsonValueKind.Object && renames.EnumerateObject().Any();
                    }
                    catch (JsonException)
                    {
                        context.Response.StatusCode = StatusCodes.Status400BadRequest;
                        await context.Response.WriteAsJsonAsync(new { error = "Invalid configuration JSON." });
                        return;
                    }
                    finally { context.Request.Body.Position = 0; }
                }
                context.RequestAborted.ThrowIfCancellationRequested();
                using var acquired = !write ? ConfigMutationGuard.EnterProcessing(path)
                    : exclusive ? ConfigMutationGuard.EnterMutation(path) : ConfigMutationGuard.EnterPublication(path);
                context.RequestAborted.ThrowIfCancellationRequested();
                using var configuration = write ? null : context.RequestServices.GetService<TorrentarrConfig>()?.BeginOperation();
                await next(context);
            }
            catch (ConfigurationBusyException ex)
            {
                context.Response.StatusCode = StatusCodes.Status409Conflict;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });
    }
}
