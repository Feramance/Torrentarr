using System.Net;
using System.Text.Json;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class FailedTorrentDestinationEndpointTests(TorrentarrWebApplicationFactory factory)
    : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Fact]
    public async Task CategoryInventoryUsesExistingApiAuthentication()
    {
        using var anonymous = factory.CreateClient();
        const string route = "/api/config/failed-torrent-destinations?category=movies";
        using var rejected = await anonymous.GetAsync(route);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        using var client = factory.CreateClientWithApiToken();
        using var document = JsonDocument.Parse(await client.GetStringAsync(route));
        Assert.Equal(JsonValueKind.Array, document.RootElement.ValueKind);
    }
}
