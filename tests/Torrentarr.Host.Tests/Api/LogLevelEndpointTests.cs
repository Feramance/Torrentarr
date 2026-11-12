using FluentAssertions;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Integration tests for runtime log-level endpoints:
///   POST /web/loglevel
///   POST /api/loglevel
/// </summary>
[Collection("HostWeb")]
public class LogLevelEndpointTests : IClassFixture<TorrentarrWebApplicationFactory>
{
    private readonly TorrentarrWebApplicationFactory _factory;

    public LogLevelEndpointTests(TorrentarrWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── POST /web/loglevel ────────────────────────────────────────────────────

    [Fact]
    public async Task PostLogLevel_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogLevel_ResponseHasSuccessTrue()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out var success).Should().BeTrue();
        success.GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task PostLogLevel_ResponseHasLevelField()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "information" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("level", out _).Should().BeTrue("response must include the active log level");
    }

    [Theory]
    [InlineData("information")]
    [InlineData("debug")]
    [InlineData("warning")]
    [InlineData("error")]
    [InlineData("VERBOSE")]   // alias for trace
    [InlineData("CRITICAL")]  // alias for fatal
    public async Task PostLogLevel_ValidLevel_Returns200(string level)
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/web/loglevel", new { level });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLogLevel_UnknownLevel_FallsBackToInformation()
    {
        var client = _factory.CreateClientWithApiToken();

        // Unknown level strings fall back to Information per the switch default
        var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "garbage" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        json.GetProperty("level").GetString().Should().Be("Information");
    }

    // ── POST /api/loglevel (mirror) ───────────────────────────────────────────

    [Fact]
    public async Task PostApiLogLevel_Returns200()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/api/loglevel", new { level = "information" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostApiLogLevel_ResponseHasSameShape()
    {
        var client = _factory.CreateClientWithApiToken();

        var response = await client.PostAsJsonAsync("/api/loglevel", new { level = "debug" });
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        json.TryGetProperty("success", out _).Should().BeTrue();
        json.TryGetProperty("level", out _).Should().BeTrue();
    }

    [Theory]
    [InlineData("/web/loglevel", "TRACE")]
    [InlineData("/web/loglevel", "VERBOSE")]
    [InlineData("/api/loglevel", "TRACE")]
    [InlineData("/api/loglevel", "VERBOSE")]
    public async Task TraceAliases_EnableVerbose_AndPropagateToWorkers(string route, string level)
    {
        using var client = _factory.CreateClientWithApiToken();
        var config = _factory.Services.GetRequiredService<Torrentarr.Core.Configuration.TorrentarrConfig>();
        config.ArrInstances["logging-test"] = new();
        var signal = Path.Combine(Torrentarr.Core.Configuration.ConfigurationLoader.GetDataDirectoryPath(), "logs", "worker-logging-test.loglevel");
        try
        {
            var response = await client.PostAsJsonAsync(route, new { level });
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            body.GetProperty("level").GetString().Should().Be("Verbose");
            (await File.ReadAllTextAsync(signal)).Should().Be("Verbose");
        }
        finally
        {
            config.ArrInstances.Remove("logging-test");
            File.Delete(signal);
            await client.PostAsJsonAsync(route, new { level = "INFO" });
        }
    }

    [Fact]
    public async Task ApplicationEvents_ReachAggregateAndWorkerFiles_WhileEfNoiseIsSuppressed()
    {
        var previousConfig = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
        var previousOverride = Torrentarr.Core.Configuration.ConfigurationLoader.TestConfigPathOverride;
        try
        {
            // Other test hosts can close the process-wide Serilog logger.
            using var host = new TorrentarrWebApplicationFactory();
            using var client = host.CreateClientWithApiToken();
            using var response = await client.PostAsJsonAsync("/web/loglevel", new { level = "TRACE" });
            response.EnsureSuccessStatusCode();
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            var application = factory.CreateLogger("Torrentarr.Infrastructure.Services.ArrSyncService");
            var ef = factory.CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");
            var marker = Guid.NewGuid().ToString("N");
            var instance = "logging-" + marker;
            var root = Path.Combine(Torrentarr.Core.Configuration.ConfigurationLoader.GetDataDirectoryPath(), "logs");
            try
            {
                using (application.BeginScope(new Dictionary<string, object> { ["ProcessInstance"] = instance }))
                    application.LogInformation("Database SaveChanges DbContext DbCommand {Marker:l}", marker);
                ef.IsEnabled(LogLevel.Debug).Should().BeFalse();
                ef.IsEnabled(LogLevel.Information).Should().BeFalse();
                ef.IsEnabled(LogLevel.Warning).Should().BeTrue();
                ef.LogWarning("Database warning {Marker:l}", marker);
                var aggregate = string.Join('\n', Directory.GetFiles(root, "all-*.log").SelectMany(path => ReadLog(path).Split('\n')).Where(line => line.Contains(marker)));
                aggregate.Should().Contain("Database SaveChanges DbContext DbCommand " + marker).And.Contain("Database warning " + marker);
                var worker = Directory.GetFiles(root, $"worker-{instance}-*.log").Should().ContainSingle().Subject;
                ReadLog(worker).Should().Contain("Database SaveChanges DbContext DbCommand " + marker).And.Contain("Torrentarr." + instance);
            }
            finally { await client.PostAsJsonAsync("/web/loglevel", new { level = "INFO" }); }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", previousConfig);
            Torrentarr.Core.Configuration.ConfigurationLoader.TestConfigPathOverride = previousOverride;
        }
    }

    private static string ReadLog(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
