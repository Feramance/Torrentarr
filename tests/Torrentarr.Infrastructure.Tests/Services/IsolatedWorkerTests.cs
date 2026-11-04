using FluentAssertions;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class IsolatedWorkerTests
{
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
