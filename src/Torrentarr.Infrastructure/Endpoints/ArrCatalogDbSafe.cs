using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Endpoints;

/// <summary>
/// qBitrr 5.12.9 <c>_arr_catalog_db_safe</c>: catalog DB corruption returns 503 and triggers repair.
/// </summary>
public static class ArrCatalogDbSafe
{
    public static IApplicationBuilder UseArrCatalogDbSafe(this IApplicationBuilder app)
    {
        return app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex) when (IsCatalogPath(ctx.Request.Path) && DatabaseRetryExtensions.IsSqliteCorruption(ex))
            {
                var health = ctx.RequestServices.GetService<IDatabaseHealthService>();
                var repaired = health != null && await health.MaintainAsync(repairIfUnhealthy: true, ctx.RequestAborted);
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = repaired
                        ? "Database was repaired — retry shortly"
                        : "Database corruption detected — automatic repair failed"
                });
            }
        });
    }

    internal static bool IsCatalogPath(PathString path)
    {
        var value = path.Value ?? "";
        return value.Contains("/radarr/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/sonarr/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/lidarr/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/readarr/", StringComparison.OrdinalIgnoreCase)
            || value.Contains("/arr/", StringComparison.OrdinalIgnoreCase);
    }
}
