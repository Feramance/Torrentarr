using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Torrentarr.Core.Services;
using Xunit;
using Moq;

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

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task TorrentSubjectsUseClientScopedNamesAndCategoriesWithoutChangingIdentity(string prefix)
    {
        using var factory = new TorrentarrWebApplicationFactory(); using var client = factory.CreateClientWithApiToken();
        var cache = factory.Services.GetRequiredService<ITorrentCacheService>();
        cache.SetName("qBit-General", "abc123", "A Returners Magic S02E04");
        cache.SetCategory("qBit-General", "abc123", "sonarr-anime token=secret");
        cache.SetName("another-client", "abc123", "Wrong client torrent");
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new(
            "import.ownership", "automation", "Import owner is unresolved", "Import skipped", "Check category ownership",
            ClientInstance: "qBit-General", SubjectType: "torrent", SubjectId: "abc123"));
        var response = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        var group = response.GetProperty("items")[0];
        var example = group.GetProperty("examples")[0];
        example.GetProperty("subjectId").GetString().Should().Be("abc123");
        example.GetProperty("subjectName").GetString().Should().Be("A Returners Magic S02E04");
        example.GetProperty("subjectCategory").GetString().Should().Be("sonarr-anime token=[redacted]");
        var details = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues/" + group.GetProperty("id").GetInt64());
        details.GetProperty("items")[0].GetProperty("subjectName").GetString().Should().Be("A Returners Magic S02E04");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TorrentMetadataLookupSupportsExistingIssuesAndUnavailableClients(bool fail)
    {
        var torrentClient = new Moq.Mock<Torrentarr.Core.Interfaces.ITorrentClient>();
        if (fail) torrentClient.Setup(c => c.GetTorrentsAsync(null, null, Moq.It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("unavailable"));
        else torrentClient.Setup(c => c.GetTorrentsAsync(null, null, Moq.It.IsAny<CancellationToken>())).ReturnsAsync([
            new Torrentarr.Core.Models.TorrentInfo { Hash = "abc123", Name = "Anime episode", Category = "autobrr" }]);
        var registry = new Moq.Mock<Torrentarr.Core.Interfaces.ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient("qBit-General")).Returns(torrentClient.Object);
        using var source = new TorrentarrWebApplicationFactory();
        using var factory = source.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(registry.Object)));
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new(
            "import.ownership", "automation", "Import owner is unresolved", "Import skipped", "Check category ownership",
            ClientInstance: "qBit-General", SubjectType: "torrent", SubjectId: "abc123"));
        var response = await client.GetFromJsonAsync<JsonElement>("/web/issues");
        response.GetProperty("available").GetBoolean().Should().BeTrue();
        var example = response.GetProperty("items")[0].GetProperty("examples")[0];
        example.GetProperty("subjectName").GetString().Should().Be(fail ? null : "Anime episode");
        example.GetProperty("subjectCategory").GetString().Should().Be(fail ? null : "autobrr");
        torrentClient.Verify(c => c.GetTorrentsAsync(null, null, Moq.It.IsAny<CancellationToken>()), Moq.Times.Once);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ArrOnlyIssues_UseOnlyUnambiguousLibraryAssociation(int matches)
    {
        using var factory = new TorrentarrWebApplicationFactory();
        using var client = factory.CreateClient();
        var config = factory.Services.GetRequiredService<Torrentarr.Core.Configuration.TorrentarrConfig>();
        config.ArrInstances["Sonarr-Anime"] = new() { Category = "sonarr-anime" };
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Torrentarr.Infrastructure.Database.TorrentarrDbContext>();
            for (var n = 0; n < matches; n++)
                db.TorrentLibrary.Add(new() { Hash = "abc123", ArrInstance = "Sonarr-Anime", QbitInstance = "client-" + n });
            // Same hash belonging to another Arr must not affect the match.
            db.TorrentLibrary.Add(new() { Hash = "abc123", ArrInstance = "Other-Arr", QbitInstance = "wrong" });
            await db.SaveChangesAsync();
        }
        var cache = factory.Services.GetRequiredService<ITorrentCacheService>();
        cache.SetName("client-0", "ABC123", "Anime episode");
        cache.SetCategory("client-0", "ABC123", "sonarr-anime/season");
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new(
            "import.replacement", "automation", "Replacement failed", "Retrying", "Check logs",
            ArrInstance: "Sonarr-Anime", SubjectType: "torrent", SubjectId: "ABC123"));
        var response = await client.GetFromJsonAsync<JsonElement>("/web/issues");
        var group = response.GetProperty("items")[0];
        var example = group.GetProperty("examples")[0];
        example.GetProperty("subjectName").GetString().Should().Be(matches == 1 ? "Anime episode" : null);
        example.GetProperty("subjectCategory").GetString().Should().Be(matches == 1 ? "sonarr-anime/season" : "sonarr-anime");
        example.GetProperty("clientInstance").GetString().Should().BeEmpty();
        var history = await client.GetFromJsonAsync<JsonElement>("/web/issues/" + group.GetProperty("id").GetInt64());
        history.GetProperty("items")[0].GetProperty("subjectName").GetString().Should().Be(matches == 1 ? "Anime episode" : null);
        history.GetProperty("items")[0].GetProperty("subjectCategory").GetString().Should().Be(matches == 1 ? "sonarr-anime/season" : "sonarr-anime");
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
