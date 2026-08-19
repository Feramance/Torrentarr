using FluentAssertions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrWorkerManagerRssTimerTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(15, true)]
    public void IsPeriodicCommandEnabled_DisablesNonPositiveTimers(int minutes, bool expected)
    {
        ArrWorkerManager.IsPeriodicCommandEnabled(minutes).Should().Be(expected);
    }
}
