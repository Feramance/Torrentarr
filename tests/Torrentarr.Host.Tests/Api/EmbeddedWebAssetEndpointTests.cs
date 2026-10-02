using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public class EmbeddedWebAssetEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public EmbeddedWebAssetEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task UiRoute_ServesEmbeddedIndexAndAsset()
    {
        var client = _factory.CreateClient();

        var indexResponse = await client.GetAsync("/ui/");
        indexResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var index = await indexResponse.Content.ReadAsStringAsync();
        index.Should().Contain("<html");

        var assetPath = Regex.Match(index, "src=\"(?<path>/assets/[^\"]+\\.js)\"").Groups["path"].Value;
        assetPath.Should().NotBeNullOrWhiteSpace();

        var assetResponse = await client.GetAsync(assetPath);
        assetResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        (await assetResponse.Content.ReadAsByteArrayAsync()).Should().NotBeEmpty();
    }
}
