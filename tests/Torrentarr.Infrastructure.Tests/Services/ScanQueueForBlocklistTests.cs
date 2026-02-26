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
/// Tests for ArrSyncService.ScanQueueForBlocklistAsync (§1.7).
/// The method is private — invoked via reflection following the pattern in AvailabilityCheckTests.
/// Logic: skip if ArrErrorCodesToBlocklist is empty; skip items whose status != "warning"
/// or state != "importPending"; blocklist+delete any item whose messages contain a code match.
/// </summary>
public class ScanQueueForBlocklistTests
{
    // Alias for the tuple type the private method expects
    private static (int Id, string? DownloadId, string? TrackedDownloadStatus,
        string? TrackedDownloadState, List<StatusMessage>? StatusMessages)
        Item(int id, string? downloadId, string? status, string? state, List<StatusMessage>? msgs)
        => (id, downloadId, status, state, msgs);

    private static ArrSyncService CreateService()
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ArrSyncService(
            NullLogger<ArrSyncService>.Instance,
            new TorrentarrConfig(),
            new TorrentarrDbContext(options));
    }

    private static async Task InvokeScanAsync(
        ArrSyncService service,
        IEnumerable<(int Id, string? DownloadId, string? TrackedDownloadStatus,
            string? TrackedDownloadState, List<StatusMessage>? StatusMessages)> items,
        ArrInstanceConfig cfg,
        Func<int, CancellationToken, Task<bool>> deleteFromQueue)
    {
        var method = typeof(ArrSyncService).GetMethod(
            "ScanQueueForBlocklistAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        await (Task)method!.Invoke(service,
            new object?[] { items, cfg, deleteFromQueue, CancellationToken.None })!;
    }

    // ── Early exit: empty blocklist ───────────────────────────────────────────

    [Fact]
    public async Task Scan_EmptyBlocklist_NeverCallsDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = [] };
        var items = new[] { Item(1, "abc", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("empty blocklist means scan is skipped entirely");
    }

    // ── Status filter ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_StatusNotWarning_ItemSkipped()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(2, "h2", "ok", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("status 'ok' is not 'warning' — item is skipped");
    }

    // ── State filter ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_StateNotImportPending_ItemSkipped()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(3, "h3", "warning", "downloading",
            [new StatusMessage { Messages = ["Corrupt video file"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("state 'downloading' is not 'importPending' — item is skipped");
    }

    // ── Message matching ──────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_MessageContainsCode_DeleteCalled()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt video file"] };
        var items = new[] { Item(42, "hashX", "warning", "importPending",
            [new StatusMessage { Messages = ["Corrupt video file or severe data loss"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(42);
    }

    [Fact]
    public async Task Scan_MessageDoesNotContainCode_NoDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(5, "h5", "warning", "importPending",
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
        var items = new[] { Item(6, "h6", "warning", "importPending", null) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("null StatusMessages produces no messages to match");
    }

    [Fact]
    public async Task Scan_NullInnerMessages_NoDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };
        var items = new[] { Item(7, "h7", "warning", "importPending",
            [new StatusMessage { Messages = null }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().BeEmpty("null inner Messages list produces no messages to match");
    }

    // ── Case-insensitive matching ──────────────────────────────────────────────

    [Fact]
    public async Task Scan_CaseInsensitiveStatusAndCode_Matches()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["corrupt video"] };
        var items = new[] { Item(8, "h8", "WARNING", "IMPORTPENDING",
            [new StatusMessage { Messages = ["CORRUPT VIDEO file detected"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(8,
            "matching is case-insensitive for status, state, and error codes");
    }

    // ── Multiple items ────────────────────────────────────────────────────────

    [Fact]
    public async Task Scan_MultipleItems_OnlyMatchingOnesDeleted()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["Corrupt"] };

        var items = new[]
        {
            Item(10, "h10", "warning", "importPending",
                [new StatusMessage { Messages = ["Corrupt video"] }]),   // match
            Item(11, "h11", "warning", "downloading",
                [new StatusMessage { Messages = ["Corrupt video"] }]),   // wrong state
            Item(12, "h12", "ok",      "importPending",
                [new StatusMessage { Messages = ["Corrupt video"] }]),   // wrong status
            Item(13, "h13", "warning", "importPending",
                [new StatusMessage { Messages = ["No match here"] }]),   // no code match
        };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        deleted.Should().ContainSingle().Which.Should().Be(10,
            "only item 10 passes all filters and has a matching error code");
    }

    [Fact]
    public async Task Scan_MultipleMatchingCodes_FirstMatchTriggersDelete()
    {
        var svc = CreateService();
        var deleted = new List<int>();
        var cfg = new ArrInstanceConfig { ArrErrorCodesToBlocklist = ["CodeA", "CodeB"] };
        var items = new[] { Item(20, "h20", "warning", "importPending",
            [new StatusMessage { Messages = ["Contains CodeB here"] }]) };

        await InvokeScanAsync(svc, items, cfg, (id, _) => { deleted.Add(id); return Task.FromResult(true); });

        // CodeB matches even though CodeA doesn't — any matching code is sufficient
        deleted.Should().ContainSingle().Which.Should().Be(20);
    }
}
