using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Torrentarr.Infrastructure.Endpoints;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class LogFileApiTests
{
    [Fact]
    public void Search_FindsMatchingLines()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-logs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "All.log"), "alpha\nerror: boom\nbeta\n");
            var result = LogFileApi.Search(dir, "All.log", "error", null, null, "0", 10, 0);
            ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Search_RequiresQuery()
    {
        var result = LogFileApi.Search("/tmp", "All.log", "", null, null, null, null, null);
        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(400);
    }

    [Fact]
    public void IsValidLogFileName_RejectsTraversal()
    {
        LogFileApi.IsValidLogFileName("../secret.log").Should().BeFalse();
        LogFileApi.IsValidLogFileName("All.log").Should().BeTrue();
    }
    [Fact]
    public async Task StructuredTail_SupportsHistoryDeltasAndTruncation()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-tail-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "All.log");
        try
        {
            await File.WriteAllTextAsync(path, "one\ntwo\nthree\nfour\n");
            var ctx = new DefaultHttpContext();
            ctx.Request.QueryString = new QueryString("?lines=2");
            var tail = await LogFileApi.ReadWindowAsync(path, ctx.Request, default);
            tail.content.Should().Be("three\nfour");
            ctx.Request.QueryString = new QueryString("?lines=2&offset=2");
            (await LogFileApi.ReadWindowAsync(path, ctx.Request, default)).content.Should().Be("one\ntwo");
            ctx.Request.QueryString = new QueryString($"?since_bytes={tail.next_bytes}&inode={tail.inode}");
            await File.AppendAllTextAsync(path, "caf");
            (await LogFileApi.ReadWindowAsync(path, ctx.Request, default)).content.Should().BeEmpty();
            await File.AppendAllTextAsync(path, "é\n");
            var delta = await LogFileApi.ReadWindowAsync(path, ctx.Request, default);
            delta.content.Should().Be("café\n");
            ctx.Request.QueryString = new QueryString($"?since_bytes={delta.next_bytes}&inode={delta.inode}");
            await File.WriteAllTextAsync(path, "reset\n");
            var rotated = await LogFileApi.ReadWindowAsync(path, ctx.Request, default);
            rotated.rotated.Should().BeTrue();
            rotated.content.Should().Be("reset");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task StructuredTail_RetriesIncompleteInitialLine()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "ready\ncaf");
            var ctx = new DefaultHttpContext();
            var initial = await LogFileApi.ReadWindowAsync(path, ctx.Request, default);
            initial.content.Should().Be("ready");
            ctx.Request.QueryString = new QueryString($"?since_bytes={initial.next_bytes}&inode={initial.inode}");
            await File.AppendAllTextAsync(path, "é\n");
            (await LogFileApi.ReadWindowAsync(path, ctx.Request, default)).content.Should().Be("café\n");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task StructuredTail_ReadsAroundSearchMatchAndHandlesEmptyFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"torrentarr-around-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "All.log");
        try
        {
            await File.WriteAllLinesAsync(path, Enumerable.Range(1, 100).Select(i => $"line {i}"));
            var ctx = new DefaultHttpContext();
            ctx.Request.QueryString = new QueryString("?around_line=50&lines=4");
            (await LogFileApi.ReadWindowAsync(path, ctx.Request, default)).content.Should().Be("line 48\nline 49\nline 50\nline 51");
            await File.WriteAllTextAsync(path, "");
            ctx.Request.QueryString = new QueryString("?since_bytes=0");
            (await LogFileApi.ReadWindowAsync(path, ctx.Request, default)).content.Should().BeEmpty();
        }
        finally { Directory.Delete(dir, true); }
    }
}
