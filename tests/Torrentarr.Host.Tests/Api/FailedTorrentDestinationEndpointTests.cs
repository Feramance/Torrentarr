using System.Net;
using System.Net.Http.Json;
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
    [Theory]
    [InlineData("/web/config")]
    [InlineData("/api/config")]
    public async Task DestinationMappingSurvivesPartialSaveAndClientRename(string route)
    {
        using var client = factory.CreateClientWithApiToken();
        const string arr = "Radarr-ParkingFixture";
        const string name = "parking-fixture";
        const string renamed = "parking-fixture-renamed";
        Task<HttpResponseMessage> Save(Dictionary<string, object?> changes, Dictionary<string, string>? renames = null) =>
            client.PostAsJsonAsync(route, new { changes, renames = renames ?? new() });

        try
        {
            using var original = await Save(new()
            {
                [$"TorrentClient.{name}.Host"] = "fixture",
                [$"TorrentClient.{name}.Disabled"] = true,
                [$"{arr}.Category"] = "test-parking",
                [$"{arr}.Managed"] = false,
                [$"{arr}.Torrent.FailedTorrentAction"] = "move",
                [$"{arr}.Torrent.FailedTorrentDestinations"] = new[] { new { Client = name, Category = "parked" } }
            });
            original.EnsureSuccessStatusCode();
            using var partial = await Save(new() { [$"{arr}.ReSearch"] = false });
            partial.EnsureSuccessStatusCode();
            using var rename = await Save(new()
            {
                [$"TorrentClient.{name}"] = null,
                [$"TorrentClient.{renamed}.Host"] = "fixture",
                [$"TorrentClient.{renamed}.Disabled"] = true
            }, new() { [name] = renamed });
            rename.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await client.GetStringAsync(route));
            var integrations = route == "/api/config" ? document.RootElement.GetProperty("ArrInstances") : document.RootElement;
            var torrent = integrations.GetProperty(arr).GetProperty("Torrent");
            Assert.Equal("move", torrent.GetProperty("FailedTorrentAction").GetString());
            var destination = Assert.Single(torrent.GetProperty("FailedTorrentDestinations").EnumerateArray());
            Assert.Equal(renamed, destination.GetProperty("Client").GetString());
            Assert.Equal("parked", destination.GetProperty("Category").GetString());
        }
        finally
        {
            using var cleanup = await Save(new()
            {
                [arr] = null,
                [$"TorrentClient.{name}"] = null,
                [$"TorrentClient.{renamed}"] = null
            });
            cleanup.EnsureSuccessStatusCode();
        }
    }
}
