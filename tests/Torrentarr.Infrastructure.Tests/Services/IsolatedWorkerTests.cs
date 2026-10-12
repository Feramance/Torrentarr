using FluentAssertions;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class IsolatedWorkerTests
{
    [Theory]
    [InlineData(5, 4, 1)]
    [InlineData(5, 7, 0)]
    [InlineData(5, 0, 5)]
    public void LoopDelay_SubtractsWorkAndNeverGoesNegative(int interval, int work, int expected)
    {
        ArrWorkerService.GetLoopDelay(interval, TimeSpan.FromSeconds(work)).Should().Be(TimeSpan.FromSeconds(expected));
    }

    [Fact]
    public void ThrottledSearch_PreservesLastResultAndTimestamp()
    {
        var metrics = new WorkerRuntimeMetrics();
        metrics.RecordSearch(new SearchResult { SearchesTriggered = 2, ItemsSearched = 3 });
        var timestamp = metrics.SearchTimestamp;
        metrics.RecordSearch(null);
        metrics.SearchSummary.Should().Be("2 searches triggered (3 items)");
        metrics.SearchTimestamp.Should().Be(timestamp);
        metrics.MetricType.Should().Be("search");
    }
}
