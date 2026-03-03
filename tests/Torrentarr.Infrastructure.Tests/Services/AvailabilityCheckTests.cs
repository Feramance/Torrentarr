using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class AvailabilityCheckTests
{
    private static readonly NullLogger<ArrSyncService> Logger = NullLogger<ArrSyncService>.Instance;

    private static bool CallMinimumAvailabilityCheck(
        string? minimumAvailability,
        DateTime? inCinemas,
        DateTime? digitalRelease,
        DateTime? physicalRelease,
        int year,
        string title)
    {
        var method = typeof(ArrSyncService).GetMethod("MinimumAvailabilityCheck",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (bool)method!.Invoke(null, new object?[] { minimumAvailability, inCinemas, digitalRelease, physicalRelease, year, title, Logger })!;
    }

    private static ArrSyncService CreateService()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TorrentarrDbContext(options);
        return new ArrSyncService(Logger, new TorrentarrConfig(), dbContext);
    }

    private static bool CallCheckEpisodeAvailability(DateTime? airDateUtc, string episodeTitle)
    {
        var method = typeof(ArrSyncService).GetMethod("CheckEpisodeAvailability",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var service = CreateService();
        return (bool)method!.Invoke(service, new object?[] { airDateUtc, episodeTitle, Logger })!;
    }

    private static bool CallCheckAlbumAvailability(DateTime? releaseDate, string albumTitle)
    {
        var method = typeof(ArrSyncService).GetMethod("CheckAlbumAvailability",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var service = CreateService();
        return (bool)method!.Invoke(service, new object?[] { releaseDate, albumTitle, Logger })!;
    }

    [Theory]
    [InlineData("released")]
    [InlineData("inCinemas")]
    [InlineData("announced")]
    public void MinimumAvailability_YearZero_ReturnsFalse(string minAvail)
    {
        CallMinimumAvailabilityCheck(minAvail, null, null, null, 0, "Test Movie").Should().BeFalse();
    }

    [Theory]
    [InlineData("released")]
    [InlineData("inCinemas")]
    [InlineData("announced")]
    public void MinimumAvailability_YearInFuture_ReturnsFalse(string minAvail)
    {
        var futureYear = DateTime.UtcNow.Year + 1;
        CallMinimumAvailabilityCheck(minAvail, null, null, null, futureYear, "Test Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_YearOldMovie_ReturnsTrue()
    {
        var oldYear = DateTime.UtcNow.Year - 2;
        CallMinimumAvailabilityCheck("released", null, null, null, oldYear, "Old Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_NoDatesWithReleased_ReturnsTrue()
    {
        CallMinimumAvailabilityCheck("released", null, null, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_DigitalAndPhysicalBothPassedWithReleased_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("released", null, past, past, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_DigitalAndPhysicalFutureWithReleased_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("released", null, future, future, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_DigitalPassedWithReleased_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("released", null, past, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_DigitalFutureWithReleased_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("released", null, future, null, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_PhysicalPassedWithReleased_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("released", null, null, past, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_PhysicalFutureWithReleased_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("released", null, null, future, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_NoDatesWithInCinemas_ReturnsTrue()
    {
        CallMinimumAvailabilityCheck("inCinemas", null, null, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_InCinemasPassedWithInCinemas_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("inCinemas", past, null, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_InCinemasFutureWithInCinemas_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("inCinemas", future, null, null, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_NoInCinemas_DigitalPassedWithInCinemas_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("inCinemas", null, past, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_NoInCinemas_DigitalFutureWithInCinemas_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("inCinemas", null, future, null, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_NoInCinemas_PhysicalPassedWithInCinemas_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallMinimumAvailabilityCheck("inCinemas", null, null, past, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_NoInCinemas_PhysicalFutureWithInCinemas_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallMinimumAvailabilityCheck("inCinemas", null, null, future, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void MinimumAvailability_NoInCinemas_NoDatesWithInCinemas_ReturnsTrue()
    {
        CallMinimumAvailabilityCheck("inCinemas", null, null, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_Announced_ReturnsTrue()
    {
        CallMinimumAvailabilityCheck("announced", null, null, null, DateTime.UtcNow.Year, "Movie").Should().BeTrue();
    }

    [Fact]
    public void MinimumAvailability_DefaultCase_ReturnsFalse()
    {
        CallMinimumAvailabilityCheck("in theaters", null, null, null, DateTime.UtcNow.Year, "Movie").Should().BeFalse();
    }

    [Fact]
    public void CheckEpisodeAvailability_NoAirDate_ReturnsTrue()
    {
        CallCheckEpisodeAvailability(null, "S01E01").Should().BeTrue();
    }

    [Fact]
    public void CheckEpisodeAvailability_FutureAirDate_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallCheckEpisodeAvailability(future, "S01E01").Should().BeFalse();
    }

    [Fact]
    public void CheckEpisodeAvailability_PastAirDate_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallCheckEpisodeAvailability(past, "S01E01").Should().BeTrue();
    }

    [Fact]
    public void CheckAlbumAvailability_NoReleaseDate_ReturnsTrue()
    {
        CallCheckAlbumAvailability(null, "Album Name").Should().BeTrue();
    }

    [Fact]
    public void CheckAlbumAvailability_FutureReleaseDate_ReturnsFalse()
    {
        var future = DateTime.UtcNow.AddDays(30);
        CallCheckAlbumAvailability(future, "Album Name").Should().BeFalse();
    }

    [Fact]
    public void CheckAlbumAvailability_PastReleaseDate_ReturnsTrue()
    {
        var past = DateTime.UtcNow.AddDays(-30);
        CallCheckAlbumAvailability(past, "Album Name").Should().BeTrue();
    }
}
