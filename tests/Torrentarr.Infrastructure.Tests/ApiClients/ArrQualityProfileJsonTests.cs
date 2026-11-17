using FluentAssertions;
using Newtonsoft.Json;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.ApiClients;

public sealed class ArrQualityProfileJsonTests
{
    [Fact]
    public void QualityProfile_ReadsMinFormatScore()
    {
        const string json = """
            {"id":1,"name":"HD-1080p","minFormatScore":100,"cutoffFormatScore":50}
            """;

        var profile = JsonConvert.DeserializeObject<QualityProfile>(json);

        profile.Should().NotBeNull();
        profile!.MinCustomFormatScore.Should().Be(100);
    }
}
