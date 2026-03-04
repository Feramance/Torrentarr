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
using Xunit;

namespace Torrentarr.Host.Tests.Api;

/// <summary>
/// Collection for all tests that use TorrentarrWebApplicationFactory so they run sequentially
/// and avoid "The entry point exited without ever building an IHost" when starting the host.
/// </summary>
[CollectionDefinition("HostWeb", DisableParallelization = true)]
public class HostWebCollection : ICollectionFixture<TorrentarrWebApplicationFactory>;

[CollectionDefinition("HostWebAuth", DisableParallelization = true)]
public class HostWebAuthCollection : ICollectionFixture<AuthEnabledWebApplicationFactory>;

[CollectionDefinition("HostWebLocalAuth", DisableParallelization = true)]
public class HostWebLocalAuthCollection : ICollectionFixture<LocalAuthWebApplicationFactory>;

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
        ConfigVersion = "5.9.2"
        LoopSleepTimer = 5
        FailedCategory = "failed"
        RecheckCategory = "recheck"
        PingURLS = ["one.one.one.one"]

        [WebUI]
        Host = "0.0.0.0"
        Port = 6969
        Token = ""
        AuthDisabled = true
        LocalAuthEnabled = false
        OIDCEnabled = false
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
        // Ensure our config path is used when this factory builds the host (env can be overwritten by other fixtures)
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _tempConfigPath);
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

    /// <summary>Sets TORRENTARR_CONFIG and ConfigurationLoader.TestConfigPathOverride to this factory's config path. Call before CreateClient() when multiple factory types exist in the test run.</summary>
    public void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _tempConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _tempConfigPath;
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

/// <summary>
/// Factory that uses auth-enabled config (AuthDisabled = false, no Local/OIDC so token-only) for auth middleware and GET /web/token 401 tests.
/// </summary>
public class AuthEnabledWebApplicationFactory : TorrentarrWebApplicationFactory
{
    private readonly string _authConfigPath;

    public AuthEnabledWebApplicationFactory()
    {
        _authConfigPath = Path.GetTempFileName() + ".auth.toml";
        File.WriteAllText(_authConfigPath, TestConfigTomlWithAuth);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _authConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _authConfigPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _authConfigPath);
        base.ConfigureWebHost(builder);
    }

    public new void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _authConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _authConfigPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Leave config file on disk so shared TORRENTARR_CONFIG does not point to a deleted file
        }
        base.Dispose(disposing);
    }

    private const string TestConfigTomlWithAuth = """
        [Settings]
        ConfigVersion = "5.9.2"
        LoopSleepTimer = 5
        FailedCategory = "failed"
        RecheckCategory = "recheck"
        PingURLS = ["one.one.one.one"]

        [WebUI]
        Host = "0.0.0.0"
        Port = 6969
        Token = "test-api-token"
        AuthDisabled = false
        LocalAuthEnabled = false
        OIDCEnabled = false
        LiveArr = false
        """;
}

/// <summary>
/// Factory with Local auth (Username = admin, PasswordHash = BCrypt of "password") for login and set-password tests.
/// </summary>
public class LocalAuthWebApplicationFactory : TorrentarrWebApplicationFactory
{
    private readonly string _localAuthConfigPath;
    public const string TestUsername = "admin";
    public const string TestPassword = "password";

    public LocalAuthWebApplicationFactory()
    {
        var hash = BCrypt.Net.BCrypt.HashPassword(TestPassword, 10);
        var escapedHash = hash.Replace("\\", "\\\\").Replace("\"", "\\\"");
        _localAuthConfigPath = Path.GetTempFileName() + ".localauth.toml";
        var toml = $"""
            [Settings]
            ConfigVersion = "5.9.2"
            LoopSleepTimer = 5
            FailedCategory = "failed"
            RecheckCategory = "recheck"
            PingURLS = ["one.one.one.one"]

            [WebUI]
            Host = "0.0.0.0"
            Port = 6969
            Token = "test-api-token"
            AuthDisabled = false
            LocalAuthEnabled = true
            OIDCEnabled = false
            Username = "admin"
            PasswordHash = "{escapedHash}"
            LiveArr = false
            """;
        File.WriteAllText(_localAuthConfigPath, toml);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _localAuthConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _localAuthConfigPath;
    }

    public new void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _localAuthConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _localAuthConfigPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && File.Exists(_localAuthConfigPath))
            File.Delete(_localAuthConfigPath);
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
