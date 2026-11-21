using Newtonsoft.Json.Linq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class SearchPacingTests
{
    [Fact]
    public void CooldownUsesPositiveMediaEvidenceAndExpiresAtTheBoundary()
    {
        var at = DateTimeOffset.Parse("2026-11-17T12:00:00Z").ToUnixTimeSeconds();
        var events = JArray.Parse("""
            [
                {"eventType":"grabbed","movieId":1,"date":"2026-11-17T12:00:00Z"},
                {"eventType":"grabbed","movieId":1,"date":"2026-11-17T11:59:00Z"},
                {"eventType":"downloadFolderImported","movieId":2,"date":"2026-11-17T12:00:00Z"}
            ]
            """);
        var grabs = SearchPacing.ReadGrabDates("radarr", events, at);
        var snapshot = new SearchPacingObservation(1, grabs);
        Assert.Contains("Recent grab", snapshot.GrabHold(1, 2, at + 119));
        Assert.Null(snapshot.GrabHold(1, 2, at + 120));
        Assert.Null(snapshot.GrabHold(2, 2, at));
        Assert.Equal("Arr grab history unavailable", new SearchPacingObservation(null, null).GrabHold(1, 2, at));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(null, true)]
    public void CeilingIncludesItsBoundaryAndHoldsWhenEvidenceIsUnavailable(int? count, bool hold)
    {
        var snapshot = new SearchPacingObservation(count, null);
        Assert.Equal(hold, snapshot.DownloadHold(2) != null);
        Assert.Null(snapshot.DownloadHold(0));
    }

    [Fact]
    public void DownloadsAreCountedOnceInTheExistingCategoryScope()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig { MatchSubcategories = true };
        var arr = new ArrInstanceConfig { Category = "movies" };
        config.ArrInstances["Radarr"] = arr;
        config.ArrInstances["Radarr-4K"] = new ArrInstanceConfig { Category = "movies/4k" };
        var torrents = new List<TorrentInfo>
        {
            new() { Category = "movies", Hash = "ABC", State = "downloading" },
            new() { Category = "movies", Hash = "abc", State = "stalledDL" },
            new() { Category = "movies/other", Hash = "def", State = "forcedMetaDL" },
            new() { Category = "movies/4k", Hash = "ghi", State = "downloading" },
            new() { Category = "movies", Hash = "jkl", State = "queuedDL" }
        };
        Assert.Equal(2, SearchPacing.CountActive(config, arr, "qBit", torrents));
        torrents.Add(new TorrentInfo { Category = "movies", Hash = "mno", State = "unknown" });
        Assert.Null(SearchPacing.CountActive(config, arr, "qBit", torrents));
    }
}
