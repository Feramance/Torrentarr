using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog.Core;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Services;
using FluentAssertions;
using Serilog.Events;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LiveConfigurationRuntimeTests
{
    [Fact]
    public async Task Runtime_ReconcilesCategoryWorkersBeforeAcknowledgingEachRevision()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { ManagedCategories = ["old"] };
        var states = new ProcessStateManager();
        using var scopes = new ServiceCollection().BuildServiceProvider();
        var connectivity = new Mock<IConnectivityService>();
        connectivity.Setup(c => c.IsConnectedAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        });
        using var manager = new QBitCategoryWorkerManager(NullLogger<QBitCategoryWorkerManager>.Instance,
            config, scopes.GetRequiredService<IServiceScopeFactory>(), states, connectivity.Object);
        var workers = (System.Collections.Concurrent.ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)>)typeof(QBitCategoryWorkerManager)
            .GetField("_workers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        using var services = new ServiceCollection().AddSingleton(manager).BuildServiceProvider();
        var reloader = new Mock<IConfigReloader>();
        long revision = 1;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloader.SetupGet(r => r.Revision).Returns(() => Volatile.Read(ref revision));
        reloader.Setup(r => r.WaitForChangeAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((long _, TimeSpan _, CancellationToken ct) => Task.Delay(10, ct));
        reloader.Setup(r => r.Acknowledge("runtime", It.IsAny<long>(), null)).Callback((string _, long applied, string? _) =>
        {
            if (applied == 1) { workers.Should().ContainKey("old"); first.SetResult(); }
            else { workers.Should().ContainKey("new").And.NotContainKey("old"); second.SetResult(); }
        });
        var clients = new Mock<ITorrentClientRegistry>();
        clients.Setup(c => c.ReconcileAsync(It.IsAny<IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>>>(), true, It.IsAny<CancellationToken>())).ReturnsAsync(0);
        using var runtime = new LiveConfigurationRuntime(config, reloader.Object, clients.Object, services,
            new LoggingLevelSwitch(), NullLogger<LiveConfigurationRuntime>.Instance);
        try
        {
            await runtime.StartAsync(default);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
            config.TorrentClients["one"].ManagedCategories = ["new"];
            Volatile.Write(ref revision, 2);
            await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await runtime.StopAsync(default); await manager.SuspendAsync(); }
    }

    [Theory]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("DEBUG", LogEventLevel.Debug)]
    [InlineData("WARN", LogEventLevel.Warning)]
    [InlineData("ERROR", LogEventLevel.Error)]
    [InlineData("CRITICAL", LogEventLevel.Fatal)]
    [InlineData(null, LogEventLevel.Information)]
    [InlineData("unknown", LogEventLevel.Information)]
    public void ParseLevel_MapsConfiguredLevels(string? value, LogEventLevel expected)
    {
        LiveConfigurationRuntime.ParseLevel(value).Should().Be(expected);
    }
}
