using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Database.Models;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class WorkerHistoryEndpointTests(TorrentarrWebApplicationFactory factory) : IClassFixture<TorrentarrWebApplicationFactory>
{
    [Theory]
    [InlineData("/web/history")]
    [InlineData("/api/history")]
    public async Task HistoryFiltersAndPagesPersistedWorkerEvents(string endpoint)
    {
        var worker = "History-" + Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
            db.WorkerHistory.AddRange(
                new WorkerHistoryEvent { Instance = worker, Type = "search", Summary = "Older", Timestamp = DateTimeOffset.UtcNow },
                new WorkerHistoryEvent { Instance = worker, Type = "search", Summary = "Newer", Timestamp = DateTimeOffset.UtcNow },
                new WorkerHistoryEvent { Instance = worker, Type = "import", Summary = "Import", Timestamp = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        using var client = factory.CreateClientWithApiToken();
        var response = await client.GetAsync($"{endpoint}?worker={worker}&type=search&pageSize=1");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        page.GetProperty("entries").GetArrayLength().Should().Be(1);
        page.GetProperty("entries")[0].GetProperty("summary").GetString().Should().Be("Newer");
        page.GetProperty("workers").EnumerateArray().Select(e => e.GetString()).Should().Contain(worker);
        page.GetProperty("entries")[0].GetProperty("logStream").GetString().Should().StartWith("worker-History-");
        var next = page.GetProperty("nextCursor").GetInt64();
        var older = await client.GetAsync($"{endpoint}?worker={worker}&type=search&pageSize=1&cursor={next}");
        var olderPage = JsonDocument.Parse(await older.Content.ReadAsStringAsync()).RootElement;
        olderPage.GetProperty("entries")[0].GetProperty("summary").GetString().Should().Be("Older");
        olderPage.GetProperty("nextCursor").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("type=not-a-type")]
    [InlineData("cursor=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task HistoryRejectsInvalidQueryParameters(string query)
    {
        using var client = factory.CreateClientWithApiToken();
        (await client.GetAsync("/web/history?" + query)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task HistoryRequiresAuthentication()
    {
        using var client = factory.CreateClient();
        (await client.GetAsync("/api/history")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
