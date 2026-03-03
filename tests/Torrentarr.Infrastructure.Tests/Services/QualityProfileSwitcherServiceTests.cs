using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for QualityProfileSwitcherService (§1.2 UseTempForMissing).
/// Focuses on the early-return guard clauses in the three public methods —
/// these paths complete without making any API or DB calls, so no mocking is needed.
/// The "happy path" (actual profile switching) requires a live Arr instance and is
/// covered by live integration tests.
/// </summary>
public class QualityProfileSwitcherServiceTests
{
    private static QualityProfileSwitcherService CreateService(string? dbName = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(dbName ?? Guid.NewGuid().ToString())
            .Options;
        var db = new TorrentarrDbContext(options);
        return new QualityProfileSwitcherService(
            NullLogger<QualityProfileSwitcherService>.Instance, db);
    }

    // ── ForceResetAllTempProfilesAsync ────────────────────────────────────────

    [Fact]
    public async Task ForceResetAll_ForceResetFalse_ReturnsWithoutError()
    {
        var svc = CreateService();
        // ForceResetTempProfiles defaults to false — method should return immediately
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig { ForceResetTempProfiles = false }
        };

        var act = async () => await svc.ForceResetAllTempProfilesAsync("radarr", cfg);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    public async Task ForceResetAll_ForceResetFalse_AllArrTypes_ReturnQuickly(string arrType)
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = arrType,
            Search = new SearchConfig { ForceResetTempProfiles = false }
        };

        var act = async () => await svc.ForceResetAllTempProfilesAsync(arrType, cfg);

        await act.Should().NotThrowAsync();
    }

    // ── RestoreTimedOutProfilesAsync ──────────────────────────────────────────

    [Fact]
    public async Task RestoreTimedOut_UseTempForMissingFalse_ReturnWithoutError()
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig { UseTempForMissing = false }
        };

        var act = async () => await svc.RestoreTimedOutProfilesAsync("radarr", cfg);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RestoreTimedOut_KeepTempProfileTrue_ReturnWithoutError()
    {
        var svc = CreateService();
        // KeepTempProfile=true → profiles should never be restored
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                KeepTempProfile = true
            }
        };

        var act = async () => await svc.RestoreTimedOutProfilesAsync("radarr", cfg);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RestoreTimedOut_TimeoutMinutesZero_ReturnWithoutError()
    {
        var svc = CreateService();
        // TempProfileResetTimeoutMinutes=0 is the "never reset" sentinel
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                KeepTempProfile = false,
                TempProfileResetTimeoutMinutes = 0
            }
        };

        var act = async () => await svc.RestoreTimedOutProfilesAsync("radarr", cfg);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RestoreTimedOut_TimeoutMinutesNegative_ReturnWithoutError()
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                KeepTempProfile = false,
                TempProfileResetTimeoutMinutes = -1
            }
        };

        var act = async () => await svc.RestoreTimedOutProfilesAsync("radarr", cfg);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData("radarr")]
    [InlineData("sonarr")]
    [InlineData("lidarr")]
    public async Task RestoreTimedOut_EmptyDb_AllArrTypes_NoItemsToRestore(string arrType)
    {
        // UseTempForMissing=true, timeout>0, empty DB → query returns empty list → no-op
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = arrType,
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                KeepTempProfile = false,
                TempProfileResetTimeoutMinutes = 30
            }
        };

        var act = async () => await svc.RestoreTimedOutProfilesAsync(arrType, cfg);

        await act.Should().NotThrowAsync();
    }

    // ── SwitchToTempProfilesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task SwitchToTemp_UseTempForMissingFalse_ReturnWithoutError()
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig { UseTempForMissing = false }
        };

        var act = async () => await svc.SwitchToTempProfilesAsync(
            "radarr", cfg, [new SearchCandidate { Reason = "Missing" }]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SwitchToTemp_EmptyQualityProfileMappings_ReturnWithoutError()
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                QualityProfileMappings = []
            }
        };

        var act = async () => await svc.SwitchToTempProfilesAsync(
            "radarr", cfg, [new SearchCandidate { Reason = "Missing" }]);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SwitchToTemp_NoCandidatesWithMissingReason_ReturnWithoutError()
    {
        var svc = CreateService();
        // Mappings configured, but no "Missing" candidates — only "Upgrade"
        var cfg = new ArrInstanceConfig
        {
            Type = "radarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                QualityProfileMappings = new Dictionary<string, string> { ["HD-1080p"] = "Any" }
            }
        };
        var candidates = new[] { new SearchCandidate { Reason = "Upgrade" } };

        var act = async () => await svc.SwitchToTempProfilesAsync("radarr", cfg, candidates);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SwitchToTemp_EmptyCandidatesList_ReturnWithoutError()
    {
        var svc = CreateService();
        var cfg = new ArrInstanceConfig
        {
            Type = "sonarr",
            Search = new SearchConfig
            {
                UseTempForMissing = true,
                QualityProfileMappings = new Dictionary<string, string> { ["HD-1080p"] = "Any" }
            }
        };

        var act = async () => await svc.SwitchToTempProfilesAsync(
            "sonarr", cfg, Enumerable.Empty<SearchCandidate>());

        await act.Should().NotThrowAsync();
    }
}
