using FluentAssertions;
using Moq;
using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class QbitOverviewBuilderTests
{
    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 1)]
    [InlineData(null, true, 2)]
    public async Task Overview_UsesEffectiveOwnerSubcategorySetting(bool? arrOverride, bool clientDefault, int expected)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", MatchSubcategories = clientDefault };
        config.ArrInstances["Radarr"] = new() { Category = "movies", MatchSubcategories = arrOverride };
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Category = "movies", Size = 10 }, new() { Category = "movies/4k", Size = 20 }]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("one")).Returns(client.Object);
        var result = JObject.FromObject(await QbitOverviewBuilder.BuildAsync(config, registry.Object, null));
        var category = result["categories"]!.Single();
        category["torrentCount"]!.Value<int>().Should().Be(expected);
        category["totalSize"]!.Value<long>().Should().Be(expected == 2 ? 30 : 10);
    }
}
