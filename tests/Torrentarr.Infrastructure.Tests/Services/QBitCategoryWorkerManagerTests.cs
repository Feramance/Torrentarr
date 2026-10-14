using System.Collections.Concurrent;
using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class QBitCategoryWorkerManagerTests
{
    [Fact]
    public async Task FailedSuspension_RestartsStoppedWorkersAndWaitsForTimedOutWorkerToExit()
    {
        var config = new TorrentarrConfig();
        config.QBitInstances["qBit"] = new() { ManagedCategories = ["fast", "slow"] };
        using var services = new ServiceCollection().BuildServiceProvider();
        var connectivity = new Mock<IConnectivityService>();
        connectivity.Setup(c => c.IsConnectedAsync(It.IsAny<CancellationToken>())).Returns(async (CancellationToken ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        });
        using var manager = new QBitCategoryWorkerManager(NullLogger<QBitCategoryWorkerManager>.Instance,
            config, services.GetRequiredService<IServiceScopeFactory>(), new ProcessStateManager(), connectivity.Object);
        var workers = (ConcurrentDictionary<string, (Task Task, CancellationTokenSource Cts)>)typeof(QBitCategoryWorkerManager)
            .GetField("_workers", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastCts = new CancellationTokenSource();
        var slowCts = new CancellationTokenSource();
        workers["fast"] = (Task.CompletedTask, fastCts);
        workers["slow"] = (slow.Task, slowCts);
        try
        {
            await FluentActions.Awaiting(() => manager.SuspendAsync()).Should().ThrowAsync<TimeoutException>();
            workers["fast"].Cts.Should().NotBeSameAs(fastCts);
            workers["slow"].Task.Should().BeSameAs(slow.Task);
            slow.SetResult();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!workers.TryGetValue("slow", out var replacement) || replacement.Task == slow.Task)
                await Task.Delay(10, timeout.Token);
            workers["slow"].Cts.Should().NotBeSameAs(slowCts);
        }
        finally
        {
            slow.TrySetResult();
            await manager.SuspendAsync();
        }
    }
}
