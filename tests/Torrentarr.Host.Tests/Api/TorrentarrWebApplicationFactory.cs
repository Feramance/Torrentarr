using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
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

/// <summary>Separate host instance for update endpoint tests so POST /web/update cannot interleave with other API tests that share <see cref="HostWebCollection"/>.</summary>
[CollectionDefinition("HostWebUpdate", DisableParallelization = true)]
public class HostWebUpdateCollection : ICollectionFixture<TorrentarrWebApplicationFactory>;

[CollectionDefinition("HostWebAuth", DisableParallelization = true)]
public class HostWebAuthCollection : ICollectionFixture<AuthEnabledWebApplicationFactory>;

[CollectionDefinition("HostWebLocalAuth", DisableParallelization = true)]
public class HostWebLocalAuthCollection : ICollectionFixture<LocalAuthWebApplicationFactory>;

[CollectionDefinition("HostWebLocalAuthNoPassword", DisableParallelization = true)]
public class HostWebLocalAuthNoPasswordCollection : ICollectionFixture<LocalAuthNoPasswordWebApplicationFactory>;

/// <summary>
/// Custom WebApplicationFactory that:
/// - Writes a minimal, known-good config.toml to a temp file and points
///   TORRENTARR_CONFIG at it so Program.cs never touches the user's real config.
/// - Replaces the SQLite on-disk database with an in-process SQLite :memory: database.
/// - Prevents ArrWorkerManager and ProcessOrchestratorService from spawning processes.
/// </summary>
public class TorrentarrWebApplicationFactory : WebApplicationFactory<Program>, IDisposable
{
    /// <summary>When set by a derived factory before CreateClient(), this host build uses that config path (avoids races when collections run in parallel).</summary>
    protected static readonly System.Threading.AsyncLocal<string?> ConfigPathForCurrentBuild = new();

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
        Token = "test-api-token"
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
        // Use per-build path when set (e.g. by derived factory's SetConfigEnv before CreateClient) so parallel collections get correct config.
        var configPath = ConfigPathForCurrentBuild.Value ?? _tempConfigPath;
        ConfigPathForCurrentBuild.Value = null; // clear so a later base-factory build does not inherit an auth path
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", configPath);
        ConfigurationLoader.TestConfigPathOverride = configPath;
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

    /// <summary>Creates a client with default Bearer token for /api/* (API token is always required). Use this for tests that call API endpoints.</summary>
    public HttpClient CreateClientWithApiToken()
    {
        var client = base.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-api-token");
        return client;
    }

    /// <summary>Creates a client without the default Bearer token. Use for tests that expect 401 when calling /api/* without auth.</summary>
    public HttpClient CreateClientWithoutApiToken()
    {
        return base.CreateClient();
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

    public new void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _authConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _authConfigPath;
        ConfigPathForCurrentBuild.Value = _authConfigPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Ensure auth config is used even when build runs on a different thread (AsyncLocal not set)
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        // Ensure local auth config is used even when build runs on a different thread (AsyncLocal not set)
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _localAuthConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _localAuthConfigPath;
    }

    public new void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _localAuthConfigPath);
        ConfigurationLoader.TestConfigPathOverride = _localAuthConfigPath;
        ConfigPathForCurrentBuild.Value = _localAuthConfigPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && File.Exists(_localAuthConfigPath))
            File.Delete(_localAuthConfigPath);
        base.Dispose(disposing);
    }
}

/// <summary>
/// Factory with local auth enabled but no password set (AuthDisabled=false, LocalAuthEnabled=true, PasswordHash="").
/// Used to test the SETUP_REQUIRED login flow.
/// </summary>
public class LocalAuthNoPasswordWebApplicationFactory : TorrentarrWebApplicationFactory
{
    private readonly string _configPath;

    private const string TomlContent = """
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
        Username = ""
        PasswordHash = ""
        LiveArr = false
        """;

    public LocalAuthNoPasswordWebApplicationFactory()
    {
        _configPath = Path.GetTempFileName() + ".nopwd.toml";
        File.WriteAllText(_configPath, TomlContent);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        ConfigurationLoader.TestConfigPathOverride = _configPath;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        ConfigurationLoader.TestConfigPathOverride = _configPath;
    }

    public new void SetConfigEnv()
    {
        Environment.SetEnvironmentVariable("TORRENTARR_CONFIG", _configPath);
        ConfigurationLoader.TestConfigPathOverride = _configPath;
        ConfigPathForCurrentBuild.Value = _configPath;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && File.Exists(_configPath))
            File.Delete(_configPath);
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
