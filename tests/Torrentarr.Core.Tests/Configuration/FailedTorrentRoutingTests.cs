using Torrentarr.Core.Configuration;
using Xunit;

namespace Torrentarr.Core.Tests.Configuration;

public sealed class FailedTorrentRoutingTests
{
    [Fact]
    public void MoveRequiresIndependentDestinationsForEveryInScopeClient()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit-first"] = new() { Host = "first", MatchSubcategories = true };
        config.QBitInstances["qBit-second"] = new() { Host = "second" };
        config.QBitInstances["qBit-offline"] = new() { Host = "offline", Disabled = true };
        var arr = new ArrInstanceConfig { Category = "movies" };
        config.ArrInstances["Radarr-Movies"] = arr;
        config.ArrInstances["Sonarr-TV"] = new() { Category = "tv" };
        Assert.Null(FailedTorrentRouting.Validate(config, arr));

        arr.Torrent.FailedTorrentAction = FailedTorrentRouting.Move;
        arr.Torrent.FailedTorrentDestinations.Add(new() { Client = "qBit-first", Category = "parked-a" });
        Assert.Contains("qBit-second", FailedTorrentRouting.Validate(config, arr));
        arr.Torrent.FailedTorrentDestinations.Add(new() { Client = "qBit-second", Category = "parked-b" });
        Assert.Null(FailedTorrentRouting.Validate(config, arr));

        foreach (var category in new[] { "movies", "movies/archive", "tv", config.Settings.FailedCategory,
            config.Settings.RecheckCategory, config.Settings.FailedCategory + "/archive" })
            Assert.NotNull(FailedTorrentRouting.DestinationError(config, arr, "qBit-first", category));
    }
}
