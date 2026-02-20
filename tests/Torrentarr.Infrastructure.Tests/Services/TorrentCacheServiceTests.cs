using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class TorrentCacheServiceTests
{
    private TorrentCacheService CreateService() =>
        new TorrentCacheService(NullLogger<TorrentCacheService>.Instance);

    [Fact]
    public void SetCategory_GetCategory_RoundTrip()
    {
        var svc = CreateService();
        svc.SetCategory("abc123", "radarr");

        svc.GetCategory("abc123").Should().Be("radarr");
    }

    [Fact]
    public void SetName_GetName_RoundTrip()
    {
        var svc = CreateService();
        svc.SetName("abc123", "My Movie (2023)");

        svc.GetName("abc123").Should().Be("My Movie (2023)");
    }

    [Fact]
    public void GetCategory_ReturnsNull_ForUnknownHash()
    {
        var svc = CreateService();

        svc.GetCategory("unknown-hash").Should().BeNull();
    }

    [Fact]
    public void GetName_ReturnsNull_ForUnknownHash()
    {
        var svc = CreateService();

        svc.GetName("unknown-hash").Should().BeNull();
    }

    [Fact]
    public void IsInIgnoreCache_ReturnsFalse_ForUnknownHash()
    {
        var svc = CreateService();

        svc.IsInIgnoreCache("nope").Should().BeFalse();
    }

    [Fact]
    public void AddToIgnoreCache_WithZeroDuration_ExpiresImmediately()
    {
        var svc = CreateService();
        svc.AddToIgnoreCache("abc123", TimeSpan.Zero);

        // Zero duration means it expires at or before now
        svc.IsInIgnoreCache("abc123").Should().BeFalse();
    }

    [Fact]
    public void AddToIgnoreCache_WithLongDuration_PresentBeforeExpiry()
    {
        var svc = CreateService();
        svc.AddToIgnoreCache("abc123", TimeSpan.FromHours(1));

        svc.IsInIgnoreCache("abc123").Should().BeTrue();
    }

    [Fact]
    public void RemoveFromIgnoreCache_RemovesEntry()
    {
        var svc = CreateService();
        svc.AddToIgnoreCache("abc123", TimeSpan.FromHours(1));
        svc.RemoveFromIgnoreCache("abc123");

        svc.IsInIgnoreCache("abc123").Should().BeFalse();
    }

    [Fact]
    public void CleanExpired_RemovesOnlyExpiredEntries()
    {
        var svc = CreateService();
        // Add one that expires immediately and one that doesn't
        svc.AddToIgnoreCache("expired", TimeSpan.Zero);
        svc.AddToIgnoreCache("alive", TimeSpan.FromHours(1));

        svc.CleanExpired();

        svc.IsInIgnoreCache("expired").Should().BeFalse();
        svc.IsInIgnoreCache("alive").Should().BeTrue();
    }

    [Fact]
    public void Clear_WipesAllCaches()
    {
        var svc = CreateService();
        svc.SetCategory("h1", "radarr");
        svc.SetName("h1", "Movie");
        svc.AddToIgnoreCache("h1", TimeSpan.FromHours(1));

        svc.Clear();

        svc.GetCategory("h1").Should().BeNull();
        svc.GetName("h1").Should().BeNull();
        svc.IsInIgnoreCache("h1").Should().BeFalse();
    }

    [Fact]
    public void GetStats_ReturnsCorrectCounts()
    {
        var svc = CreateService();
        svc.SetCategory("h1", "radarr");
        svc.SetCategory("h2", "sonarr");
        svc.SetName("h1", "Movie 1");
        svc.AddToIgnoreCache("h3", TimeSpan.FromHours(1));

        var stats = svc.GetStats();

        stats.CategoryCacheSize.Should().Be(2);
        stats.NameCacheSize.Should().Be(1);
        stats.IgnoreCacheSize.Should().Be(1);
    }

    [Fact]
    public async Task ThreadSafety_ConcurrentSets_DoNotThrow()
    {
        var svc = CreateService();

        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(() =>
        {
            svc.SetCategory($"hash-{i}", $"cat-{i}");
            svc.SetName($"hash-{i}", $"name-{i}");
        }));

        await Task.WhenAll(tasks);

        // No exceptions thrown — verify at least some entries exist
        var stats = svc.GetStats();
        stats.CategoryCacheSize.Should().BeGreaterThan(0);
    }
}
