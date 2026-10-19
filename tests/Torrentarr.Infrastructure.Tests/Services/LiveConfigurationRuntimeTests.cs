using FluentAssertions;
using Serilog.Events;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LiveConfigurationRuntimeTests
{
    [Theory]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("DEBUG", LogEventLevel.Debug)]
    [InlineData("WARN", LogEventLevel.Warning)]
    [InlineData("ERROR", LogEventLevel.Error)]
    [InlineData("CRITICAL", LogEventLevel.Fatal)]
    [InlineData(null, LogEventLevel.Information)]
    [InlineData("unknown", LogEventLevel.Information)]
    public void ParseLevel_MapsConfiguredLevels(string? value, LogEventLevel expected)
    {
        LiveConfigurationRuntime.ParseLevel(value).Should().Be(expected);
    }
}
