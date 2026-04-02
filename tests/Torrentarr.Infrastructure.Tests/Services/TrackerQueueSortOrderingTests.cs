using FluentAssertions;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class TrackerQueueSortOrderingTests
{
    [Fact]
    public void BuildOrderedHashes_HigherPriorityCalledLastSoItEndsOnTop()
    {
        var low = new TorrentInfo { Hash = "low", AddedOn = 100 };
        var high = new TorrentInfo { Hash = "high", AddedOn = 200 };
        var sortable = new List<(TorrentInfo, int)> { (low, 1), (high, 10) };

        var ordered = TrackerQueueSortOrdering.BuildOrderedHashesForTopPriorityCalls(sortable);

        // TopPrio is applied in sequence; last call wins → "high" must be last in the list.
        ordered.Should().Equal("low", "high");
    }

    [Fact]
    public void BuildOrderedHashes_SamePriority_UsesEarlierAddedOnFirst()
    {
        var a = new TorrentInfo { Hash = "a", AddedOn = 10 };
        var b = new TorrentInfo { Hash = "b", AddedOn = 20 };
        var sortable = new List<(TorrentInfo, int)> { (b, 5), (a, 5) };

        var ordered = TrackerQueueSortOrdering.BuildOrderedHashesForTopPriorityCalls(sortable);

        ordered.Should().Equal("b", "a");
    }
}
