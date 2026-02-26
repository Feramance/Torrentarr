using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Custom WebApplicationFactory that:
/// - Writes a minimal, known-good config.toml to a temp file and points
///   TORRENTARR_CONFIG at it so Program.cs never touches the user's real config.
/// - Replaces the SQLite on-disk database with an in-process SQLite :memory: database.
/// - Prevents ArrWorkerManager and ProcessOrchestratorService from spawning processes.
/// </summary>
public class TorrentarrWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    // Keep the connection open for the lifetime of the factory so the in-memory DB persists.
    private readonly SqliteConnection _keepAliveConnection;
    private readonly string _tempConfigPath;

    // Minimal valid TOML config used for all Host integration tests.
    // Single-quoted strings are TOML literal strings (no escape processing) — safe for regex patterns.
    private const string TestConfigToml = """
        [Settings]
        ConfigVersion = "5.9.0"
        LoopSleepTimer = 5
        FailedCategory = "failed"
        RecheckCategory = "recheck"
        PingURLS = ["one.one.one.one"]

        [WebUI]
        Host = "0.0.0.0"
        Port = 6969
        Token = ""
        LiveArr = false
        """;

    public TorrentarrWebApplicationFactory()
    {
        _keepAliveConnection = new SqliteConnection("Data Source=:memory:");
        _keepAliveConnection.Open();

        // Write the test config before the host starts so Program.cs picks it up.
        _tempConfigPath = Path.GetTempFileName() + ".toml";
        File.WriteAllText(_tempConfigPath, TestConfigToml);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _tempConfigPath);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Replace the SQLite DbContext with an in-memory SQLite connection.
            // SQLite :memory: (not EF InMemory) so PRAGMA and raw SQL still work.
            services.RemoveAll<DbContextOptions<TorrentarrDbContext>>();
            services.RemoveAll<DbContextOptions>();

            services.AddDbContext<TorrentarrDbContext>(options =>
                options.UseSqlite(_keepAliveConnection));

            // Remove all IHostedService registrations so background workers don't start.
            var hostedDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService))
                .ToList();
            foreach (var d in hostedDescriptors)
                services.Remove(d);

            // Remove the singleton ArrWorkerManager (registered separately) and replace with stub.
            services.RemoveAll<ArrWorkerManager>();
            services.AddSingleton<ArrWorkerManager, NoOpArrWorkerManager>();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _keepAliveConnection.Dispose();
            if (File.Exists(_tempConfigPath))
                File.Delete(_tempConfigPath);
            Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", null);
        }
        base.Dispose(disposing);
    }
}

/// <summary>Stub that does nothing — prevents worker process spawning in tests.</summary>
public class NoOpArrWorkerManager : ArrWorkerManager
{
    public NoOpArrWorkerManager(
        ILogger<ArrWorkerManager> logger,
        IServiceScopeFactory scopeFactory,
        TorrentarrConfig config,
        ProcessStateManager stateManager,
        IConnectivityService connectivityService)
        : base(logger, scopeFactory, config, stateManager, connectivityService) { }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
}
