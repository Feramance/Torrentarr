using FluentAssertions;
using Torrentarr.Core.Models;
using Xunit;

namespace Torrentarr.Core.Tests.Models;

public class TorrentInfoTests
{
    [Theory]
    [InlineData("uploading", true)]
    [InlineData("stalledupload", true)]
    [InlineData("queuedupload", true)]
    [InlineData("pausedupload", true)]
    [InlineData("UPLOADING", true)]
    [InlineData("StalledUpload", true)]
    [InlineData("downloading", false)]
    [InlineData("stalleddownload", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    public void IsUploading_DetectsUploadingStates(string state, bool expected)
    {
        var torrent = new TorrentInfo { State = state };

        torrent.IsUploading.Should().Be(expected);
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("stalleddownload", true)]
    [InlineData("queueddownload", true)]
    [InlineData("pauseddownload", true)]
    [InlineData("forceddownload", true)]
    [InlineData("metadata", true)]
    [InlineData("DOWNLOADING", true)]
    [InlineData("StalledDownload", true)]
    [InlineData("uploading", false)]
    [InlineData("stalledupload", false)]
    [InlineData("", false)]
    public void IsDownloading_DetectsDownloadingStates(string state, bool expected)
    {
        var torrent = new TorrentInfo { State = state };

        torrent.IsDownloading.Should().Be(expected);
    }

    [Theory]
    [InlineData("stoppeddownload", true)]
    [InlineData("stoppedupload", true)]
    [InlineData("stopped", true)]
    [InlineData("STOPPEDDOWNLOAD", true)]
    [InlineData("StoppedUpload", true)]
    [InlineData("uploading", false)]
    [InlineData("downloading", false)]
    [InlineData("paused", false)]
    [InlineData("pauseddownload", false)]
    [InlineData("", false)]
    public void IsStopped_DetectsStoppedStates(string state, bool expected)
    {
        var torrent = new TorrentInfo { State = state };

        torrent.IsStopped.Should().Be(expected);
    }

    [Fact]
    public void QBitInstanceName_DefaultValue_IsQBit()
    {
        var torrent = new TorrentInfo();

        torrent.QBitInstanceName.Should().Be("qBit");
    }
}
