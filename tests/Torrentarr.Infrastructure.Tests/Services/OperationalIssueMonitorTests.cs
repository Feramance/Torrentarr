using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public sealed class OperationalIssueMonitorTests
{
    [Fact]
    public async Task CompletedMaintenanceRuns_AreNotReplayedAcrossInspections()
    {
        var reports = new List<OperationalIssueReport>();
        var resolutions = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => reports.Add(r)).Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => resolutions.Add(r)).Returns(Task.CompletedTask);
        var finished = DateTimeOffset.UtcNow;
        var history = new List<Torrentarr.Core.Models.MaintenanceRunSummary>
        {
            new() { RunId = "failed-first", ClientInstanceIds = ["one"], Failed = 1, Result = "failed", CompletedAt = finished },
            new() { RunId = "success-second", ClientInstanceIds = ["one"], Result = "success", CompletedAt = finished.AddSeconds(1) }
        };
        var maintenance = new Mock<IMaintenanceCoordinator>();
        maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns(() => history);
        await using var services = new ServiceCollection().AddSingleton(maintenance.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object,
            new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reports.Should().ContainSingle(r => r.Code == "maintenance.run");
        resolutions.Should().Contain(r => r.Code == "maintenance.run" && r.SubjectType == "maintenance");
        var previousResolutions = resolutions.Count;
        await monitor.InspectAsync(default);
        reports.Should().HaveCount(1);
        resolutions.Should().HaveCount(previousResolutions);
    }

    [Fact]
    public async Task EqualCompletionTime_UsesStartedOrderSoSuccessResolvesEarlierFailure()
    {
        var reported = new List<OperationalIssueReport>();
        var resolved = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => reported.Add(r))
            .Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => resolved.Add(r))
            .Returns(Task.CompletedTask);
        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        var completed = started.AddMinutes(5);
        // GetHistory returns newest first. The monitor must process the failure first.
        var history = new List<Torrentarr.Core.Models.MaintenanceRunSummary>
        {
            new() { RunId = "success-second", ClientInstanceIds = ["same"], Result = "success",
                StartedAt = started.AddMinutes(1), CompletedAt = completed },
            new() { RunId = "failure-first", ClientInstanceIds = ["same"], Result = "failed",
                Failed = 1, StartedAt = started, CompletedAt = completed }
        };
        var maintenance = new Mock<IMaintenanceCoordinator>();
        maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns(() => history);
        await using var services = new ServiceCollection().AddSingleton(maintenance.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(),
            reporter.Object, new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reported.Should().ContainSingle(r => r.Code == "maintenance.run");
        resolved.Should().Contain(r => r.Code == "maintenance.run" && r.SubjectType == "maintenance");
        var resolutionCount = resolved.Count;
        await monitor.InspectAsync(default);
        reported.Should().HaveCount(1);
        resolved.Should().HaveCount(resolutionCount);
    }

    [Fact]
    public async Task RunsSharingCompletionTimestamp_AreProcessedOnceEach()
    {
        var reported = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => reported.Add(r)).Returns(Task.CompletedTask);
        var when = DateTimeOffset.UtcNow;
        var history = new List<Torrentarr.Core.Models.MaintenanceRunSummary>
        {
            new() { RunId = "one", ClientInstanceIds = ["same"], Failed = 1, Result = "failed", CompletedAt = when },
            new() { RunId = "two", ClientInstanceIds = ["same"], Failed = 1, Result = "failed", CompletedAt = when }
        };
        var maintenance = new Mock<IMaintenanceCoordinator>();
        maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns(() => history);
        await using var services = new ServiceCollection().AddSingleton(maintenance.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object,
            new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        await monitor.InspectAsync(default);
        reported.Should().HaveCount(2);
    }

    [Fact]
    public async Task MultiClientMaintenanceFailureIsReportedOnce_AndOnlySameScopeSuccessClearsIt()
    {
        var reported = new List<OperationalIssueReport>(); var resolved = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => reported.Add(r)).Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => resolved.Add(r)).Returns(Task.CompletedTask);
        var history = new List<Torrentarr.Core.Models.MaintenanceRunSummary>
        {
            new() { ClientInstanceIds = ["healthy", "failed"], Failed = 1, Applied = 1, Result = "partial", CompletedAt = DateTimeOffset.UtcNow }
        };
        var maintenance = new Mock<IMaintenanceCoordinator>(); maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns(() => history);
        await using var services = new ServiceCollection().AddSingleton(maintenance.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object, new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reported.Should().ContainSingle(); reported[0].ClientInstance.Should().BeEmpty();
        reported[0].SubjectType.Should().Be("maintenance");
        await monitor.InspectAsync(default); reported.Should().ContainSingle();
        history.Add(new() { ClientInstanceIds = ["healthy"], Result = "success", CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        await monitor.InspectAsync(default);
        resolved.Should().NotContain(r => r.SubjectId == reported[0].SubjectId);
        history.Add(new() { ClientInstanceIds = ["failed", "healthy"], Result = "partial", Skipped = 1, CompletedAt = DateTimeOffset.UtcNow.AddSeconds(2) });
        await monitor.InspectAsync(default);
        resolved.Should().NotContain(r => r.SubjectId == reported[0].SubjectId);
        history.Add(new() { ClientInstanceIds = ["failed", "healthy"], Result = "success", CompletedAt = DateTimeOffset.UtcNow.AddSeconds(3) });
        await monitor.InspectAsync(default);
        resolved.Should().Contain(r => r.SubjectId == reported[0].SubjectId);
        resolved.Should().Contain(r => r.ClientInstance == "failed" && r.SubjectType == "service" && r.SubjectId == "");
    }

    [Fact]
    public async Task PendingConfigurationNeverClearsAnError_AndRecoveryRequiresSuccessfulObservation()
    {
        var reports = new List<OperationalIssueReport>(); var resolved = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => reports.Add(r)).Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>())).Callback<OperationalIssueReport, CancellationToken>((r, _) => resolved.Add(r)).Returns(Task.CompletedTask);
        var state = new ConfigurationApplicationStatus(1, "error", new(), "Invalid saved configuration");
        var config = new Mock<IConfigReloader>(); config.Setup(c => c.GetStatus()).Returns(() => state);
        var online = false;
        var network = new Mock<IConnectivityService>(); network.SetupGet(n => n.LastChecked).Returns(DateTime.UtcNow); network.SetupGet(n => n.IsConnected).Returns(() => online);
        var low = true;
        var storage = new Mock<IFreeSpaceService>(); storage.Setup(s => s.GetFreeSpaceStatsAsync(It.IsAny<CancellationToken>())).Returns(() => Task.FromResult<IReadOnlyList<FreeSpaceStats>>([new() { ClientInstanceId = "client", Path = "/downloads", BelowThreshold = low }]));
        await using var services = new ServiceCollection().AddSingleton(config.Object).AddSingleton(network.Object).AddSingleton(storage.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object, new TorrentarrConfig(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reports.Select(r => r.Code).Should().Contain(["config.apply", "network.internet", "storage.reserve"]);
        resolved.Should().BeEmpty();
        state = state with { State = "pending" }; await monitor.InspectAsync(default);
        resolved.Should().NotContain(r => r.Code == "config.apply");
        state = state with { State = "applied", Error = null }; online = true; low = false; await monitor.InspectAsync(default);
        resolved.Select(r => r.Code).Should().Contain(["config.apply", "network.internet", "storage.reserve"]);
    }
}
