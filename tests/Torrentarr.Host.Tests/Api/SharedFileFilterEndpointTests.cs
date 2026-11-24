using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class SharedFileFilterEndpointTests(TorrentarrWebApplicationFactory factory) : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Fact]
    public async Task PreviewUsesAuthenticationAndReportsLocalMatchesAndUnavailableLists()
    {
        const string route = "/api/file-filters/preview?instance=Sonarr-FilterFixture&path=Movie/sample.mkv";
        using var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(route)).StatusCode);
        using var client = factory.CreateClientWithApiToken();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        var previousFilters = config.Settings.SharedFileFilters;
        config.ArrInstances["Sonarr-FilterFixture"] = new ArrInstanceConfig();
        try
        {
            using (var json = JsonDocument.Parse(await client.GetStringAsync(route)))
            {
                Assert.True(json.RootElement.GetProperty("available").GetBoolean());
                Assert.Contains("Filename", json.RootElement.GetProperty("reason").GetString());
            }
            config.ArrInstances["Sonarr-FilterFixture"].Torrent.SharedFileFilters = ["missing"];
            config.Settings.SharedFileFilters = [new() { Name = "missing", Source = "https://user:secret@lists.example/private-passkey?token=secret-query" }];
            var preview = await client.GetStringAsync(route);
            Assert.DoesNotContain("private-passkey", preview);
            Assert.DoesNotContain("secret-query", preview);
            Assert.DoesNotContain("user:secret", preview);
            using (var json = JsonDocument.Parse(preview))
            {
                Assert.False(json.RootElement.GetProperty("available").GetBoolean());
                Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("reason").ValueKind);
                Assert.Single(json.RootElement.GetProperty("lists").EnumerateArray());
            }
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(
                "/api/file-filters/preview?instance=Sonarr-FilterFixture&path=" + new string('x', 4097))).StatusCode);
        }
        finally
        {
            config.ArrInstances.Remove("Sonarr-FilterFixture");
            config.Settings.SharedFileFilters = previousFilters;
        }
    }
}
