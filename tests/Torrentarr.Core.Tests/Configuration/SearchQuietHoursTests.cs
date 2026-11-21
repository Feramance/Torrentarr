using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class SearchQuietHoursTests
{
    [Theory]
    [InlineData("2026-11-21T21:59:59Z", null)]
    [InlineData("2026-11-21T22:00:00Z", "2026-11-22T07:00:00Z")]
    [InlineData("2026-11-22T06:59:59Z", "2026-11-22T07:00:00Z")]
    [InlineData("2026-11-22T07:00:00Z", null)]
    public void MidnightWindowIncludesOnlyItsStart(string now, string? next)
    {
        var config = new SearchConfig { QuietHoursEnabled = true };
        Assert.Equal(next == null ? (DateTimeOffset?)null : DateTimeOffset.Parse(next),
            SearchQuietHours.NextEligible(config, DateTimeOffset.Parse(now)));
    }

    [Theory]
    [InlineData("2026-03-29T00:59:59Z", "2026-03-29T01:00:00Z")]
    [InlineData("2026-10-25T00:15:00Z", "2026-10-25T00:30:00Z")]
    [InlineData("2026-10-25T01:15:00Z", "2026-10-25T01:30:00Z")]
    public void DaylightSavingEvaluatesRealInstants(string now, string next)
    {
        var config = new SearchConfig
        {
            QuietHoursEnabled = true,
            QuietHoursStart = "01:00",
            QuietHoursEnd = "02:30",
            QuietHoursTimeZone = "Europe/Malta"
        };
        Assert.Equal(DateTimeOffset.Parse(next), SearchQuietHours.NextEligible(config, DateTimeOffset.Parse(now)));
    }

    [Theory]
    [InlineData("25:00", "07:00", "Etc/UTC")]
    [InlineData("22:00", "22:00", "Etc/UTC")]
    [InlineData("22:00", "07:00", "Unknown/Zone")]
    [InlineData("22:00", "07:00", "")]
    public void InvalidWindowsAreRejected(string start, string end, string zone) =>
        Assert.NotNull(SearchQuietHours.Validate(new() { QuietHoursStart = start, QuietHoursEnd = end, QuietHoursTimeZone = zone }));
}
