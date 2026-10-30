using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Services;
using Xunit;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class OperationalIssueEndpointTests
{
    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task IssuesAreGroupedFilteredAndResolved(string prefix)
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        var reporter = factory.Services.GetRequiredService<IOperationalIssueReporter>();
        var issue = new OperationalIssueReport("test.profile", "automation", "Search skipped", "Original profile preserved.", "Check profiles.", ArrInstance: "Sonarr-TV", SubjectId: "1");
        await reporter.ReportAsync(issue); await reporter.ReportAsync(issue with { SubjectId = "2" });
        var response = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues?category=automation");
        response.GetProperty("available").GetBoolean().Should().BeTrue();
        response.GetProperty("total").GetInt32().Should().Be(1);
        var group = response.GetProperty("items")[0]; group.GetProperty("affected").GetInt32().Should().Be(2);
        (await client.GetAsync(prefix + "/issues/" + group.GetProperty("id").GetInt64())).EnsureSuccessStatusCode();
        await reporter.ResolveAsync(issue);
        var active = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        active.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(1);
        var history = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues?state=resolved");
        history.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(1);
        (await client.GetAsync(prefix + "/issues?state=invalid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var summary = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues/summary");
        summary.GetProperty("activeGroups").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task DatabaseOutageReturnsUnavailableAndPendingReports_NotHealthyEmptyHistory()
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        using (var scope = factory.Services.CreateScope())
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(scope.ServiceProvider.GetRequiredService<Torrentarr.Infrastructure.Database.TorrentarrDbContext>().Database, "DROP TABLE torrentarr_operational_issues");
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new("database.test", "system", "Database unavailable", "Search held back", "Check database"));
        var response = await client.GetFromJsonAsync<JsonElement>("/web/issues");
        response.GetProperty("available").GetBoolean().Should().BeFalse(); response.GetProperty("pending").GetArrayLength().Should().Be(1);
        var summary = await client.GetFromJsonAsync<JsonElement>("/web/issues/summary");
        summary.GetProperty("activeGroups").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ApiIssuesRequireToken()
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClient();
        (await client.GetAsync("/api/issues")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/issues/summary")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
