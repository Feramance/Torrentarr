using System.Collections.Generic;
using FluentAssertions;
using Moq;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Pure-logic tests for SeedingService (no live qBit needed).
/// Tests ExtractTrackerHost (static) and IsHnRSafeToRemoveAsync (pure calculation).
/// </summary>
public class SeedingServiceTests
{
    private static SeedingService CreateService(TorrentarrConfig? config = null, StalledUploadTracker? stalled = null, TorrentInfo? observed = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TorrentarrDbContext(options);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(observed == null ? [] : [observed]);
        client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient(It.IsAny<string>())).Returns(client.Object);
        return new SeedingService(NullLogger<SeedingService>.Instance, dbContext, config ?? new TorrentarrConfig(), registry.Object, stalled);

    }

    [Fact]
    public async Task ProtectedTorrent_StillAppliesConfiguredTrackerRemoval()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", RemoveIfExists = true,
            HitAndRunMode = "and", MinSeedRatio = 99 }]
        };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var torrent = new TorrentInfo { ClientInstanceId = "client", Hash = "hash", AddedOn = 1, Progress = 1, IsPrivate = true };
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>())).ReturnsAsync([torrent]);
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        client.Setup(c => c.RemoveTrackersAsync("hash", It.IsAny<List<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        await service.ApplyTrackerActionsForTorrentAsync(torrent);
        client.Verify(c => c.RemoveTrackersAsync("hash", It.IsAny<List<string>>(), It.IsAny<CancellationToken>()), Times.Once());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SuperSeeding_UsesFinalMatchingRuleAndSkipsUnchangedState(bool observed, bool desired)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example", SuperSeedMode = !desired },
            new() { Uri = "tracker.example", SuperSeedMode = desired }]
        };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>())).ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = 2 }]);
        client.Setup(c => c.SetSuperSeedingAsync("hash", desired, It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var registry = new Mock<ITorrentClientRegistry>(); registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        var torrent = new TorrentInfo { Hash = "hash", ClientInstanceId = "client", SuperSeeding = observed };
        await service.ApplyTrackerActionsForTorrentAsync(torrent);
        await service.ApplyTrackerActionsForTorrentAsync(torrent);
        client.Verify(c => c.SetSuperSeedingAsync("hash", desired, It.IsAny<CancellationToken>()), observed == desired ? Times.Never() : Times.Once());
        client.Verify(c => c.SetSuperSeedingAsync("hash", !desired, It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public async Task OrdinarySeedingLimits_AreIndependentOfHnrDeletionClearance()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            CategorySeeding = new()
            {
                RemoveTorrent = 1,
                MaxUploadRatio = 0.1,
                HitAndRunMode = "and",
                MinSeedRatio = 2,
                MinSeedingTimeDays = 8
            }
        };
        var service = CreateService(config);
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            State = "uploading",
            AddedOn = 1,
            Progress = 1,
            Ratio = 0.5,
            SeedingTime = 100,
            IsPrivate = true
        };
        (await service.SeedingLimitsMetAsync(torrent)).Should().BeTrue();
        (await service.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
    }

    [Fact]
    public async Task CustomFormatRejection_DoesNotRequireOrdinarySeedingLimits()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new() { CategorySeeding = new() { RemoveTorrent = 2, MaxSeedingTime = 100000 } };
        var torrent = new TorrentInfo
        {
            ClientInstanceId = "client",
            Hash = "hash",
            State = "uploading",
            AddedOn = 1,
            Progress = 1,
            IsPrivate = false,
            RatioLimit = -1,
            SeedingTimeLimit = -1
        };
        var service = CreateService(config, observed: torrent);
        (await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.RejectedByCustomFormat)).Allowed.Should().BeTrue();
        (await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.SeedingLimit)).Blockers.Should().Contain(b => b.Code == "seeding.limit");
        config.TorrentClients["client"].CategorySeeding.HitAndRunMode = "and";
        config.TorrentClients["client"].CategorySeeding.MinSeedRatio = 2;
        (await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.RejectedByCustomFormat)).Allowed.Should().BeFalse();
    }

    [Theory]
    [InlineData("and", 1, 1, 0.1, 80000, 1.0, 6460, false)]
    [InlineData("and", 1, 1, 0.1, 90000, 1.0, 0, false)]
    [InlineData("and", 1, 1, 1.0, 86460, 1.0, 0, true)]
    [InlineData("or", 1, 1, 1.0, 100, 1.0, 86360, true)]
    [InlineData("or", 1, 1, 0.1, 86460, 1.0, 0, true)]
    [InlineData("and", 0, 1, 0.0, 86459, 1.0, 1, false)]
    [InlineData("and", 1, 0, 0.1, 100, 1.0, 0, false)]
    [InlineData("and", 0, 0, 0.0, 100, 1.0, 0, true)]
    [InlineData("and", 1, 1, 0.1, 90000, 0.5, 0, false)]
    [InlineData("and", 1, 1, 1.0, 100, 0.5, 0, true)]
    [InlineData("and", 1, 1, 0.0, 0, 0.05, 0, true)]
    public void LegacyDiagnostics_UseClientTimeAndPreserveClearance(string mode, double minimumRatio,
        int minimumDays, double ratio, long seedSeconds, double progress, double remaining, bool eligible)
    {
        foreach (var trackerOverride in new[] { false, true })
        {
            var config = new TorrentarrConfig();
            config.TorrentClients["client"] = new()
            {
                CategorySeeding = new()
                {
                    HitAndRunMode = mode,
                    MinSeedRatio = minimumRatio,
                    MinSeedingTimeDays = minimumDays,
                    TrackerUpdateBuffer = 60
                },
                Trackers = trackerOverride ? [new() { Uri = "tracker.example" }] : []
            };
            var service = CreateService(config);
            var torrent = new TorrentInfo
            {
                ClientInstanceId = "client",
                IsPrivate = true,
                Ratio = ratio,
                SeedingTime = seedSeconds,
                Progress = progress,
                RatioLimit = -1,
                SeedingTimeLimit = -1
            };
            var decision = service.GetProtectionDecisions(torrent,
                [new() { Url = "https://tracker.example/announce", Status = 2 }]).Should().ContainSingle().Subject;
            decision.State.Should().Be(eligible ? "eligible" : "protected");
            decision.ObservedSeedSeconds.Should().Be(seedSeconds);
            decision.RemainingSeedSeconds.Should().Be(remaining);
            decision.LocalEstimate.Should().BeFalse();
            decision.SeedAccountingDescription().Should().Contain("Client-reported").And.Contain("remaining time requirement");
            decision.Reason.Should().Contain("tracker update buffer=60s").And.Contain("mode=" + mode);
        }
    }

    [Theory]
    [InlineData(true, false, "hnr.protected")]
    [InlineData(false, false, "hnr.unknown")]
    [InlineData(true, true, "hnr.protected.native-stop-risk")]
    public async Task RemovalDiagnostic_OnlyHealthyProtectionWaitsAreExpected(bool healthy, bool nativeRisk, string code)
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new() { CategorySeeding = new() { HitAndRunMode = "and", MinSeedRatio = 1, MinSeedingTimeDays = 8 } };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentTrackersAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([new() { Url = "https://tracker.example/announce", Status = healthy ? 2 : 4 }]);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        var decision = await service.EvaluateRemovalAsync(new()
        {
            ClientInstanceId = "client",
            Hash = "hash",
            AddedOn = 1,
            IsPrivate = true,
            Progress = 1,
            SeedingTime = 480000,
            Ratio = 0.048,
            RatioLimit = nativeRisk ? 1 : -1,
            SeedingTimeLimit = -1
        }, TorrentRemovalIntent.RejectedByAllowlist);
        decision.Blockers.Should().ContainSingle().Which.Code.Should().Be(code);
        decision.IsExpectedProtectionWait().Should().Be(code == "hnr.protected");
        decision.Reason.Should().Contain("Client-reported seed=480000s").And.Contain("remaining time requirement=211200s");
    }

    [Fact]
    public async Task ModernDeadlineRisk_RemainsActionableAndUsesLocalObservations()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            Trackers = [new() { Uri = "tracker.example",
            Protection = new() { MinimumSeedHours = 48, FulfillmentHours = 1 } }]
        };
        using var db = new TorrentarrDbContext(new DbContextOptionsBuilder<TorrentarrDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var client = new Mock<ITorrentClient>();
        List<TorrentTracker> trackers = [new() { Url = "https://tracker.example/announce", Status = 2 }];
        client.Setup(c => c.GetTorrentTrackersAsync("hash", It.IsAny<CancellationToken>())).ReturnsAsync(trackers);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("client")).Returns(client.Object);
        var service = new SeedingService(NullLogger<SeedingService>.Instance, db, config, registry.Object);
        var torrent = new TorrentInfo
        {
            Hash = "hash",
            ClientInstanceId = "client",
            AddedOn = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds(),
            IsPrivate = true,
            Progress = 1,
            SeedingTime = 90000,
            RatioLimit = -1,
            SeedingTimeLimit = -1
        };
        var protection = service.GetProtectionDecisions(torrent, trackers).Should().ContainSingle().Subject;
        protection.LocalEstimate.Should().BeTrue();
        protection.ObservedSeedSeconds.Should().Be(0);
        protection.RemainingSeedSeconds.Should().Be(48 * 3600);
        protection.DeadlineRisk.Should().Be("unachievable");
        var decision = await service.EvaluateRemovalAsync(torrent, TorrentRemovalIntent.RejectedByAllowlist);
        decision.IsExpectedProtectionWait().Should().BeFalse();
        decision.Blockers.Should().ContainSingle().Which.Code.Should().Be("hnr.protected.deadline-unachievable");
        decision.Reason.Should().Contain("Locally observed seed=0s").And.Contain("deadline risk=unachievable");
    }

    [Fact]
    public void CategoryOverride_ReplacesOnlyExplicitValues()
    {
        var baseConfig = new CategorySeedingConfig
        {
            MaxUploadRatio = 1.5,
            MaxSeedingTime = 3600,
            HitAndRunMode = "and",
            MinSeedRatio = 2.0,
            Categories =
            [
                new CategorySeedingCategoryOverride
                {
                    Name = "movies",
                    MaxUploadRatio = 4.0,
                    HitAndRunMode = "or"
                }
            ]
        };

        var effective = SeedingService.ApplyCategoryOverride(baseConfig, "movies", matchSubcategories: false);

        effective.MaxUploadRatio.Should().Be(4.0);
        effective.HitAndRunMode.Should().Be("or");
        effective.MaxSeedingTime.Should().Be(3600);
        effective.MinSeedRatio.Should().Be(2.0);
    }

    [Fact]
    public void CategoryOverride_MatchesDescendantOnlyWhenEnabled()
    {
        var baseConfig = new CategorySeedingConfig
        {
            MaxUploadRatio = 1.0,
            Categories = [new CategorySeedingCategoryOverride { Name = "tv", MaxUploadRatio = 3.0 }]
        };

        SeedingService.ApplyCategoryOverride(baseConfig, "tv/4k", matchSubcategories: false)
            .Should().BeSameAs(baseConfig);
        SeedingService.ApplyCategoryOverride(baseConfig, "tv/4k", matchSubcategories: true)
            .MaxUploadRatio.Should().Be(3.0);
    }

    [Fact]
    public void CategoryOverride_NormalizesConfiguredCategoryName()
    {
        var baseConfig = new CategorySeedingConfig
        {
            MaxUploadRatio = 1.0,
            Categories = [new CategorySeedingCategoryOverride { Name = " tv // 4k ", MaxUploadRatio = 3.0 }]
        };

        SeedingService.ApplyCategoryOverride(baseConfig, "tv/4k", matchSubcategories: false)
            .MaxUploadRatio.Should().Be(3.0);
    }

    [Fact]
    public void CategoryOverride_PreservesCaseSensitiveCategoryIdentity()
    {
        var baseConfig = new CategorySeedingConfig
        {
            MaxUploadRatio = 1.0,
            Categories =
            [
                new CategorySeedingCategoryOverride { Name = "movies", MaxUploadRatio = 2.0 },
                new CategorySeedingCategoryOverride { Name = "Movies", MaxUploadRatio = 3.0 }
            ]
        };

        SeedingService.ApplyCategoryOverride(baseConfig, "Movies", matchSubcategories: false)
            .MaxUploadRatio.Should().Be(3.0);
    }

    [Fact]
    public async Task StalledRemoval_RechecksStateAndAgeWithoutRatioRemovalPolicy()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = -1,
                StalledDelay = 5,
                IgnoreTorrentsYoungerThan = 60
            }
        };
        var service = CreateService(config);
        var torrent = new TorrentInfo
        {
            Hash = "stalled",
            ClientInstanceId = "qBit",
            QBitInstanceName = "qBit",
            Category = "tv",
            State = "stalledDL",
            Progress = 0.2,
            IsPrivate = false,
            AddedOn = now - 7200,
            LastActivity = now - 3600
        };
        (await service.CanDeleteTorrentAsync(torrent, "seeding")).Should().BeFalse();
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.StalledDownload, "stalled")).Should().BeTrue();
        torrent.LastActivity = now - 10;
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.StalledDownload, "stalled")).Should().BeFalse();
        torrent.LastActivity = now - 3600;
        torrent.State = "downloading";
        torrent.Availability = 1;
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.StalledDownload, "stalled")).Should().BeFalse();
    }

    [Fact]
    public async Task RejectedAllowlist_RemovalRequiresAgeAndTagClearanceButNotRatioPolicy()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig { CategorySeeding = new CategorySeedingConfig { RemoveTorrent = -1 } };
        var service = CreateService(config);
        var torrent = new TorrentInfo
        {
            Hash = "excluded",
            ClientInstanceId = "qBit",
            QBitInstanceName = "qBit",
            Category = "tv",
            State = "downloading",
            Progress = 0.1,
            IsPrivate = false,
            AddedOn = now - 7200,
            LastActivity = now - 3600
        };
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.RejectedByAllowlist, "allowlist")).Should().BeTrue();
        torrent.Tags = "qBitrr-ignored";
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.RejectedByAllowlist, "allowlist")).Should().BeFalse();
        torrent.Tags = "";
        torrent.AddedOn = now - 2;
        (await service.CanDeleteTorrentForIntentAsync(torrent, TorrentRemovalIntent.RejectedByAllowlist, "allowlist")).Should().BeFalse();
    }

    // ── ExtractTrackerHost ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://tracker.example.com/announce", "tracker.example.com")]
    [InlineData("http://tracker.example.com/announce", "tracker.example.com")]
    [InlineData("udp://tracker.openbitclient.com:1337/announce", "tracker.openbitclient.com")]
    [InlineData("https://tracker.example.com:443/announce", "tracker.example.com")]
    public void ExtractTrackerHost_ReturnsHostOnly(string url, string expected)
    {
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
    }

    [Fact]
    public void ExtractTrackerHost_EmptyString_ReturnsEmpty()
    {
        SeedingService.ExtractTrackerHost("").Should().BeEmpty();
    }

    [Fact]
    public void ExtractTrackerHost_WhitespaceOnly_ReturnsEmpty()
    {
        SeedingService.ExtractTrackerHost("   ").Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://sub.tracker.example.com/announce", "sub.tracker.example.com")]
    [InlineData("https://a.b.c.example.org/announce", "a.b.c.example.org")]
    [InlineData("tracker.torrentleech.org", "tracker.torrentleech.org")]
    [InlineData("torrentleech.org", "torrentleech.org")]
    public void ExtractTrackerHost_SubdomainUrls_ReturnsFullHost(string url, string expected)
    {
        // These tests document that ExtractTrackerHost extracts the full hostname
        // Subdomain matching (sub.example.com matches example.com config) is done
        // in GetTrackerConfigAsync, not in ExtractTrackerHost
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("https://tracker.example.com/a/long/path/announce", "tracker.example.com")]
    [InlineData("udp://tracker.example.com:6969/announce.php", "tracker.example.com")]
    public void ExtractTrackerHost_LongPaths_ReturnsHostOnly(string url, string expected)
    {
        SeedingService.ExtractTrackerHost(url).Should().Be(expected);
    }

    // ── TrackerMessageIndicatesDead (qBitrr #412 parity) ─────────────────────

    [Theory]
    [InlineData("Torrent not found", true)]
    [InlineData("unregistered torrent", true)]
    [InlineData("Host not found (authoritative)", false)]
    [InlineData("DNS lookup failed: not found", false)]
    public void TrackerMessageIndicatesDead_MatchesTorrentSpecificKeywordsOnly(string message, bool expected)
    {
        SeedingService.TrackerMessageIndicatesDead(message).Should().Be(expected);
    }

    // ── IsHnRSafeToRemoveAsync (pure logic) ────────────────────────────────────

    private static TorrentInfo MakeTorrent(double progress, double ratio, long seedingTimeSec)
        => new TorrentInfo
        {
            Hash = "aabbcc",
            AddedOn = 1,
            IsPrivate = false,
            Name = "Test Torrent",
            Progress = progress,
            Ratio = ratio,
            SeedingTime = seedingTimeSec,
            State = "uploading",
            Tags = ""
        };

    private static TrackerConfig HnrConfig(
        string hnrMode = "or",
        double minRatio = 1.0,
        int minDays = 0,
        int minDlPct = 10,
        double partialRatio = 1.0,
        int buffer = 0)
        => new TrackerConfig
        {
            HitAndRunMode = hnrMode,
            MinSeedRatio = minRatio,
            MinSeedingTimeDays = minDays,
            HitAndRunMinimumDownloadPercent = minDlPct,
            HitAndRunPartialSeedRatio = partialRatio,
            TrackerUpdateBuffer = buffer
        };

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_HnrModeOff_AlwaysTrue()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(1.0, 0, 0);
        var config = HnrConfig(hnrMode: "disabled");

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_ProgressBelowMinDownloadPct_SafeToRemove()
    {
        // progress < minDownloadPercent → safe (haven't downloaded enough to count)
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.05, ratio: 0, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10); // 10%

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_PartialProgress_RatioMet_SafeToRemove()
    {
        // partial (between minDl% and 100%) + ratio >= partialRatio → safe
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.5, ratio: 1.0, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10, partialRatio: 1.0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_PartialProgress_RatioNotMet_NotSafe()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 0.5, ratio: 0.5, seedingTimeSec: 0);
        var config = HnrConfig(minDlPct: 10, partialRatio: 1.0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_RatioMet_Safe()
    {
        var svc = CreateService();
        // minRatio=1.0, no time requirement
        var torrent = MakeTorrent(progress: 1.0, ratio: 1.5, seedingTimeSec: 100);
        var config = HnrConfig(minRatio: 1.0, minDays: 0);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_TimeMet_Safe()
    {
        var svc = CreateService();
        // minDays=1 → 86400 sec; torrent seeded for 90000 sec
        var torrent = MakeTorrent(progress: 1.0, ratio: 0.0, seedingTimeSec: 90000);
        var config = HnrConfig(minRatio: 0, minDays: 1); // no ratio requirement, time only

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_NeitherRatioNorTimeMet_NotSafe()
    {
        var svc = CreateService();
        var torrent = MakeTorrent(progress: 1.0, ratio: 0.3, seedingTimeSec: 1000);
        var config = HnrConfig(minRatio: 1.0, minDays: 2); // 2 days = 172800 sec

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsHnRSafeToRemoveAsync_FullDownload_BothRatioAndTime_EitherMet_Safe()
    {
        // When both ratio and time requirements are set, either one being met is sufficient
        var svc = CreateService();
        // Ratio met, time not met
        var torrent = MakeTorrent(progress: 1.0, ratio: 1.5, seedingTimeSec: 1000);
        var config = HnrConfig(minRatio: 1.0, minDays: 2);

        var result = await svc.IsHnRSafeToRemoveAsync(torrent, config);

        result.Should().BeTrue();
    }

    // ── IsUploadingState / IsDownloadingState / IsStoppedState ──────────────────

    [Theory]
    [InlineData("uploading", true)]
    [InlineData("stalledupload", true)]
    [InlineData("queuedupload", true)]
    [InlineData("pausedupload", true)]
    [InlineData("forcedupload", true)]
    [InlineData("downloading", false)]
    [InlineData("stalleddownload", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    [InlineData("pausedUP", true)]
    [InlineData("stoppedUP", true)]
    [InlineData("queuedUP", true)]
    [InlineData("forcedUP", true)]
    public void IsUploadingState_DetectsUploadingStates(string state, bool expected)
    {
        SeedingService.IsUploadingState(state).Should().Be(expected);
    }

    [Theory]
    [InlineData("downloading", true)]
    [InlineData("stalleddownload", true)]
    [InlineData("queueddownload", true)]
    [InlineData("pauseddownload", true)]
    [InlineData("forceddownload", true)]
    [InlineData("metadata", true)]
    [InlineData("uploading", false)]
    [InlineData("stalledupload", false)]
    [InlineData("", false)]
    public void IsDownloadingState_DetectsDownloadingStates(string state, bool expected)
    {
        SeedingService.IsDownloadingState(state).Should().Be(expected);
    }

    [Theory]
    [InlineData("stoppeddownload", true)]
    [InlineData("stoppedupload", true)]
    [InlineData("stopped", true)]
    [InlineData("STOPPEDDOWNLOAD", true)]
    [InlineData("uploading", false)]
    [InlineData("downloading", false)]
    [InlineData("paused", false)]
    [InlineData("", false)]
    public void IsStoppedState_DetectsStoppedStates(string state, bool expected)
    {
        SeedingService.IsStoppedState(state).Should().Be(expected);
    }

    // ── HnrAllowsDeleteAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task HnrAllowsDeleteAsync_NoHnrTrackers_ReturnsTrue()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = new List<TrackerConfig>() // No HnR trackers
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 0, 0);
        torrent.QBitInstanceName = "qBit";

        var result = await svc.HnrAllowsDeleteAsync(torrent, "test deletion");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HnrAllowsDeleteAsync_CategorySeedingHnr_BlocksWhenObligationsUnmet()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = new List<TrackerConfig>(),
            CategorySeeding = new CategorySeedingConfig
            {
                HitAndRunMode = "and",
                MinSeedRatio = 1.0,
                MinSeedingTimeDays = 7
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 0.3, 1000);
        torrent.QBitInstanceName = "qBit";

        var result = await svc.HnrAllowsDeleteAsync(torrent, "removal criteria");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HnrAllowsDeleteAsync_CategorySeedingHnr_AllowsWhenObligationsMet()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            Trackers = new List<TrackerConfig>(),
            CategorySeeding = new CategorySeedingConfig
            {
                HitAndRunMode = "or",
                MinSeedRatio = 1.0,
                MinSeedingTimeDays = 1
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 1.5, 1000);
        torrent.QBitInstanceName = "qBit";

        var result = await svc.HnrAllowsDeleteAsync(torrent, "removal criteria");

        result.Should().BeTrue();
    }

    // ── ShouldRemoveTorrentAsync state-based logic ───────────────────────────────

    [Fact]
    public async Task ShouldRemoveTorrentAsync_DownloadingTorrent_NeverRemoved()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = 1, // Remove on ratio
                MaxUploadRatio = 0.5
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 1.0, 1000); // Ratio met
        torrent.State = "downloading"; // But downloading, not uploading
        torrent.QBitInstanceName = "qBit";

        var result = await svc.ShouldRemoveTorrentAsync(torrent);

        result.Should().BeFalse(); // Downloading torrents are never removed by seeding limits
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_UploadingTorrent_CanBeRemoved()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = 1, // Remove on ratio
                MaxUploadRatio = 0.5
            }
        };
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 1.0, 1000); // Ratio met
        torrent.State = "uploading";
        torrent.QBitInstanceName = "qBit";

        var result = await svc.ShouldRemoveTorrentAsync(torrent);

        result.Should().BeTrue();
    }

    [Fact]
    public void IsUploadingState_StalledUp_IsTrue()
    {
        SeedingService.IsUploadingState("stalledUP").Should().BeTrue();
        SeedingService.IsStalledUploadState("stalledUP").Should().BeTrue();
        SeedingService.IsStalledUploadState("uploading").Should().BeFalse();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_FirstStalledUploadLoop_DoesNotMeetTimeLimit()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var svc = CreateService(config, new StalledUploadTracker(time));
        var torrent = StalledTorrent(seedingTime: 0);

        var result = await svc.ShouldRemoveTorrentAsync(torrent);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_StalledUploadIdle_MeetsTimeLimit()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        var svc = CreateService(config, tracker);
        var torrent = StalledTorrent(seedingTime: 0);

        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(60));
        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_LeavingStalledUpload_ResetsClock()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var svc = CreateService(config, new StalledUploadTracker(time));
        var torrent = StalledTorrent(seedingTime: 0);

        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(30));
        torrent.State = "pausedUP";
        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        torrent.State = "stalledUP";
        time.Advance(TimeSpan.FromSeconds(30));
        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(60));
        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_SeedingTimeStillMeetsWithoutStall()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        var svc = CreateService(config);
        var torrent = MakeTorrent(1.0, 0, 120);
        torrent.State = "uploading";
        torrent.QBitInstanceName = "qBit";

        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeTrue();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_StalledIdle_HnRUnmet_BlocksRemoval()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        config.QBitInstances["qBit"].CategorySeeding.HitAndRunMode = "or";
        config.QBitInstances["qBit"].CategorySeeding.MinSeedRatio = 2.0;
        config.QBitInstances["qBit"].CategorySeeding.MinSeedingTimeDays = 7;
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var svc = CreateService(config, new StalledUploadTracker(time));
        var torrent = StalledTorrent(seedingTime: 0);
        torrent.Ratio = 0.1;
        torrent.Progress = 1.0;

        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(60));
        (await svc.ShouldRemoveTorrentAsync(torrent)).Should().BeFalse();
    }

    [Fact]
    public async Task ShouldRemoveTorrentAsync_SameHashDifferentInstances_IndependentClocks()
    {
        var config = TimeOnlySeedingConfig(limitSeconds: 60);
        config.QBitInstances["qBit-seedbox"] = new QBitConfig
        {
            CategorySeeding = config.QBitInstances["qBit"].CategorySeeding
        };
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var tracker = new StalledUploadTracker(time);
        var svc = CreateService(config, tracker);

        var local = StalledTorrent(seedingTime: 0);
        local.QBitInstanceName = "qBit";
        var seedbox = StalledTorrent(seedingTime: 0);
        seedbox.QBitInstanceName = "qBit-seedbox";

        (await svc.ShouldRemoveTorrentAsync(local)).Should().BeFalse();
        time.Advance(TimeSpan.FromSeconds(60));
        (await svc.ShouldRemoveTorrentAsync(seedbox)).Should().BeFalse();
        (await svc.ShouldRemoveTorrentAsync(local)).Should().BeTrue();
        time.Advance(TimeSpan.FromSeconds(60));
        (await svc.ShouldRemoveTorrentAsync(seedbox)).Should().BeTrue();
    }

    private static TorrentarrConfig TimeOnlySeedingConfig(int limitSeconds)
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new QBitConfig
        {
            CategorySeeding = new CategorySeedingConfig
            {
                RemoveTorrent = 2,
                MaxSeedingTime = limitSeconds,
                HitAndRunMode = "disabled"
            }
        };
        return config;
    }

    private static TorrentInfo StalledTorrent(long seedingTime)
    {
        var torrent = MakeTorrent(1.0, 0, seedingTime);
        torrent.State = "stalledUP";
        torrent.QBitInstanceName = "qBit";
        torrent.Hash = "stalled-hash";
        return torrent;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    [Fact]
    public async Task GetTorrentQueueSortPriorityAsync_UsesTagTierWhenNoAnnounceMatch()
    {
        var svc = CreateService();
        var torrent = new TorrentInfo
        {
            Hash = "abc",
            Name = "t",
            Category = "movies",
            Tags = "vip",
            QBitInstanceName = "qBit"
        };
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["vip"] = 12 };

        var p = await svc.GetTorrentQueueSortPriorityAsync(torrent, map);

        p.Should().Be(12);
    }

    [Fact]
    public async Task GetTorrentQueueSortPriorityAsync_TagPriAtOrBelowNegative100_FallsBackToAnnounce()
    {
        var svc = CreateService();
        var torrent = new TorrentInfo
        {
            Hash = "abc",
            Tags = "low",
            QBitInstanceName = "qBit"
        };
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["low"] = -100 };

        var p = await svc.GetTorrentQueueSortPriorityAsync(torrent, map);

        p.Should().Be(-100);
    }
}
