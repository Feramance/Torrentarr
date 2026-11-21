using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class DownloadStallTrackerTests
{
    [Fact]
    public void MetadataTransitionClearsTheWindowAndSustainedMetadataReachesItsThreshold()
    {
        var tracker = new DownloadStallTracker();
        var torrent = new TorrentInfo { Hash = "metadata", AddedOn = 10, State = "metaDL" };
        Assert.Equal(0, tracker.Observe(torrent, true, 100, 60, 1).Seconds);
        Assert.Equal(30, tracker.Observe(torrent, true, 130, 60, 1).Seconds);
        torrent.State = "downloading";
        Assert.Equal(0, tracker.Observe(torrent, false, 145, 60, 1).Seconds);
        torrent.State = "metaDL";
        Assert.Equal(0, tracker.Observe(torrent, true, 160, 60, 1).Seconds);
        Assert.Equal(60, tracker.Observe(torrent, true, 220, 60, 1).Seconds);
    }

    [Fact]
    public void ProgressResetsTheWindowWhileGapsAndRechecksDoNotCountAsProgress()
    {
        var tracker = new DownloadStallTracker();
        var torrent = new TorrentInfo { Hash = "payload", AddedOn = 10, State = "stalledDL", Downloaded = 5, AmountLeft = 10 };
        tracker.Observe(torrent, true, 100, 60, 1);
        torrent.Downloaded = 10;
        torrent.AmountLeft = 5;
        Assert.Equal((0L, true), tracker.Observe(torrent, true, 130, 60, 1));
        torrent.State = "checkingDL";
        torrent.AmountLeft = 4;
        Assert.Equal((0L, false), tracker.Observe(torrent, false, 160, 60, 1));
        torrent.State = "stalledDL";
        Assert.Equal((0L, false), tracker.Observe(torrent, true, 190, 60, 1));
        torrent.Downloaded = 11;
        torrent.AmountLeft = 3;
        Assert.Equal((0L, false), tracker.Observe(torrent, true, 260, 60, 1));
        tracker.InvalidateClient(torrent.ClientInstanceId);
        Assert.Equal((0L, false), tracker.Observe(torrent, true, 290, 60, 1));
        tracker.ForgetMissing(torrent.ClientInstanceId, torrent.Category, []);
        Assert.Equal((0L, false), tracker.Observe(torrent, true, 320, 60, 1));
    }
}
