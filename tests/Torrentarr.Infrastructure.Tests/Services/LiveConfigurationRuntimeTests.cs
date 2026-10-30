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
    public async Task Runtime_RetriesFailedClientActivationBeforeAcknowledgingSuccess()
    {
        var config = new TorrentarrConfig();
        config.TorrentClients["active"] = new() { Host = "fixture" };
        config.TorrentClients["disabled"] = new() { Host = "offline", Disabled = true };
        config.QBitInstances["placeholder"] = new();
        var reloader = new Mock<IConfigReloader>();
        reloader.SetupGet(r => r.Revision).Returns(2);
        reloader.Setup(r => r.WaitForChangeAsync(It.IsAny<long>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .Returns((long _, TimeSpan _, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct));
        var failed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reloader.Setup(r => r.Acknowledge("runtime", 2, It.IsAny<string>()))
            .Callback((string _, long _, string? error) =>
            {
                if (error == null) applied.TrySetResult();
                else failed.TrySetResult(error);
            });
        var clients = new Mock<ITorrentClientRegistry>();
        clients.SetupSequence(c => c.ReconcileAsync(It.IsAny<IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>>>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(0).ReturnsAsync(1);
        using var services = new ServiceCollection().BuildServiceProvider();
        using var runtime = new LiveConfigurationRuntime(config, reloader.Object, clients.Object, services,
            new LoggingLevelSwitch(), NullLogger<LiveConfigurationRuntime>.Instance);
        try
        {
            await runtime.StartAsync(default);
            var firstError = await failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            firstError.Should().Contain("revision 2").And.Contain("active");
            applied.Task.IsCompleted.Should().BeFalse();
            await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            clients.Verify(c => c.ReconcileAsync(It.IsAny<IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>>>(), true, It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally { await runtime.StopAsync(default); }
    }

    [Fact]
    public async Task Runtime_ReportsSafeErrorWhenReconciliationThrows()
    {
        var config = new TorrentarrConfig();
        var reloader = new Mock<IConfigReloader>();
        reloader.SetupGet(r => r.Revision).Returns(3);
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        reloader.Setup(r => r.Acknowledge("runtime", 3, It.IsAny<string>()))
            .Callback((string _, long _, string? error) =>
            {
                if (error != null) reported.TrySetResult(error);
            });
        var clients = new Mock<ITorrentClientRegistry>();
        clients.Setup(c => c.ReconcileAsync(It.IsAny<IEnumerable<KeyValuePair<string, TorrentClientInstanceConfig>>>(), true, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("http://internal.example/?apiKey=secret"));
        using var services = new ServiceCollection().BuildServiceProvider();
        using var runtime = new LiveConfigurationRuntime(config, reloader.Object, clients.Object, services,
            new LoggingLevelSwitch(), NullLogger<LiveConfigurationRuntime>.Instance);
        try
        {
            await runtime.StartAsync(default);
            var error = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            error.Should().Contain("revision 3").And.Contain("IOException");
            error.Should().NotContain("secret").And.NotContain("internal.example");
        }
        finally { await runtime.StopAsync(default); }
    }

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
