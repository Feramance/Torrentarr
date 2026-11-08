using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MigrationAndMissingMappings_CreateIssuesBeforeProcessing_AndResolveAfterRestart(bool removeClient)
    {
        using var root = new TemporaryDirectory();
        using var db = new Torrentarr.Infrastructure.Database.TorrentarrDbContext(new DbContextOptionsBuilder<Torrentarr.Infrastructure.Database.TorrentarrDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var config = new TorrentarrConfig();
        config.TorrentClients["recycling"] = new()
        {
            Host = "localhost",
            Migration = new() { Notices = ["Legacy monitor ownership is ambiguous; review FreeSpace."] },
            Maintenance = new()
            {
                RecycleBin = new() { Enabled = true },
                PathMappings = [new() { ClientPath = "/downloads", LocalPath = root.Path }]
            }
        };
        config.QBitInstances["qBit-other"] = new() { Host = "localhost" };
        await using var services = new ServiceCollection().AddSingleton(db).AddSingleton(config)
            .AddSingleton<IPathMappingService, PathMappingService>().BuildServiceProvider();
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, config);
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter, config, NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        await monitor.InspectAsync(default);
        var active = await db.OperationalIssues.AsNoTracking().Where(i => i.ResolvedAt == null).ToListAsync();
        active.Should().HaveCount(2).And.OnlyContain(i => i.Target == "config" && i.Occurrences == 2);
        active.Single(i => i.Code == "config.storage").ClientInstance.Should().Be("qBit-other");
        active.Single(i => i.Code == "config.storage").Remediation.Should().Contain("Maintenance.PathMappings");
        active.Single(i => i.Code == "config.migration").Remediation.Should().Contain("Migration.Notices");
        config.TorrentClients["recycling"].Migration.Notices.Clear();
        if (removeClient) config.QBitInstances.Clear();
        else config.QBitInstances["qBit-other"].Maintenance.PathMappings.Add(new() { ClientPath = root.Path, LocalPath = root.Path });
        using var restarted = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter, config, NullLogger<OperationalIssueMonitor>.Instance);
        await restarted.InspectAsync(default);
        var history = await db.OperationalIssues.AsNoTracking().ToListAsync();
        history.Should().HaveCount(2).And.OnlyContain(i => i.ResolvedAt != null);
    }

    [Fact]
    public async Task InvalidOrMissingConfiguration_DirectsUserToConfigAndResolvesAfterCorrection()
    {
        using var root = new TemporaryDirectory();
        var config = new TorrentarrConfig();
        config.TorrentClients["client"] = new()
        {
            TorrentScanIntervalSeconds = 7,
            Maintenance = new()
            {
                RecycleBin = new() { Enabled = true },
                PathMappings = [new() { ClientPath = "/downloads", LocalPath = System.IO.Path.Combine(root.Path, "missing-mount") }]
            }
        };
        var reports = new List<OperationalIssueReport>();
        var resolved = new List<OperationalIssueReport>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => reports.Add(r)).Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) => resolved.Add(r)).Returns(Task.CompletedTask);
        await using var services = new ServiceCollection().AddSingleton<IPathMappingService, PathMappingService>().BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object, config, NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reports.Select(r => r.Code).Should().BeEquivalentTo(["config.validation", "config.client", "config.storage"]);
        reports.Should().OnlyContain(r => r.Target == "config" && r.Remediation.Contains("Open Config"));
        reports.Single(r => r.Code == "config.validation").Impact.Should().Contain("TorrentScanIntervalSeconds");
        config.TorrentClients["client"].Host = "localhost";
        config.TorrentClients["client"].TorrentScanIntervalSeconds = 30;
        config.TorrentClients["client"].Maintenance.PathMappings[0].LocalPath = root.Path;
        await monitor.InspectAsync(default);
        resolved.Select(r => r.Code).Should().BeEquivalentTo(reports.Select(r => r.Code));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PersistedMaintenanceHistory_ResolvesLaterSuccessWithoutReplayingFailures(bool equalCompletion, bool previouslyReported)
    {
        await using var db = new Torrentarr.Infrastructure.Database.TorrentarrDbContext(new DbContextOptionsBuilder<Torrentarr.Infrastructure.Database.TorrentarrDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        await db.Database.OpenConnectionAsync();
        await db.Database.EnsureCreatedAsync();
        var completed = DateTimeOffset.UtcNow.AddMinutes(-2);
        if (previouslyReported)
        {
            db.OperationalIssues.Add(new() { Code = "maintenance.run", SubjectType = "maintenance", SubjectId = "[\"ONE\"]", LastObserved = DateTime.UtcNow, Occurrences = 1 });
            await db.SaveChangesAsync();
        }
        var history = new List<Torrentarr.Core.Models.MaintenanceRunSummary>
        {
            new() { RunId = "failure", ClientInstanceIds = ["one"], Failed = 1, Result = "failed", StartedAt = completed.AddMinutes(-2), CompletedAt = completed },
            new() { RunId = "success", ClientInstanceIds = ["one"], Result = "success", StartedAt = completed.AddMinutes(-1), CompletedAt = equalCompletion ? completed : completed.AddSeconds(1) }
        };
        var maintenance = new Mock<IMaintenanceCoordinator>();
        maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns(() => history);
        await using var services = new ServiceCollection().AddSingleton(db).AddSingleton(maintenance.Object).BuildServiceProvider();
        using var reporter = new OperationalIssueReporter(services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OperationalIssueReporter>.Instance, new());
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter, new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        await monitor.InspectAsync(default);
        using var restarted = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter, new(), NullLogger<OperationalIssueMonitor>.Instance);
        await restarted.InspectAsync(default);
        db.ChangeTracker.Clear();
        var issue = await db.OperationalIssues.SingleAsync();
        issue.ResolvedAt.Should().NotBeNull();
        issue.Occurrences.Should().Be(1);
        history.Add(new() { RunId = "new-failure", ClientInstanceIds = ["one"], Failed = 1, Result = "failed", CompletedAt = DateTimeOffset.UtcNow.AddSeconds(1) });
        await restarted.InspectAsync(default);
        db.ChangeTracker.Clear();
        issue = await db.OperationalIssues.SingleAsync();
        issue.ResolvedAt.Should().BeNull();
        issue.Occurrences.Should().Be(2);
    }

    [Fact]
    public async Task Restart_DoesNotReplayPersistedMaintenanceFailuresAsNewObservations()
    {
        await using var db = new Torrentarr.Infrastructure.Database.TorrentarrDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Torrentarr.Infrastructure.Database.TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var completed = DateTimeOffset.UtcNow.AddHours(-2);
        db.OperationalIssues.Add(new() { Code = "maintenance.run", SubjectType = "maintenance", SubjectId = "[\"ONE\"]", LastObserved = completed.UtcDateTime.AddMinutes(1) });
        await db.SaveChangesAsync();
        var reporter = new Mock<IOperationalIssueReporter>();
        var maintenance = new Mock<IMaintenanceCoordinator>();
        maintenance.Setup(m => m.GetHistory(It.IsAny<int>())).Returns([new() { RunId = "old-failure", ClientInstanceIds = ["one"], Failed = 3, Result = "partial", CompletedAt = completed }]);
        await using var services = new ServiceCollection().AddSingleton(db).AddSingleton(maintenance.Object).BuildServiceProvider();
        using var monitor = new OperationalIssueMonitor(services.GetRequiredService<IServiceScopeFactory>(), reporter.Object, new(), NullLogger<OperationalIssueMonitor>.Instance);
        await monitor.InspectAsync(default);
        reporter.Verify(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Theory]
    [InlineData("Running", 10, true)]
    [InlineData("Running", 31, false)]
    [InlineData("Error", 1, false)]
    [InlineData("Timed out", 1, false)]
    [InlineData("Initializing", 1, false)]
    [InlineData("Backoff", 1, false)]
    [InlineData("Waiting for connectivity", 1, false)]
    public void WorkerRecovery_UsesCurrentProgressWithoutWaitingForFirstSearchBatch(string phase, int ageMinutes, bool healthy)
    {
        var now = DateTimeOffset.UtcNow;
        var state = new Torrentarr.Core.Interfaces.ProcessStatus
        {
            IsAlive = true,
            LastHeartbeat = now.UtcDateTime,
            Torrent = new("Sleeping", now, now.AddHours(1), now),
            Search = new(phase, now.AddMinutes(-ageMinutes))
        };
        OperationalIssueMonitor.HasHealthyProgress(state, now).Should().Be(healthy);
        state.LastHeartbeat = now.AddSeconds(-16).UtcDateTime;
        OperationalIssueMonitor.HasHealthyProgress(state, now).Should().BeFalse();
    }

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
        var events = new List<(string Action, OperationalIssueReport Issue)>();
        var reporter = new Mock<IOperationalIssueReporter>();
        reporter.Setup(r => r.ReportAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) =>
            {
                reported.Add(r);
                events.Add(("report", r));
            })
            .Returns(Task.CompletedTask);
        reporter.Setup(r => r.ResolveAsync(It.IsAny<OperationalIssueReport>(), It.IsAny<CancellationToken>()))
            .Callback<OperationalIssueReport, CancellationToken>((r, _) =>
            {
                resolved.Add(r);
                events.Add(("resolve", r));
            })
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
        events.Where(e => e.Issue.Code == "maintenance.run" && e.Issue.SubjectType == "maintenance")
            .Select(e => e.Action).Should().Equal("report", "resolve");
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
        reports.Single(r => r.Code == "config.apply").Impact.Should().Contain("revision=");
        reports.Single(r => r.Code == "network.internet").Impact.Should().Contain("connectivity check=");
        reports.Single(r => r.Code == "storage.reserve").Impact.Should().Contain("/downloads").And.Contain("free=").And.Contain("reserve=");
        resolved.Should().BeEmpty();
        state = state with { State = "pending" }; await monitor.InspectAsync(default);
        resolved.Should().NotContain(r => r.Code == "config.apply");
        state = state with { State = "applied", Error = null }; online = true; low = false; await monitor.InspectAsync(default);
        resolved.Select(r => r.Code).Should().Contain(["config.apply", "network.internet", "storage.reserve"]);
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("migration-issues-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

}
