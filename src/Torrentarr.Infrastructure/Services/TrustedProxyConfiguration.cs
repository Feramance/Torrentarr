using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace Torrentarr.Infrastructure.Services;

/// <summary>
/// Restricts ASP.NET Core forwarded-header processing to explicitly trusted reverse-proxy
/// addresses. Never accept forwarded headers from arbitrary client IPs.
/// </summary>
public static class TrustedProxyConfiguration
{
    public const string EnvironmentVariable = "TORRENTARR_TRUSTED_PROXIES";

    public static IReadOnlyList<IPAddress> Parse(string? addresses)
    {
        var trusted = new List<IPAddress>();
        foreach (var token in (addresses ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IPAddress.TryParse(token, out var address))
                throw new ArgumentException($"Invalid IP address in {EnvironmentVariable}: '{token}'. Specify literal IPv4 or IPv6 addresses.");
            if (!trusted.Contains(address))
                trusted.Add(address);
        }
        return trusted;
    }

    public static void Configure(IServiceCollection services)
    {
        var trusted = Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (trusted.Count == 0) return;

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders |= ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var proxy in trusted)
                if (!options.KnownProxies.Contains(proxy))
                    options.KnownProxies.Add(proxy);
        });
    }
}
