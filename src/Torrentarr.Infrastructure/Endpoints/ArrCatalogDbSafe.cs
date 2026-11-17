using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Torrentarr.Infrastructure.Database;

namespace Torrentarr.Infrastructure.Endpoints;

/// <summary>
/// Report catalog database corruption as service unavailable.
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
                ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    error = "Database corruption detected. Stop Torrentarr and its workers before recovering the database offline."
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
