using System.Net;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Xunit;
using Moq;

namespace Torrentarr.Host.Tests.Api;

[Collection("HostWeb")]
public sealed class OperationalIssueEndpointTests
{
    private sealed class IssueClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds = 30) => _now = _now.AddSeconds(seconds);
    }

    private sealed class IssueFactory : TorrentarrWebApplicationFactory
    {
        public IssueClock Clock { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureServices(services => services.AddSingleton<TimeProvider>(Clock));
        }
    }

    [Theory]
    [InlineData(TorrentRemovalIntent.RejectedByAllowlist, false)]
    [InlineData(TorrentRemovalIntent.StalledDownload, false)]
    [InlineData(TorrentRemovalIntent.StalledDownload, true)]
    public async Task RemovalWarningsStayActiveUntilRecoveryIsVerified(TorrentRemovalIntent intent, bool removeTorrent)
    {
        using var factory = new IssueFactory();
        using var http = factory.CreateClient();
        const string instance = "qBit-General";
        const string category = "sonarr-tv";
        var allowlist = intent == TorrentRemovalIntent.RejectedByAllowlist;
        var config = factory.Services.GetRequiredService<TorrentarrConfig>();
        config.Settings.Tagless = true;
        config.QBitInstances[instance] = new();
        config.ArrInstances["Sonarr-TV"] = new()
        {
            Category = category,
            Torrent = { FileExtensionAllowlist = [@"\.mkv$"], StalledDelay = allowlist ? -1 : 0 }
        };
        var blockedState = allowlist ? "downloading" : "stalledDL";
        var torrent = new TorrentInfo
        {
            Hash = "blocked-removal",
            Category = category,
            ClientInstanceId = instance,
            State = blockedState,
            AddedOn = 1,
            LastActivity = 1,
            Progress = 0.5,
            AmountLeft = 100
        };
        List<TorrentInfo> inventory = [torrent];
        List<TorrentFileRecord> files = [new() { Name = "episode.txt" }];
        var client = new Mock<ITorrentClient>();
        client.SetupGet(c => c.Capabilities).Returns(TorrentClientCapabilities.QBittorrent);
        client.Setup(c => c.GetTorrentsAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => inventory);
        client.Setup(c => c.GetTorrentFilesAsync(torrent.Hash, It.IsAny<CancellationToken>())).ReturnsAsync(() => files);
        var registry = new Mock<ITorrentClientRegistry>();
        registry.Setup(r => r.GetClient(instance)).Returns(client.Object);
        registry.Setup(r => r.GetAllClients()).Returns(new Dictionary<string, ITorrentClient> { [instance] = client.Object });
        var seeding = new Mock<ISeedingService>();
        seeding.Setup(s => s.HnrAllowsDeleteAsync(torrent, It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        seeding.Setup(s => s.EvaluateRemovalAsync(torrent, intent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemovalDecision([new("test.blocker", "Original blocker evidence", "Retry after recovery")]));
        var failScan = false;
        seeding.Setup(s => s.ApplySeedingLimitsAsync(torrent, It.IsAny<CancellationToken>()))
            .Returns(() => failScan ? Task.FromException(new IOException("inspection failed")) : Task.CompletedTask);
        seeding.Setup(s => s.RemoveCompletedTorrentsAsync(category, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RemovalResult());
        var cache = factory.Services.GetRequiredService<ITorrentCacheService>();

        async Task Scan(bool fullScan = false)
        {
            using var scope = factory.Services.CreateScope();
            var services = scope.ServiceProvider;
            var processor = new TorrentProcessor(NullLogger<TorrentProcessor>.Instance, registry.Object,
                services.GetRequiredService<TorrentarrDbContext>(), config, cache, new(),
                seedingService: seeding.Object, issues: services.GetRequiredService<IOperationalIssueReporter>(), clock: factory.Clock);
            if (fullScan) await processor.ProcessTorrentsAsync(category);
            else await processor.ProcessTorrentAsync(torrent.Hash);
        }

        async Task AssertIssue(string state, int count, int occurrences = 0)
        {
            factory.Clock.Advance();
            var response = await http.GetFromJsonAsync<JsonElement>($"/web/issues?state={state}&q={intent}");
            response.GetProperty("available").GetBoolean().Should().BeTrue();
            response.GetProperty("total").GetInt32().Should().Be(count);
            if (count == 0) return;
            var group = response.GetProperty("items")[0];
            group.GetProperty("affected").GetInt32().Should().Be(1);
            group.GetProperty("occurrences").GetInt32().Should().Be(occurrences);
            group.GetProperty("impact").GetString().Should().Contain("Original blocker evidence");
        }

        await Scan();
        await AssertIssue("active", 1, 1);
        torrent.State = "queuedDL";
        await Scan();
        await AssertIssue("active", 1, 1);
        cache.Clear();
        await Scan();
        await AssertIssue("active", 1, 1);

        torrent.State = blockedState;
        if (allowlist)
        {
            var excluded = files;
            files = [];
            await Scan();
            await AssertIssue("active", 1, 1);
            files = excluded;
        }
        await Scan();
        await AssertIssue("active", 1, 2);

        files = [new() { Name = "episode.mkv" }];
        torrent.State = "downloading";
        torrent.Availability = 1;
        failScan = true;
        await FluentActions.Awaiting(() => Scan()).Should().ThrowAsync<IOException>();
        await AssertIssue("active", 1, 2);
        failScan = false;
        if (removeTorrent) inventory = [];
        await Scan(fullScan: removeTorrent);
        await AssertIssue("active", 0);
        await AssertIssue("resolved", 1, 2);
    }

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task IssuesAreGroupedFilteredAndResolved(string prefix)
    {
        using var factory = new IssueFactory(); using var client = factory.CreateClientWithApiToken();
        var reporter = factory.Services.GetRequiredService<IOperationalIssueReporter>();
        var issue = new OperationalIssueReport("test.profile", "automation", "Search skipped", "Original profile preserved.", "Check profiles.", ArrInstance: "Sonarr-TV", SubjectId: "1", Severity: "warning");
        await reporter.ReportAsync(issue); await reporter.ReportAsync(issue with { SubjectId = "2" });
        factory.Clock.Advance(29);
        var hidden = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        hidden.GetProperty("total").GetInt32().Should().Be(0);
        await reporter.ReportAsync(issue);
        factory.Clock.Advance(1);
        var newError = issue with { SubjectId = "new-error", Severity = "error" };
        await reporter.ReportAsync(newError);
        var response = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues?category=automation");
        response.GetProperty("available").GetBoolean().Should().BeTrue();
        response.GetProperty("total").GetInt32().Should().Be(1);
        var group = response.GetProperty("items")[0]; group.GetProperty("affected").GetInt32().Should().Be(2);
        group.GetProperty("severity").GetString().Should().Be("warning");
        var details = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues/" + group.GetProperty("id").GetInt64());
        details.GetProperty("total").GetInt32().Should().Be(2);
        await reporter.ResolveAsync(newError);
        await reporter.ResolveAsync(issue);
        var active = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        active.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(1);
        var history = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues?state=resolved");
        history.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(2);
        (await client.GetAsync(prefix + "/issues?state=invalid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var summary = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues/summary");
        summary.GetProperty("activeGroups").GetInt32().Should().Be(1);
        factory.Clock.Advance(1);
        await reporter.ReportAsync(issue);
        var briefIssue = issue with { SubjectId = "brief" };
        await reporter.ReportAsync(briefIssue);
        await reporter.ResolveAsync(briefIssue);
        var stillOne = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        stillOne.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(1);
        factory.Clock.Advance();
        var recurrent = await client.GetFromJsonAsync<JsonElement>(prefix + "/issues");
        recurrent.GetProperty("items")[0].GetProperty("affected").GetInt32().Should().Be(2);
    }

    [Theory]
    [InlineData("/web")]
    [InlineData("/api")]
    public async Task TorrentSubjectsUseClientScopedNamesAndCategoriesWithoutChangingIdentity(string prefix)
    {
        using var factory = new IssueFactory(); using var client = factory.CreateClientWithApiToken();
        var cache = factory.Services.GetRequiredService<ITorrentCacheService>();
        cache.SetName("qBit-General", "abc123", "A Returners Magic S02E04");
        cache.SetCategory("qBit-General", "abc123", "sonarr-anime token=secret");
        cache.SetName("another-client", "abc123", "Wrong client torrent");
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new(
            "import.ownership", "automation", "Import owner is unresolved", "Import skipped", "Check category ownership",
            ClientInstance: "qBit-General", SubjectType: "torrent", SubjectId: "abc123"));
        factory.Clock.Advance();
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
        using var source = new IssueFactory();
        using var factory = source.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton(registry.Object)));
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new(
            "import.ownership", "automation", "Import owner is unresolved", "Import skipped", "Check category ownership",
            ClientInstance: "qBit-General", SubjectType: "torrent", SubjectId: "abc123"));
        source.Clock.Advance();
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
        using var factory = new IssueFactory();
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
        factory.Clock.Advance();
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
        using var factory = new IssueFactory(); using var client = factory.CreateClientWithApiToken();
        using (var scope = factory.Services.CreateScope())
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(scope.ServiceProvider.GetRequiredService<Torrentarr.Infrastructure.Database.TorrentarrDbContext>().Database, "DROP TABLE torrentarr_operational_issues");
        await factory.Services.GetRequiredService<IOperationalIssueReporter>().ReportAsync(new("database.test", "system", "Database unavailable", "Search held back", "Check database"));
        var early = await client.GetFromJsonAsync<JsonElement>("/web/issues");
        early.GetProperty("pending").GetArrayLength().Should().Be(0);
        factory.Clock.Advance();
        var response = await client.GetFromJsonAsync<JsonElement>("/web/issues");
        response.GetProperty("available").GetBoolean().Should().BeFalse(); response.GetProperty("pending").GetArrayLength().Should().Be(1);
        var summary = await client.GetFromJsonAsync<JsonElement>("/web/issues/summary");
        summary.GetProperty("activeGroups").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task ApiIssuesRequireToken()
    {
        using var factory = new IssueFactory(); using var client = factory.CreateClient();
        (await client.GetAsync("/api/issues")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/issues/summary")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
