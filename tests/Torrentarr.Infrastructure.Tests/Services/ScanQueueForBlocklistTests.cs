using Moq;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Torrentarr.Core.Configuration;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

/// <summary>
/// Tests for ArrSyncService.ScanQueueForBlocklistAsync.
/// qBitrr: status == completed, trackedDownloadStatus == warning, trackedDownloadState == importPending,
/// and Arr error messages must match the blocklist exactly (case-sensitive).
/// </summary>
public class ScanQueueForBlocklistTests
{
    private static (int Id, string? DownloadId, string? Status, string? TrackedDownloadStatus,
        string? TrackedDownloadState, string? OutputPath, List<StatusMessage>? StatusMessages)
        Item(int id, string? downloadId, string? status, string? trackedStatus, string? state,
            List<StatusMessage>? msgs, string? outputPath = null)
        => (id, downloadId, status, trackedStatus, state, outputPath, msgs);

    private static ArrSyncService CreateService(bool allowed = true, bool ambiguous = false)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var config = new TorrentarrConfig();
        config.TorrentClients["one"] = new() { Host = "fixture", ManagedCategories = ["movies"] };
        if (ambiguous) config.TorrentClients["two"] = new() { Host = "fixture", ManagedCategories = ["movies"] };
        config.ArrInstances["Radarr"] = new() { Category = "movies" };
        var registry = new Mock<ITorrentClientRegistry>();
        var client = new Mock<ITorrentClient>();
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new[] { "hashX", "h8", "h10", "h20" }.Select(h => new TorrentInfo { Hash = h, Category = "movies" }).ToList());
        registry.Setup(r => r.GetClient(It.IsAny<string>())).Returns(client.Object);
        var deletion = new Mock<ISafeDeletionService>();
        deletion.Setup(d => d.RemoveTorrentAsync(It.IsAny<string>(), It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SafeDeletionResult(allowed, true, 0, allowed ? null : "HnR protected"));
        return new ArrSyncService(NullLogger<ArrSyncService>.Instance, config,
            new TorrentarrDbContext(options), new DatabaseRestartCoordinator(), registry.Object, deletion.Object);
    }

    private static async Task InvokeScanAsync(
        ArrSyncService service,
        IEnumerable<(int Id, string? DownloadId, string? Status, string? TrackedDownloadStatus,
            string? TrackedDownloadState, string? OutputPath, List<StatusMessage>? StatusMessages)> items,
        ArrInstanceConfig cfg,
        Func<int, CancellationToken, Task<bool>> deleteFromQueue)
    {
        cfg.Category = "movies";
        var method = typeof(ArrSyncService).GetMethod(
            "ScanQueueForBlocklistAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(service,
            new object?[] { items, cfg, deleteFromQueue, CancellationToken.None })!;
    }

    [Fact]
    public async Task Scan_EmptyBlocklist_NeverCallsDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = [] };
        var items = new[] { Item(1, "abc", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("empty blocklist means scan is skipped entirely");
    }

    [Fact]
    public async Task Scan_StatusNotCompleted_ItemSkipped()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(2, "h2", "downloading", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("status must be completed");
    }

    [Fact]
    public async Task Scan_TrackedStatusNotWarning_ItemSkipped()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(2, "h2", "completed", "ok", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("trackedDownloadStatus must be warning");
    }

    [Fact]
    public async Task Scan_StateNotImportPending_ItemSkipped()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(3, "h3", "completed", "warning", "downloading",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("state 'downloading' is not 'importPending'");
    }

    [Fact]
    public async Task Scan_ExactMessageMatch_DeleteCalled()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(42, "hashX", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(42);
    }

    [Fact]
    public async Task Scan_SubstringMessage_DoesNotMatch()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(42, "hashX", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file or severe data loss"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("qBitrr matches error messages exactly, not as substrings");
    }

    [Fact]
    public async Task Scan_MessageDoesNotContainCode_NoDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(5, "h5", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["No suitable files were found"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("message doesn't contain any blocklist code");
    }

    [Fact]
    public async Task Scan_NullStatusMessages_NoDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(6, "h6", "completed", "warning", "importPending", null) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("null StatusMessages produces no messages to match");
    }

    [Fact]
    public async Task Scan_NullInnerMessages_NoDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(7, "h7", "completed", "warning", "importPending",
            [new StatusMessage { Messages = null }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("null inner Messages list produces no messages to match");
    }

    [Fact]
    public async Task Scan_StatusFields_AreCaseInsensitive_CodesAreExact()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(8, "h8", "COMPLETED", "WARNING", "IMPORTPENDING",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(8);
    }

    [Fact]
    public async Task Scan_CodeMatch_IsCaseSensitive()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(8, "h8", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("error-code membership is case-sensitive");
    }

    [Fact]
    public async Task Scan_MultipleItems_OnlyMatchingOnesDeleted()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };

        var items = new[]
        {
            Item(10, "h10", "completed", "warning", "importPending",
                [new StatusMessage { Messages = ["Corrupt"] }]),
            Item(11, "h11", "completed", "warning", "downloading",
                [new StatusMessage { Messages = ["Corrupt"] }]),
            Item(12, "h12", "ok", "warning", "importPending",
                [new StatusMessage { Messages = ["Corrupt"] }]),
            Item(13, "h13", "completed", "warning", "importPending",
                [new StatusMessage { Messages = ["No match here"] }]),
        };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(10);
    }

    [Fact]
    public async Task Scan_MultipleMatchingCodes_FirstMatchTriggersDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["CodeA", "CodeB"] };
        var items = new[] { Item(20, "h20", "completed", "warning", "importPending",
            [new StatusMessage { Messages = ["CodeB"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(20);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task ProtectedOrAmbiguousCleanupPreservesQueueAndFiles(bool allowed, bool ambiguous)
    {
        var service = CreateService(allowed, ambiguous);
        var path = Path.GetTempFileName();
        try
        {
            var deleted = new List<int>();
            await InvokeScanAsync(service, [Item(1, "hashX", "completed", "warning", "importPending",
                [new StatusMessage { Title = Path.GetFileName(path), Messages = ["Corrupt"] }], Path.GetDirectoryName(path))],
                new() { ArrErrorCodesToBlocklist = ["Corrupt"] }, (id, _) => { deleted.Add(id); return Task.FromResult(true); });
            deleted.Should().BeEmpty();
            File.Exists(path).Should().BeTrue();
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ResolveBlocklistedCleanupTarget_RejectsRootedTitle()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"torrentarr-bl-{Guid.NewGuid():N}"));
        var outside = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"torrentarr-out-{Guid.NewGuid():N}.txt"));

        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, outside).Should().BeNull();
        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, Path.DirectorySeparatorChar + "etc" + Path.DirectorySeparatorChar + "passwd")
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBlocklistedCleanupTarget_RejectsParentTraversal()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"torrentarr-bl-{Guid.NewGuid():N}"));
        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, ".." + Path.DirectorySeparatorChar + "passwd")
            .Should().BeNull();
        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, Path.Combine("nested", "..", "..", "passwd"))
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBlocklistedCleanupTarget_AllowsRelativeChild()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"torrentarr-bl-{Guid.NewGuid():N}"));
        var expected = Path.GetFullPath(Path.Combine(dir, "folder", "bad.mkv"));
        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, Path.Combine("folder", "bad.mkv"))
            .Should().Be(expected);
    }

    [Fact]
    public void ResolveBlocklistedCleanupTarget_RejectsFilesystemRootOutputPath()
    {
        var volumeRoot = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
        volumeRoot.Should().NotBeNullOrEmpty();
        ArrSyncService.ResolveBlocklistedCleanupTarget(volumeRoot, "etc").Should().BeNull();
        ArrSyncService.ResolveBlocklistedCleanupTarget(Path.DirectorySeparatorChar.ToString(), "etc")
            .Should().BeNull();
    }

    [Fact]
    public void ResolveBlocklistedCleanupTarget_RejectsInvalidPathCharacters()
    {
        var dir = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"torrentarr-bl-{Guid.NewGuid():N}"));
        ArrSyncService.ResolveBlocklistedCleanupTarget(dir, "bad\0name").Should().BeNull();

        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            if (ch is '/' or '\\' or '\0')
                continue;

            ArrSyncService.ResolveBlocklistedCleanupTarget(dir, "file" + ch + "name").Should().BeNull();
        }

        if (OperatingSystem.IsWindows())
        {
            ArrSyncService.ResolveBlocklistedCleanupTarget(dir, "Who?").Should().BeNull();
        }
    }
}
