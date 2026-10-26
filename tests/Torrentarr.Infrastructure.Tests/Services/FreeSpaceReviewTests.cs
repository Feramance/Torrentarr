using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class FreeSpaceReviewTests : IDisposable
{
    private readonly TorrentarrConfig _config = new();
    private readonly Mock<ITorrentClientRegistry> _registry = new();
    private readonly Dictionary<string, ITorrentClient> _clients = new(StringComparer.OrdinalIgnoreCase);
    private readonly TorrentarrDbContext _db;

    public FreeSpaceReviewTests()
    {
        _registry.Setup(r => r.GetAllClients()).Returns(_clients);
        _registry.Setup(r => r.GetClient(It.IsAny<string>())).Returns((string id) => _clients.GetValueOrDefault(id));
        _db = new(new DbContextOptionsBuilder<TorrentarrDbContext>().UseSqlite("Data Source=:memory:").Options);
        _db.Database.OpenConnection(); _db.Database.EnsureCreated();
    }

    private Mock<ITorrentClient> Client(string id, bool tags, params TorrentInfo[] torrents)
    {
        _config.TorrentClients[id] = new() { Host = "fixture", DownloadPath = Path.GetTempPath(), FreeSpace = [new() { Path = Path.GetTempPath(), Enabled = true, Limit = "1" }] };
        foreach (var torrent in torrents) torrent.SavePath = Path.GetTempPath();
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent with { Tags = tags });
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync(torrents.ToList());
        client.Setup(c => c.GetTagsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.CreateTagsAsync(It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.PauseTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        client.Setup(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        _clients[id] = client.Object;
        return client;
    }
    private FreeSpaceService Service() => new(NullLogger<FreeSpaceService>.Instance, _config, _registry.Object, _db);

    private void SetLimit(string limit)
    {
        foreach (var client in _config.TorrentClients.Values)
            foreach (var monitor in client.FreeSpace) monitor.Limit = limit;
    }
    private async Task<bool> Pause() => (await Service().ProcessAsync(new HashSet<string> { "movies", "" })).Paused > 0;
    private async Task<bool> Resume() => (await Service().ProcessAsync(new HashSet<string> { "movies", "" })).Resumed > 0;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BulkPauseAndResume_UseOwningCapabilitiesAndDatabase(bool globalTagless)
    {
        var torrent = new TorrentInfo { Hash = "abc", State = "downloading", Category = "movies", AmountLeft = 1 };
        var client = Client("named", false, torrent);
        _config.Settings.Tagless = globalTagless;
        SetLimit(long.MaxValue.ToString());
        (await Pause()).Should().BeTrue();
        var paused = await _db.TorrentLibrary.AsNoTracking().SingleAsync();
        paused.QbitInstance.Should().Be("named"); paused.FreeSpacePaused.Should().BeTrue();
        torrent.State = "pausedDL";
        SetLimit("1");
        (await Resume()).Should().BeTrue();
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeFalse();
        client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("failed")]
    public async Task IncompleteInventory_DoesNotResume_ThenCompletePassRecovers(string failure)
    {
        var paused = new TorrentInfo { Hash = "abc", State = "pausedDL", Category = "movies", Tags = "qBitrr-free_space_paused", AmountLeft = 1 };
        var healthy = Client("healthy", true, paused);
        _config.TorrentClients["unknown"] = new() { Host = "fixture", Disabled = failure == "disabled" };
        if (failure == "failed")
        {
            var failed = Client("unknown", true);
            failed.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException());
        }
        (await Resume()).Should().BeFalse();
        healthy.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _config.TorrentClients.Remove("unknown"); _clients.Remove("unknown");
        (await Resume()).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledClient_DoesNotBlockActiveClientResume(bool categoryPass)
    {
        var paused = new TorrentInfo { Hash = "abc", State = "pausedDL", Category = "movies", Tags = "qBitrr-free_space_paused", AmountLeft = 1 };
        var healthy = Client("healthy", true, paused);
        _config.TorrentClients["disabled"] = new() { Host = "fixture", Disabled = true };

        if (categoryPass) await Service().ProcessAsync(new HashSet<string> { "movies" });
        else (await Resume()).Should().BeTrue();

        healthy.Verify(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CategoryPass_AccountsForUnmanagedActiveDownloads()
    {
        var paused = new TorrentInfo { Hash = "paused", State = "pausedDL", Category = "movies", Tags = "qBitrr-free_space_paused", AmountLeft = 1 };
        var active = new TorrentInfo { Hash = "active", State = "downloading", Category = "other", AmountLeft = long.MaxValue / 2 };
        var client = Client("one", true, paused, active);
        await Service().ProcessAsync(new HashSet<string> { "movies" });
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        client.Verify(c => c.PauseTorrentAsync("active", It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FailedProtectivePause_DiscardsEarlierPlannedResume()
    {
        var paused = new TorrentInfo { Hash = "paused", State = "pausedDL", Category = "movies", Tags = "qBitrr-free_space_paused", AmountLeft = 1, AddedOn = 1 };
        var active = new TorrentInfo { Hash = "active", State = "downloading", Category = "movies", AmountLeft = long.MaxValue / 2, AddedOn = 2 };
        var client = Client("one", true, paused, active);
        client.Setup(c => c.PauseTorrentAsync("active", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        await Service().ProcessAsync(new HashSet<string> { "movies" });
        client.Verify(c => c.PauseTorrentAsync("active", It.IsAny<CancellationToken>()), Times.Once);
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Cancellation_IsNotAnEmptyInventory()
    {
        var client = Client("one", true);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ThrowsAsync(new OperationCanceledException());
        await FluentActions.Awaiting(() => FreeSpaceInventory.ReadAsync(_config, _registry.Object, NullLogger.Instance, default))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SameHashOnDifferentClients_HasIndependentMarkers()
    {
        var first = Client("one", false, new TorrentInfo { Hash = "abc", State = "pausedDL", Category = "movies", AmountLeft = 1 });
        var second = Client("two", false, new TorrentInfo { Hash = "abc", State = "pausedDL", Category = "movies", AmountLeft = 1 });
        _db.TorrentLibrary.Add(new() { Hash = "abc", QbitInstance = "one", FreeSpacePaused = true });
        await _db.SaveChangesAsync();
        (await Resume()).Should().BeTrue();
        first.Verify(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Once);
        second.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
    [Fact]
    public async Task RejectedResume_PreservesDatabasePauseMarker()
    {
        var client = Client("named", false, new TorrentInfo { Hash = "abc", State = "pausedDL", AmountLeft = 1 });
        _db.TorrentLibrary.Add(new() { Hash = "abc", QbitInstance = "named", FreeSpacePaused = true });
        await _db.SaveChangesAsync();
        client.Setup(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (await Resume()).Should().BeFalse();
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task MarkerCleanupFailure_RestoresPauseOrClearsMarkerOnNextActivePass(bool tags, bool restorePause)
    {
        var torrent = new TorrentInfo
        {
            Hash = "abc",
            Category = "movies",
            State = "pausedDL",
            Tags = "qBitrr-free_space_paused",
            AmountLeft = 1
        };
        var client = Client("named", tags, torrent);
        _db.TorrentLibrary.Add(new() { Hash = "abc", QbitInstance = "named", FreeSpacePaused = true });
        await _db.SaveChangesAsync();
        if (!tags) await _db.Database.ExecuteSqlRawAsync("CREATE TRIGGER reject_clear BEFORE UPDATE ON torrentlibrary WHEN NEW.freespacepaused=0 BEGIN SELECT RAISE(ABORT, 'rejected'); END;");
        var rejectClear = true;
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (rejectClear) return false; torrent.Tags = ""; return true; });
        client.Setup(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { torrent.State = "downloading"; return true; });
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => { if (restorePause) torrent.State = "pausedDL"; return restorePause; });
        (await Resume()).Should().BeFalse();
        torrent.State.Should().Be(restorePause ? "pausedDL" : "downloading");
        client.Verify(c => c.PauseTorrentAsync("abc", It.Is<CancellationToken>(ct => !ct.CanBeCanceled)), Times.Once);
        if (!tags) (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeTrue();
        rejectClear = false;
        if (!tags) await _db.Database.ExecuteSqlRawAsync("DROP TRIGGER reject_clear");
        await Resume();
        if (!tags) (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeFalse();
        else torrent.Tags.Should().BeEmpty();
        torrent.State = "pausedDL";
        (await Resume()).Should().BeFalse();
        client.Verify(c => c.ResumeTorrentAsync("abc", It.IsAny<CancellationToken>()), Times.Exactly(restorePause ? 2 : 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualPauseDuringLowSpace_IsNeverMarkedOrAutomaticallyResumed(bool tags)
    {
        var client = Client("named", tags, new TorrentInfo { Hash = "abc", State = "pausedDL", Category = "movies", AmountLeft = 1 });
        SetLimit(long.MaxValue.ToString());
        await Service().ProcessAsync(new HashSet<string> { "movies" });
        client.Verify(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Never);
        (await _db.TorrentLibrary.CountAsync()).Should().Be(0);
        SetLimit("1");
        (await Resume()).Should().BeFalse();
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RejectedPause_RollsBackMarkerAndDoesNotClaimLaterManualPause(bool tags, bool bulk)
    {
        var torrent = new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading", AmountLeft = 1 };
        var client = Client("named", tags, torrent);
        client.Setup(c => c.AddTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<string> _, List<string> values, CancellationToken _) => { if (values.Contains("qBitrr-free_space_paused")) torrent.Tags = "qBitrr-free_space_paused"; return true; });
        client.Setup(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.IsAny<List<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((List<string> _, List<string> values, CancellationToken _) => { if (values.Contains("qBitrr-free_space_paused")) torrent.Tags = ""; return true; });
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        SetLimit(long.MaxValue.ToString());
        if (bulk) (await Pause()).Should().BeFalse();
        else await Service().ProcessAsync(new HashSet<string> { "movies" });
        if (tags)
        {
            torrent.Tags.Should().BeEmpty();
            client.Verify(c => c.RemoveTagsAsync(It.IsAny<List<string>>(), It.Is<List<string>>(values => values.Contains("qBitrr-free_space_paused")),
                It.Is<CancellationToken>(ct => !ct.CanBeCanceled)), Times.Once);
        }
        else (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeFalse();
        torrent.State = "pausedDL";
        SetLimit("1");
        (await Resume()).Should().BeFalse();
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UncertainPause_RetainsMarkerForSafety()
    {
        var torrent = new TorrentInfo { Hash = "abc", Category = "movies", State = "downloading", AmountLeft = 1 };
        var client = Client("named", false, torrent);
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("response lost"));
        SetLimit(long.MaxValue.ToString());
        (await Pause()).Should().BeFalse();
        (await _db.TorrentLibrary.AsNoTracking().SingleAsync()).FreeSpacePaused.Should().BeTrue();
    }

    [Fact]
    public async Task RejectedPause_ReportsMarkerRollbackFailure()
    {
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.PauseTorrentAsync("abc", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var result = await FluentActions.Awaiting(() => FreeSpaceInventory.PauseAndRollbackMarkerAsync(client.Object, "abc",
            () => throw new IOException("marker unavailable"), CancellationToken.None))
            .Should().ThrowAsync<IOException>().WithMessage("*marker rollback failed*");
        result.Which.InnerException!.Message.Should().Be("marker unavailable");
        client.Verify(c => c.ResumeTorrentAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    public void Dispose() => _db.Dispose();
}
