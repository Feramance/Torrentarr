using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Torrentarr.Core.Configuration;

namespace Torrentarr.Infrastructure.Services;

public sealed class LiveCorsPolicyProvider(TorrentarrConfig config) : ICorsPolicyProvider
{
    public Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (policyName != "WebUI") return Task.FromResult<CorsPolicy?>(null);
        var policy = new CorsPolicyBuilder().AllowAnyMethod().AllowAnyHeader();
        var origins = config.WebUI.CorsAllowedOrigins;
        if (origins.Count == 0) policy.AllowAnyOrigin();
        else policy.WithOrigins(origins.ToArray()).AllowCredentials();
        return Task.FromResult<CorsPolicy?>(policy.Build());
    }
}
