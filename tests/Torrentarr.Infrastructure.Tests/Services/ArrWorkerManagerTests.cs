using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrWorkerManagerTests
{
    private static ArrWorkerManager CreateManager()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new ArrWorkerManager(
            NullLogger<ArrWorkerManager>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TorrentarrConfig(),
            new ProcessStateManager(),
            new StubConnectivityService());
    }

    [Fact]
    public void ShouldRunSearch_ReturnsTrue_WhenIntervalElapsed()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 1 } };

        manager.ShouldRunSearch("Lidarr", cfg).Should().BeTrue();
    }

    [Fact]
    public void ShouldRunSearch_ReturnsFalse_WithinInterval()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 3600 } };

        manager.ShouldRunSearch("Lidarr", cfg).Should().BeTrue();
        manager.ShouldRunSearch("Lidarr", cfg).Should().BeFalse();
    }

    [Fact]
    public void ShouldRunSearch_UpdatesLastSearchTime_OnlyWhenReturningTrue()
    {
        var manager = CreateManager();
        var cfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 3600 } };

        manager.ShouldRunSearch("Sonarr", cfg).Should().BeTrue();
        manager.ShouldRunSearch("Sonarr", cfg).Should().BeFalse();

        var otherCfg = new ArrInstanceConfig { Search = new SearchConfig { SearchRequestsEvery = 1 } };
        manager.ShouldRunSearch("Radarr", otherCfg).Should().BeTrue();
    }

    private sealed class StubConnectivityService : IConnectivityService
    {
        public bool IsConnected => true;
        public DateTime? LastChecked => DateTime.UtcNow;
        public Task<bool> IsConnectedAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<bool> IsQBittorrentReachableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
