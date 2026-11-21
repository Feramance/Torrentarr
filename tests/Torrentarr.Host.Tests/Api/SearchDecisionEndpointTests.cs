using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class SearchDecisionEndpointTests(TorrentarrWebApplicationFactory factory) : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Fact]
    public async Task SearchHistoryRequiresKnownWorkerAndUsesApiAuthentication()
    {
        using var anonymous = factory.CreateClient();
        var route = "/api/processes/search-fixture/search-activity";
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);

        using var client = factory.CreateClientWithApiToken();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.ArrInstances["Search-Fixture"] = new ArrInstanceConfig { URI = "http://localhost:7878", Type = "radarr" };
        try
        {
            using var document = JsonDocument.Parse(await client.GetStringAsync(route));
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("preview").ValueKind);
            Assert.Empty(document.RootElement.GetProperty("history").EnumerateArray());
        }
        finally { config.ArrInstances.Remove("Search-Fixture"); }
    }
}
