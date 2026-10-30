using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class CorsEndpointTests(TorrentarrWebApplicationFactory factory)
{
    [Fact]
    public async Task Host_CorsPolicyChangesWithoutRestart()
    {
        using var client = factory.CreateClient();
        await AssertLivePolicyAsync(client, factory.Services.GetRequiredService<TorrentarrConfig>());
    }

    internal static async Task AssertLivePolicyAsync(HttpClient client, TorrentarrConfig config)
    {
        var previous = config.WebUI.CorsAllowedOrigins;
        try
        {
            config.WebUI.CorsAllowedOrigins = [];
            await Check("https://first.example", "*", false);
            config.WebUI.CorsAllowedOrigins = ["https://first.example"];
            await Check("https://first.example", "https://first.example", true);
            await Check("https://second.example", null, false);
            config.WebUI.CorsAllowedOrigins = ["https://second.example"];
            await Check("https://first.example", null, false);
            await Check("https://second.example", "https://second.example", true);
            config.WebUI.CorsAllowedOrigins = [];
            await Check("https://first.example", "*", false);
        }
        finally { config.WebUI.CorsAllowedOrigins = previous; }

        async Task Check(string origin, string? allowed, bool credentials)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/web/config");
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var actual = response.Headers.TryGetValues("Access-Control-Allow-Origin", out var origins) ? origins.Single() : null;
            actual.Should().Be(allowed);
            response.Headers.Contains("Access-Control-Allow-Credentials").Should().Be(credentials);
        }
    }
}
