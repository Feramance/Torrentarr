using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
                using var lease = write ? ConfigMutationGuard.EnterMutation(path) : ConfigMutationGuard.EnterProcessing(path);
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
