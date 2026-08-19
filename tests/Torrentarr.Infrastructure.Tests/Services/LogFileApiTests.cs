using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Torrentarr.Infrastructure.Endpoints;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LogFileApiTests
{
    [Fact]
    public void Search_FindsMatchingLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "All.log"), "alpha\nerror: boom\nbeta\n");
            var result = LogFileApi.Search(dir, "All.log", "error", null, null, "0", 10, 0);
            ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Search_RequiresQuery()
    {
        var result = LogFileApi.Search("/tmp", "All.log", "", null, null, null, null, null);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
    }

    [Fact]
    public void IsValidLogFileName_RejectsTraversal()
    {
        LogFileApi.IsValidLogFileName("../secret.log").Should().BeFalse();
        LogFileApi.IsValidLogFileName("All.log").Should().BeTrue();
    }
}
