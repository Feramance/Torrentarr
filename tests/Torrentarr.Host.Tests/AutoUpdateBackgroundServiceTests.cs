using FluentAssertions;
using Torrentarr.Host;
using Xunit;

namespace Torrentarr.Host.Tests;

/// <summary>
/// Pure unit tests for <see cref="AutoUpdateBackgroundService.MatchesCron"/>.
/// No I/O — all cases are table-driven.
/// </summary>
public class AutoUpdateBackgroundServiceTests
{
    // Reference date: Wednesday 2026-02-25 03:00 UTC
    private static readonly DateTime Wednesday3am = new(2026, 2, 25, 3, 0, 0, DateTimeKind.Utc);

    // ── Wildcard ──────────────────────────────────────────────────────────────

    [Fact]
    public void MatchesCron_AllWildcards_AlwaysTrue()
    {
        AutoUpdateBackgroundService.MatchesCron("* * * * *", Wednesday3am).Should().BeTrue();
    }

    // ── Exact field matching ──────────────────────────────────────────────────

    [Theory]
    [InlineData("0 3 * * 3")]  // Wednesday at 03:00
    [InlineData("0 3 25 * *")] // 25th of month at 03:00
    [InlineData("0 3 25 2 *")] // 25 Feb at 03:00
    [InlineData("0 3 * * *")]  // Every day at 03:00
    public void MatchesCron_ExactMatch_ReturnsTrue(string cron)
    {
        AutoUpdateBackgroundService.MatchesCron(cron, Wednesday3am).Should().BeTrue();
    }

    [Theory]
    [InlineData("1 3 * * *")]  // minute mismatch
    [InlineData("0 4 * * *")]  // hour mismatch
    [InlineData("0 3 26 * *")] // day mismatch
    [InlineData("0 3 * 3 *")]  // month mismatch
    [InlineData("0 3 * * 1")]  // day-of-week mismatch (1=Monday, we're Wednesday)
    public void MatchesCron_FieldMismatch_ReturnsFalse(string cron)
    {
        AutoUpdateBackgroundService.MatchesCron(cron, Wednesday3am).Should().BeFalse();
    }

    // ── Weekly schedule (most common auto-update use case) ────────────────────

    [Fact]
    public void MatchesCron_WeeklyOnSunday_TrueOnSunday()
    {
        // "0 3 * * 0" = Sunday at 03:00
        var sunday = new DateTime(2026, 3, 1, 3, 0, 0, DateTimeKind.Utc); // 2026-03-01 is a Sunday
        AutoUpdateBackgroundService.MatchesCron("0 3 * * 0", sunday).Should().BeTrue();
    }

    [Fact]
    public void MatchesCron_WeeklyOnSunday_FalseOnWednesday()
    {
        AutoUpdateBackgroundService.MatchesCron("0 3 * * 0", Wednesday3am).Should().BeFalse();
    }

    // ── Comma-separated lists ─────────────────────────────────────────────────

    [Theory]
    [InlineData("0,30 3 * * *", 0)]   // minute in list (0)
    [InlineData("0,30 3 * * *", 30)]  // minute in list (30)
    [InlineData("0 1,3,5 * * *", 3)]  // hour in list
    public void MatchesCron_CommaList_TrueWhenValueInList(string cron, int minute)
    {
        var dt = new DateTime(2026, 2, 25, 3, minute, 0, DateTimeKind.Utc);
        // For hour tests, adjust hour from minute param
        if (cron.StartsWith("0 1,3,5"))
            dt = new DateTime(2026, 2, 25, minute, 0, 0, DateTimeKind.Utc);
        AutoUpdateBackgroundService.MatchesCron(cron, dt).Should().BeTrue();
    }

    [Fact]
    public void MatchesCron_CommaList_FalseWhenValueNotInList()
    {
        var dt = new DateTime(2026, 2, 25, 3, 15, 0, DateTimeKind.Utc); // minute=15
        AutoUpdateBackgroundService.MatchesCron("0,30 3 * * *", dt).Should().BeFalse();
    }

    // ── Ranges ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0 0-5 * * *", 0)]  // start of range
    [InlineData("0 0-5 * * *", 3)]  // middle of range
    [InlineData("0 0-5 * * *", 5)]  // end of range
    public void MatchesCron_Range_TrueWhenValueInRange(string cron, int hour)
    {
        var dt = new DateTime(2026, 2, 25, hour, 0, 0, DateTimeKind.Utc);
        AutoUpdateBackgroundService.MatchesCron(cron, dt).Should().BeTrue();
    }

    [Fact]
    public void MatchesCron_Range_FalseWhenValueOutsideRange()
    {
        var dt = new DateTime(2026, 2, 25, 6, 0, 0, DateTimeKind.Utc); // hour=6
        AutoUpdateBackgroundService.MatchesCron("0 0-5 * * *", dt).Should().BeFalse();
    }

    // ── Edge cases ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]         // only 4 fields
    [InlineData("* * * * * *")]     // 6 fields
    [InlineData("invalid cron")]
    public void MatchesCron_InvalidCron_ReturnsFalse(string cron)
    {
        AutoUpdateBackgroundService.MatchesCron(cron, Wednesday3am).Should().BeFalse();
    }

    [Fact]
    public void MatchesCron_MidnightEveryDay_TrueAtMidnight()
    {
        var midnight = new DateTime(2026, 2, 25, 0, 0, 0, DateTimeKind.Utc);
        AutoUpdateBackgroundService.MatchesCron("0 0 * * *", midnight).Should().BeTrue();
    }

    [Fact]
    public void MatchesCron_MidnightEveryDay_FalseAtNoon()
    {
        var noon = new DateTime(2026, 2, 25, 12, 0, 0, DateTimeKind.Utc);
        AutoUpdateBackgroundService.MatchesCron("0 0 * * *", noon).Should().BeFalse();
    }
}
