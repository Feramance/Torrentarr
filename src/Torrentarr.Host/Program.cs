using Torrentarr.Core.Configuration;
using Torrentarr.Core.Interfaces;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Host;
using Torrentarr.Infrastructure.Endpoints;
using Torrentarr.Infrastructure.Services;
using Torrentarr.Host.Sinks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Security.Claims;
using System.Security.Cryptography;

// Informational commands exit before configuration, logging or ownership initialization.
var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
var firstArg = cmdArgs.Count > 0 ? cmdArgs[0].Trim().ToLowerInvariant() : "";

if (firstArg is "--help" or "-h" or "--version" or "-v" or "--source" or "-s" or "--license" or "-l")
{
    if (cmdArgs.Count != 1)
    {
        Console.Error.WriteLine("Informational commands do not accept additional arguments.");
        return 1;
    }
    if (firstArg is "--help" or "-h")
    {
        Console.WriteLine("Usage: Torrentarr.Host [option]");
        Console.WriteLine("  --help, -h           Show this help and exit.");
        Console.WriteLine("  --version, -v        Show version and exit.");
        Console.WriteLine("  --source, -s         Show source URL and exit.");
        Console.WriteLine("  --license, -l        Show license and exit.");
        Console.WriteLine("  --gen-config, -gc    Generate default configuration.");
        Console.WriteLine("  --repair-database    Repair the database.");
        Console.WriteLine("  --backup-database [destination]  Back up the database.");
        return 0;
    }
    // --version / -v: print version and exit (qBitrr parity)
    if (firstArg is "--version" or "-v")
    {
        Console.WriteLine($"Torrentarr {UpdateService.GetCurrentVersion()}");
        return 0;
    }

    // --license / -l: print license and exit (qBitrr parity)
    if (firstArg is "--license" or "-l")
    {
        Console.WriteLine("Torrentarr is licensed under the MIT License.");
        Console.WriteLine("Copyright (c) 2024-2026 Torrentarr contributors.");
        Console.WriteLine("See https://github.com/Feramance/Torrentarr/blob/master/LICENSE for the full text.");
        return 0;
    }

    // --source / -s: print source code URL and exit (qBitrr parity)
    if (firstArg is "--source" or "-s")
    {
        Console.WriteLine("https://github.com/Feramance/Torrentarr");
        return 0;
    }

}

// Data directory: aligned with resolved config path (see ConfigurationLoader.GetDataDirectoryPath)
var basePath = ConfigurationLoader.GetDataDirectoryPath();
var logsPath = Path.Combine(basePath, "logs");
var dbPath = Path.Combine(basePath, "torrentarr.db");
Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logsPath);

// Config web: placeholder for redacted secrets; must be in scope before any handler that uses it
const string REDACTED_PLACEHOLDER = "[redacted]";
const string SensitiveKeyPatternRegex = @"(apikey|api_key|token|password|secret|passkey|credential)";

// Mutable level switch — lets /web/loglevel and /api/loglevel change the level at runtime
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

// Create custom sink for per-worker log files
var workerSink = new WorkerLogEventSink(logsPath);

// Configure Serilog - write to .config/logs/ (same path as API reads from) with process metadata enrichment
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.WithProperty("ProcessType", "Host")
    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .Filter.ByExcluding(e =>
        e.RenderMessage().Contains("DbCommand") ||
        e.RenderMessage().Contains("started tracking") ||
        e.RenderMessage().Contains("changed state from") ||
        e.RenderMessage().Contains("generated temporary value") ||
        e.RenderMessage().Contains("Closing data reader") ||
        e.RenderMessage().Contains("DetectChanges") ||
        e.RenderMessage().Contains("SaveChanges") ||
        e.RenderMessage().Contains("Opening connection") ||
        e.RenderMessage().Contains("Opened connection") ||
        e.RenderMessage().Contains("Closing connection") ||
        e.RenderMessage().Contains("Closed connection") ||
        e.RenderMessage().Contains("Creating DbConnection") ||
        e.RenderMessage().Contains("Created DbConnection") ||
        e.RenderMessage().Contains("Beginning transaction") ||
        e.RenderMessage().Contains("Began transaction") ||
        e.RenderMessage().Contains("Committing transaction") ||
        e.RenderMessage().Contains("Committed transaction") ||
        e.RenderMessage().Contains("Disposing transaction") ||
        e.RenderMessage().Contains("Disposed transaction") ||
        e.RenderMessage().Contains("Disposing connection to database") ||
        e.RenderMessage().Contains("Disposed connection to database") ||
        e.RenderMessage().Contains("DbContext") ||
        e.RenderMessage().Contains("was detected as changed") ||
        e.RenderMessage().Contains("Executing endpoint") ||
        e.RenderMessage().Contains("Executed endpoint") ||
        e.RenderMessage().Contains("Request starting") ||
        e.RenderMessage().Contains("Request finished") ||
        e.RenderMessage().Contains("Writing value of type") ||
        e.RenderMessage().Contains("is valid for the request") ||
        e.RenderMessage().Contains("A data reader") ||
        e.RenderMessage().Contains("Entity Framework Core") ||
        e.RenderMessage().Contains("queryContext.StartTracking") ||
        e.RenderMessage().Contains("InternalEntityEntry") ||
        e.RenderMessage().Contains("shadowSnapshot"))
    .WriteTo.Console()
    .WriteTo.Sink(workerSink)
    .CreateLogger();

try
{
    Log.Information("Torrentarr starting...");

    // TestServer clones share a fixture config; the existing test override never applies in production.
    using var ownership = ConfigurationLoader.TestConfigPathOverride != null
        ? null : HostConfigOwnership.Acquire(ConfigurationLoader.GetDefaultConfigPath());
    await InstanceRenamePersistence.RecoverAsync(ConfigurationLoader.GetDefaultConfigPath(), dbPath);

    // Load configuration
    var configLoader = new ConfigurationLoader();
    TorrentarrConfig config;
    bool createdDefault;

    try
    {
        (config, createdDefault) = configLoader.LoadOrCreate();

        if (createdDefault)
            Log.Information("Created default configuration at: {Path}", ConfigurationLoader.GetDefaultConfigPath());

        // Ensure API token exists so /api/* is never unprotected
        if (string.IsNullOrEmpty(config.WebUI.Token))
        {
            var tokenBytes = new byte[32];
            RandomNumberGenerator.Fill(tokenBytes);
            config.WebUI.Token = Convert.ToBase64String(tokenBytes);
            configLoader.SaveConfig(config);
            Log.Information("Generated and persisted API token (Token was empty)");
        }

        Log.Information("Configuration loaded from {Path}", ConfigurationLoader.GetDefaultConfigPath());
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to load configuration");
        return 1;
    }

    var insecureExposure = WebUIAuthHelpers.CheckInsecureExposure(config.WebUI);
    if (insecureExposure != null)
    {
        Log.Fatal(insecureExposure);
        return 1;
    }
    if (config.WebUI.AuthDisabled && WebUIAuthHelpers.IsPublicBindHost(config.WebUI.Host)
        && config.WebUI.AllowInsecureExposure is null)
    {
        Log.Warning(
            "WebUI.AllowInsecureExposure is unset (legacy config). All API and WebUI actions are available without credentials on {Host}. Set AllowInsecureExposure = true to acknowledge this, bind Host to 127.0.0.1, or set AuthDisabled = false.",
            config.WebUI.Host);
    }

    if (config.Settings.ConsoleLevel != null)
    {
        levelSwitch.MinimumLevel = config.Settings.ConsoleLevel.ToUpperInvariant() switch
        {
            "TRACE" => LogEventLevel.Verbose,
            "DEBUG" => LogEventLevel.Debug,
            "INFO" or "INFORMATION" or "NOTICE" => LogEventLevel.Information,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
        Log.Information("Log level set to {Level} from config ConsoleLevel", levelSwitch.MinimumLevel);
    }

    if (!config.GetAllTorrentClients().Values.Any(q => q.Host != "CHANGE_ME" && q.UserName != "CHANGE_ME" && q.Password != "CHANGE_ME"))
    {
        Log.Warning("qBittorrent is not configured. Please configure via WebUI at http://localhost:{Port}", config.WebUI.Port);
        Log.Warning("Or edit the config file at: {Path}", ConfigurationLoader.GetDefaultConfigPath());
    }

    var managedInstances = config.ArrInstances.Where(x => x.Value.Managed && x.Value.URI != "CHANGE_ME").ToList();
    if (managedInstances.Count == 0)
        Log.Warning("No Arr instances configured. Configure via WebUI or edit config.toml");
    else
        Log.Information("Managing {Count} Arr instances: {Instances}",
            managedInstances.Count, string.Join(", ", managedInstances.Select(x => x.Key)));

    // Build ASP.NET Core WebApplication
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Register LoggingLevelSwitch so endpoints can change log level at runtime
    builder.Services.AddSingleton(levelSwitch);
    builder.Services.AddSingleton(config);
    builder.Services.AddSingleton(configLoader);
    builder.Services.AddSingleton<DatabaseRestartCoordinator>();
    builder.Services.AddHostedService<DatabaseRestartWatchdogService>();
    builder.Services.AddSingleton<ITorrentClientFactory, QBittorrentTorrentClientFactory>();
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddSingleton<ITorrentClientRegistry>(sp => sp.GetRequiredService<QBittorrentConnectionManager>());
    builder.Services.AddSingleton<IPathMappingService, PathMappingService>();
    builder.Services.AddSingleton<IHardlinkInspector, HardlinkInspector>();
    builder.Services.AddSingleton<ITorrentInventoryService, TorrentInventoryService>();
    builder.Services.AddSingleton<ISafeDeletionService, SafeDeletionService>();
    builder.Services.AddSingleton<TorrentAutomationService>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<TorrentAutomationService>());
    builder.Services.AddSingleton<MaintenanceCoordinator>();
    builder.Services.AddSingleton<IMaintenanceCoordinator>(sp => sp.GetRequiredService<MaintenanceCoordinator>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<MaintenanceCoordinator>());
    builder.Services.AddSingleton<ProcessStateManager>();
    builder.Services.AddSingleton<IConnectivityService, ConnectivityService>();
    // Arr workers run out-of-process so a worker crash cannot take down WebUI.
    builder.Services.AddSingleton<WorkerProcessSupervisor>();
    builder.Services.AddSingleton<IProcessOrchestrator>(sp => sp.GetRequiredService<WorkerProcessSupervisor>());
    builder.Services.AddSingleton<SearchYearCursor>();
    builder.Services.AddSingleton<StalledUploadTracker>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<WorkerProcessSupervisor>());
    builder.Services.AddHostedService<ProcessOrchestratorService>();
    // Scoped services (one per request / scope)
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddScoped<IArrImportService, ArrImportService>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<IFreeSpaceService, FreeSpaceService>();
    builder.Services.AddSingleton<IMediaValidationService, MediaValidationService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaService>();
    builder.Services.AddScoped<ISearchExecutor, SearchExecutor>();
    builder.Services.AddScoped<QualityProfileSwitcherService>();
    builder.Services.AddSingleton<ITorrentCacheService, TorrentCacheService>();
    builder.Services.AddSingleton<IImportPathTracker, ImportPathTracker>();
    builder.Services.AddScoped<IDatabaseHealthService, DatabaseHealthService>();
    builder.Services.AddScoped<QBitCategoryEnsureService>();
    builder.Services.AddSingleton<QBitCategoryWorkerManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<QBitCategoryWorkerManager>());
    builder.Services.AddHostedService<PeriodicWalCheckpointService>();
    builder.Services.AddSingleton<ConfigReloader>();
    builder.Services.AddSingleton<IConfigReloader>(sp => sp.GetRequiredService<ConfigReloader>());
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ConfigReloader>());
    builder.Services.AddHostedService<LiveConfigurationRuntime>();
    // §6.10 / §1.8: update check + auto-update
    builder.Services.AddSingleton<UpdateService>();
    builder.Services.AddHostedService<AutoUpdateBackgroundService>();

    builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();
    builder.Services.AddHttpClient();
    builder.Services.AddHttpClient("ArrOpen").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    builder.Services.AddHttpClient("ArrOpenSkipTls").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
    });
    builder.Services.AddScoped<CatalogRollupService>();
    builder.Services.AddScoped<ArrThumbnailService>();

    var dataProtectionKeyPath = Path.Combine(basePath, "data-protection-keys");
    Directory.CreateDirectory(dataProtectionKeyPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeyPath))
        .SetApplicationName("Torrentarr");

    var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "torrentarr_session";
            string AuthenticationStamp() => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                Newtonsoft.Json.JsonConvert.SerializeObject(new
                {
                    config.WebUI.Username,
                    config.WebUI.PasswordHash,
                    config.WebUI.LocalAuthEnabled,
                    config.WebUI.OIDCEnabled,
                    config.WebUI.OIDC
                }))));
            options.Events.OnSigningIn = context =>
            {
                if (context.Principal?.Identity is ClaimsIdentity identity)
                    identity.AddClaim(new Claim("torrentarr:auth", AuthenticationStamp()));
                return Task.CompletedTask;
            };
            options.Events.OnValidatePrincipal = context =>
            {
                if (context.Principal?.FindFirst("torrentarr:auth")?.Value != AuthenticationStamp()) context.RejectPrincipal();
                return Task.CompletedTask;
            };
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            var currentBase = UrlBaseHelper.NormalizeUrlBase(config.WebUI.UrlBase);
            if (!string.IsNullOrEmpty(currentBase))
                options.Cookie.Path = currentBase + "/";
            options.LoginPath = UrlBaseHelper.WithUrlBase(currentBase, "/login");
            options.AccessDeniedPath = UrlBaseHelper.WithUrlBase(currentBase, "/login");
        });
    authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            var oidc = config.WebUI.OIDC ?? new OIDCConfig();
            options.Authority = string.IsNullOrWhiteSpace(oidc.Authority) ? "https://localhost" : oidc.Authority.TrimEnd('/');
            options.ClientId = string.IsNullOrWhiteSpace(oidc.ClientId) ? "disabled" : oidc.ClientId;
            options.ClientSecret = oidc.ClientSecret;
            options.CallbackPath = oidc.CallbackPath;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Scope.Clear();
            foreach (var s in (oidc.Scopes ?? "openid profile").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                options.Scope.Add(s);
        });


    builder.Services.AddControllers()
        .AddNewtonsoftJson(options =>
        {
            options.SerializerSettings.ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
            options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Include;
            options.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc;
        });
    builder.Services.ConfigureHttpJsonOptions(options =>
        options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Torrentarr API",
            Version = "v1",
            Description = "API for qBittorrent + Arr automation (qBitrr C# port)"
        });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            In = ParameterLocation.Header,
            Description = "API token (WebUI.Token). Use for /api/* endpoints. Use the Authorize button to set."
        });
        c.AddSecurityRequirement(document => new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference("Bearer", document)] = []
        });
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    });

    // Database - paths already defined at top of file
    builder.Services.AddDbContext<TorrentarrDbContext>(options =>
    {
        options.UseSqlite($"Data Source={dbPath}")
               .LogTo(_ => { }, LogLevel.None);  // Suppress all EF Core SQL logs
        if (builder.Environment.IsDevelopment())
            options.EnableSensitiveDataLogging();
    });

    var liveWeb = new LiveWebConfiguration(builder.WebHost, config.WebUI);

    var app = builder.Build();
    app.MapGet("/web/config/status", (IConfigReloader reloader) => Results.Ok(reloader.GetStatus()));
    app.MapGet("/api/config/status", (IConfigReloader reloader) => Results.Ok(reloader.GetStatus()));
    if (ownership != null) app.Lifetime.ApplicationStopped.Register(ownership.Dispose);

    // Prefer physical files for development, with the embedded SPA as the standalone fallback.
    app.Environment.WebRootFileProvider = EmbeddedWebAssets.Create(
        app.Environment,
        typeof(Program).Assembly);

    liveWeb.Attach(app, config);
    app.UseRouting();
    app.Services.GetRequiredService<IConfigReloader>().ConfigReloaded += (_, args) =>
    {
        if (args.Success)
        {
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitorCache<OpenIdConnectOptions>>().Clear();
            app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitorCache<CookieAuthenticationOptions>>().Clear();
        }
    };
    app.UseConfigMutationGuard(ConfigurationLoader.GetDefaultConfigPath(), host: true);

    // First-run hint: Host wwwroot is build output; API still works without the SPA bundle.
    var webRoot = app.Environment.WebRootFileProvider;
    if (webRoot != null)
    {
        var indexFile = webRoot.GetFileInfo("index.html");
        if (!indexFile.Exists)
        {
            Log.Warning(
                "Web UI bundle was not found in the physical or embedded web root. Run ./build.sh or build webui for the full SPA.");
        }
    }

    // --gen-config / -gc: write default config and exit (qBitrr parity). Run after Build() so WebApplicationFactory gets an IHost.
    if (cmdArgs.Count == 1 && (firstArg == "--gen-config" || firstArg == "-gc"))
    {
        var configPath = ConfigurationLoader.GetDefaultConfigPath();
        var loader = new ConfigurationLoader(configPath);
        var defaultConfig = ConfigurationLoader.GenerateDefaultConfig();
        loader.SaveConfig(defaultConfig, configPath);
        Console.WriteLine($"Generated default configuration at: {configPath}");
        return 0;
    }
    // --repair-database: run WAL checkpoint + integrity check and exit (qBitrr parity).
    if (cmdArgs.Count == 1 && firstArg == "--repair-database")
    {
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database not found: {dbPath}");
            return 1;
        }
        var connStr = $"Data Source={dbPath}";
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection(connStr);
        conn.Open();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            cmd.ExecuteNonQuery();
        }
        string result;
        using (var cmd2 = conn.CreateCommand())
        {
            cmd2.CommandText = "PRAGMA integrity_check;";
            result = (cmd2.ExecuteScalar() as string) ?? "unknown";
        }
        Console.WriteLine($"Integrity check: {result}");
        return result.Equals("ok", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
    // --backup-database [dest]: online SQLite backup (qBitrr scripts/backup_database.py).
    if (cmdArgs.Count >= 1 && firstArg == "--backup-database")
    {
        if (!File.Exists(dbPath))
        {
            Console.Error.WriteLine($"Database not found: {dbPath}");
            return 1;
        }
        var dest = cmdArgs.Count >= 2
            ? cmdArgs[1]
            : Path.Combine(Path.GetDirectoryName(dbPath) ?? ".", "backups", $"torrentarr.db.{DateTime.UtcNow:yyyyMMdd}");
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        using (var source = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly"))
        {
            source.Open();
            if (File.Exists(dest))
                File.Delete(dest);
            using var destConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dest}");
            destConn.Open();
            source.BackupDatabase(destConn);
        }
        using (var verify = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dest}"))
        {
            verify.Open();
            using var cmd = verify.CreateCommand();
            cmd.CommandText = "PRAGMA quick_check;";
            var check = (cmd.ExecuteScalar() as string) ?? "unknown";
            Console.WriteLine($"Backup: {dest}");
            Console.WriteLine($"Integrity quick_check: {check}");
            return check.Equals("ok", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }
    }

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.Database.EnsureCreated();
        db.ConfigureWalMode();
        // Manual migrations for columns added after initial release (existing DBs skip EnsureCreated)
        ManualSqliteMigrations.Apply(db);
    }

    app.UseSwagger();
    app.UseSwaggerUI();

    app.UseCors("AllowAll");

    // Security headers
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
        await next(context);
    });

    // Static files — add cache-busting headers for the service worker
    // Relative build assets resolve identically for every SPA alias and URL base.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value;
        if (HttpMethods.IsGet(context.Request.Method) && path is "/" or "/ui" or "/ui/" or "/index.html" or "/static/index.html")
        {
            var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
            if (index.Exists)
            {
                using var reader = new StreamReader(index.CreateReadStream());
                var html = await reader.ReadToEndAsync(context.RequestAborted);
                var prefix = System.Net.WebUtility.HtmlEncode(string.IsNullOrEmpty(context.Request.PathBase.Value) ? UrlBaseHelper.NormalizeUrlBase(config.WebUI.UrlBase) : context.Request.PathBase.Value);
                context.Response.ContentType = "text/html";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync(html.Replace("<head>", $"<head><base href=\"{prefix}/\">"), context.RequestAborted);
                return;
            }
        }
        await next(context);
    });

    app.UseDefaultFiles();
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.Equals("/sw.js", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
            context.Response.Headers["Pragma"] = "no-cache";
            context.Response.Headers["Expires"] = "0";
        }
        await next(context);
    });
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseArrCatalogDbSafe();

    // Auth: when required (!AuthDisabled), protect /api/* and /web/* except public paths. Bearer token always works for API.
    app.Use(async (context, next) =>
    {
        var cfg = context.RequestServices.GetRequiredService<TorrentarrConfig>();
        var path = context.Request.Path.Value ?? "";

        // Always enforce API token for /api/* (token is generated at startup if empty)
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
        {
            var configuredToken = cfg.WebUI.Token;
            if (string.IsNullOrEmpty(configuredToken))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
            string? providedToken = null;
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                providedToken = authHeader["Bearer ".Length..];
            else if (WebUIAuthHelpers.TryGetQueryToken(context.Request, cfg.WebUI) is { } queryToken)
                providedToken = queryToken;
            if (string.IsNullOrEmpty(providedToken) || !WebUIAuthHelpers.TokenEquals(providedToken, configuredToken))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                return;
            }
            var identity = new ClaimsIdentity("Bearer");
            identity.AddClaim(new Claim(ClaimTypes.Name, "api"));
            context.User = new ClaimsPrincipal(identity);
            await next(context);
            return;
        }

        if (!IsAuthRequired(cfg))
        {
            await next(context);
            return;
        }

        if (WebUIAuthHelpers.IsPublicPath(path, context.Request.Method))
        {
            await next(context);
            return;
        }

        // 1) Bearer token (constant-time) — always accepted for API when Token is set
        var webToken = cfg.WebUI.Token;
        if (!string.IsNullOrEmpty(webToken))
        {
            string? providedToken = null;
            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                providedToken = authHeader["Bearer ".Length..];
            else if (WebUIAuthHelpers.TryGetQueryToken(context.Request, cfg.WebUI) is { } queryToken)
                providedToken = queryToken;

            if (!string.IsNullOrEmpty(providedToken) && WebUIAuthHelpers.TokenEquals(providedToken, webToken))
            {
                var identity = new ClaimsIdentity("Bearer");
                identity.AddClaim(new Claim(ClaimTypes.Name, "api"));
                context.User = new ClaimsPrincipal(identity);
                await next(context);
                return;
            }
        }

        // 2) Cookie (local or OIDC login)
        var result = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        if (result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true)
        {
            context.User = result.Principal;
            await next(context);
            return;
        }

        // Unauthenticated: 401 for API, redirect to /login for browser
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase) ||
            context.Request.Headers.Accept.Any(a => a?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true))
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }
        context.Response.Redirect(UrlBaseHelper.WithUrlBase(
            UrlBaseHelper.NormalizeUrlBase(cfg.WebUI.UrlBase), "/login"));
    });

    static bool IsAuthRequired(TorrentarrConfig c) => !c.WebUI.AuthDisabled;

    static bool MetaForceAllowed(HttpContext ctx, TorrentarrConfig cfg)
    {
        if (ctx.User?.Identity?.IsAuthenticated == true)
            return true;
        var authHeader = ctx.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true
            && !string.IsNullOrEmpty(cfg.WebUI.Token)
            && WebUIAuthHelpers.TokenEquals(authHeader["Bearer ".Length..], cfg.WebUI.Token))
            return true;
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return MetaForceRateLimiter.TryAcquire($"meta-force:{ip}");
    }

    app.MapControllers();
    app.MapArrCatalogEndpoints();
    app.MapRadarrMoviesEndpoints();

    // Home redirect: / → /ui
    app.MapGet("/", () => Results.Redirect(UrlBaseHelper.WithUrlBase(UrlBaseHelper.NormalizeUrlBase(config.WebUI.UrlBase), "/ui")));

    // Health check
    app.MapGet("/health", () => Results.Ok(new
    {
        status = "healthy",
        service = "torrentarr",
        timestamp = DateTime.UtcNow
    }));

    // qBitrr parity: curated OpenAPI + Swagger UI aliases
    app.MapGet("/api/openapi.json", (HttpContext ctx) => CuratedOpenApiDocument.ServeJson(ctx));
    app.MapGet("/web/openapi.json", (HttpContext ctx) => CuratedOpenApiDocument.ServeJson(ctx));
    app.MapGet("/api/docs", () => CuratedOpenApiDocument.RedirectToSwagger("/api/openapi.json"));
    app.MapGet("/web/docs", () => CuratedOpenApiDocument.RedirectToSwagger("/web/openapi.json"));

    foreach (var prefix in new[] { "/api", "/web" })
    {
        app.MapGet(prefix + "/torrent-clients", async (ITorrentClientRegistry registry, TorrentarrConfig cfg) =>
        {
            var clients = new List<object>();
            foreach (var (id, clientCfg) in cfg.GetAllTorrentClients())
            {
                var client = registry.GetClient(id);
                string? version = null;
                if (client != null)
                {
                    try { version = await client.GetVersionAsync(); } catch { /* reflected by connection state */ }
                }
                clients.Add(new
                {
                    id,
                    QbitInstance = id,
                    type = clientCfg.Type,
                    connected = client != null,
                    version,
                    capabilities = client?.Capabilities ?? TorrentClientCapabilities.None,
                    managedCategories = clientCfg.ManagedCategories
                });
            }
            return Results.Ok(clients);
        });
        app.MapTorrentMaintenanceEndpoints(prefix);
    }

    // ==================== /web/* endpoints ====================

    // Web Meta — fetches latest release from GitHub and compares with current version
    // §6.10: GET /web/meta — version info + update state + auth flags (MetaResponse-compatible)
    app.MapGet("/web/meta", async (HttpContext ctx, UpdateService updater, TorrentarrConfig cfg, int? force) =>
    {
        var forceRefresh = force.GetValueOrDefault() != 0;
        if (forceRefresh && !MetaForceAllowed(ctx, cfg))
            return Results.Json(new { error = "rate_limited", message = "Too many force refresh requests" }, statusCode: 429);
        await updater.CheckForUpdateAsync(forceRefresh: forceRefresh);
        return Results.Ok(updater.BuildMetaResponse(cfg.WebUI));
    });

    // Web Status — matches TypeScript StatusResponse (no extra webui field)
    app.MapGet("/web/status", async (TorrentarrConfig cfg, ITorrentClientRegistry registry, CancellationToken ct) =>
    {
        var qbitInstances = await TorrentClientApiBuilder.StatusAsync(cfg, registry, ct);
        return Results.Ok(new
        {
            qbitInstances,
            arrs = cfg.ArrInstances.Select(kvp => new
            {
                category = kvp.Value.Category,
                name = kvp.Key,
                type = kvp.Value.Type,
                alive = kvp.Value.URI != "CHANGE_ME"
            }).ToList(),
            ready = true
        });
    });

    // Web Qbit Categories — full QbitCategory shape
    // Only returns categories that are configured to be monitored:
    //   • cfg.QBit.ManagedCategories  (qBit-managed)
    //   • each Arr instance's Category (Arr-managed)
    // The "instance" field is always the qBit instance name (never the Arr instance name)
    // so that ProcessesView can match categories to the correct qBit process card.
    app.MapGet("/web/qbit/categories", async (ITorrentClientRegistry registry, TorrentarrConfig cfg, CancellationToken ct) =>
    {
        var categories = await TorrentClientApiBuilder.CategoriesAsync(cfg, registry, ct: ct);
        return Results.Ok(new { categories, ready = true });
    });

    // Web Processes — host tasks and real worker heartbeats; clients use /web/status.
    app.MapGet("/web/processes", async (ProcessStateManager stateMgr, TorrentarrConfig cfg, WorkerProcessSupervisor workers) =>
    {
        var processes = stateMgr.GetAll().Select(s => new
        {
            category = s.Category,
            name = s.Name,
            kind = s.Kind,
            pid = s.Pid,
            alive = s.Alive,
            rebuilding = s.Rebuilding,
            searchSummary = s.SearchSummary,
            searchTimestamp = s.SearchTimestamp,
            queueCount = s.QueueCount,
            categoryCount = s.CategoryCount,
            metricType = s.MetricType,
            freeSpacePaused = s.MetricType == "free-space" ? s.CategoryCount : null,
            restartable = false,
            status = s.Status
        }).ToList<object>();

        foreach (var status in (await workers.GetProcessStatusAsync()).Values)
            processes.Add(new { category = cfg.ArrInstances.GetValueOrDefault(status.Name)?.Category ?? status.Name, name = status.Name, kind = status.Kind, pid = status.ProcessId, alive = status.IsAlive, rebuilding = false, searchSummary = status.SearchSummary, searchTimestamp = status.SearchTimestamp, queueCount = status.QueueCount, categoryCount = status.CategoryCount, metricType = status.MetricType, status = status.Status ?? (status.IsAlive ? "Running" : "Stopped"), restartKey = status.Name, restartable = true, torrent = status.Torrent, search = status.Search });

        return Results.Ok(new { processes });
    });

    // Web Restart Process — stops and restarts the named instance worker (kind is advisory; one loop per Arr)
    app.MapPost("/web/processes/{category}/{kind}/restart", async (string category, string kind, TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        var kindNorm = (kind ?? "").Trim().ToLowerInvariant();
        if (kindNorm != "search" && kindNorm != "torrent" && kindNorm != "category" && kindNorm != "arr" && kindNorm != "worker")
            return Results.BadRequest(new { error = "kind must be search, torrent, category, arr, or worker" });

        var instanceName = cfg.ArrInstances.ContainsKey(category) ? category : cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { status = "restarted", restarted = instanceName != null ? new[] { instanceName } : Array.Empty<string>() });
    });

    // Web Restart All Processes
    app.MapPost("/web/processes/restart_all", async (TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    // Web Arr Rebuild — same shape as RestartResponse
    app.MapPost("/web/arr/rebuild", async (TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    // Web Log Level — actually changes the Serilog level at runtime
    app.MapPost("/web/loglevel", (LoggerConfigurationRequest req, LoggingLevelSwitch ls, TorrentarrConfig cfg) =>
    {
        ls.MinimumLevel = req.Level?.ToUpperInvariant() switch
        {
            "DEBUG" or "VERBOSE" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
        foreach (var instanceName in cfg.ArrInstances.Keys)
            File.WriteAllText(Path.Combine(logsPath, $"worker-{instanceName}.loglevel"), ls.MinimumLevel.ToString());
        return Results.Ok(new { success = true, level = ls.MinimumLevel.ToString() });
    });

    // Web Logs List — returns name, size, and last-modified for each .log file
    app.MapGet("/web/logs", () =>
    {
        var files = new List<object>();
        if (Directory.Exists(logsPath))
        {
            foreach (var file in Directory.GetFiles(logsPath, "*.log").OrderByDescending(f => f))
            {
                var fi = new FileInfo(file);
                files.Add(new { name = fi.Name, size = fi.Length, modified = fi.LastWriteTimeUtc.ToString("o") });
            }
        }
        return Results.Ok(new { files });
    });

    // Web Log Tail — last 1000 lines, plain text so frontend res.text() gets unquoted content
    app.MapGet("/web/logs/{name}", async (string name, HttpRequest request) =>
    {
        if (request.Query["format"] == "json")
            return await LogFileApi.TailFromRequestAsync(logsPath, name, request, request.HttpContext.RequestAborted);

        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        var logFile = Path.Combine(logsPath, name);
        if (!File.Exists(logFile))
            return Results.NotFound(new { error = "Log file not found" });

        var content = await TailLogFileAsync(logFile, 1000);
        return Results.Text(content, "text/plain");
    });

    // Web Log Download
    app.MapGet("/web/logs/{name}/download", (string name) =>
    {
        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        var logFile = Path.Combine(logsPath, name);
        if (!File.Exists(logFile))
            return Results.NotFound();
        return Results.File(logFile, "text/plain", name);
    });

    app.MapGet("/web/logs/{name}/search", (string name, HttpRequest request) =>
    {
        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        return LogFileApi.SearchFromRequest(logsPath, name, request);
    });

    app.MapGet("/web/logs/{name}/stream", async (string name, HttpContext ctx) =>
    {
        if (!IsValidLogFileName(name))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid log file name" });
            return;
        }
        await LogFileApi.StreamAsync(ctx, logsPath, name, ctx.RequestAborted);
    });

    app.MapGet("/web/config/schema", () => Results.Ok(ConfigSchemaBuilder.Build()));

    app.MapGet("/web/qbit/overview", async (HttpRequest request, TorrentarrConfig cfg, ITorrentClientRegistry qbitManager) =>
    {
        var instance = request.Query["instance"].FirstOrDefault();
        return Results.Ok(await QbitOverviewBuilder.BuildAsync(cfg, qbitManager, instance));
    });

    // Web Arr List
    app.MapGet("/web/arr", async (TorrentarrConfig cfg, CatalogRollupService rollups) =>
    {
        var arr = cfg.ArrInstances.Select(kvp => new
        {
            category = kvp.Value.Category,
            name = kvp.Key,
            type = kvp.Value.Type,
            alive = kvp.Value.URI != "CHANGE_ME"
        }).ToList();

        var (radarr, sonarr, lidarr, readarr) = await rollups.GetAggregatedTypeCountsAsync(cfg);
        var counts = new
        {
            radarr = new { available = radarr.Available, monitored = radarr.Monitored, missing = radarr.Missing },
            sonarr = new { available = sonarr.Available, monitored = sonarr.Monitored, missing = sonarr.Missing },
            lidarr = new { available = lidarr.Available, monitored = lidarr.Monitored, missing = lidarr.Missing },
            readarr = new { available = readarr.Available, monitored = readarr.Monitored, missing = readarr.Missing }
        };

        return Results.Ok(new { arr, ready = true, counts });
    });


    // Web Sonarr Series — seasons populated from episodes table
    app.MapGet("/web/sonarr/{category}/series", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, CatalogRollupService rollups, int? page, int? page_size, string? q, int? missing, string? reason) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Series.Where(s => keys.Contains(s.ArrInstance));
        var query = baseQuery;

        var reasonFilter = string.IsNullOrWhiteSpace(reason) || reason.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : reason.Trim();
        if (missing == 1 || reasonFilter is not null)
        {
            var episodeQuery = db.Episodes.Where(e => keys.Contains(e.ArrInstance));
            if (missing == 1)
                episodeQuery = episodeQuery.Where(e => e.EpisodeFileId == null || e.EpisodeFileId == 0);
            if (reasonFilter is not null)
            {
                if (reasonFilter.Equals("Not being searched", StringComparison.OrdinalIgnoreCase))
                    episodeQuery = episodeQuery.Where(e => e.Reason == null || e.Reason == "Not being searched");
                else
                    episodeQuery = episodeQuery.Where(e => e.Reason == reasonFilter);
            }
            var seriesIdsWithMatchingEpisodes = await episodeQuery
                .Select(e => e.SeriesId)
                .Distinct()
                .ToListAsync();
            baseQuery = baseQuery.Where(s => seriesIdsWithMatchingEpisodes.Contains(s.EntryId));
            query = baseQuery;
        }

        if (!string.IsNullOrEmpty(q))
            query = query.Where(s => s.Title != null && s.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var (episodeRollups, _) = await rollups.GetSonarrRollupsAsync(keys);

        var seriesPage = await query
            .OrderBy(s => s.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(s => new { s.EntryId, s.Title, s.Monitored, s.QualityProfileId, s.QualityProfileName })
            .ToListAsync();

        var seriesIds = seriesPage.Select(s => s.EntryId).ToList();

        var episodes = await db.Episodes
            .Where(e => keys.Contains(e.ArrInstance) && seriesIds.Contains(e.SeriesId))
            .Select(e => new
            {
                e.SeriesId,
                e.SeasonNumber,
                e.EpisodeNumber,
                e.Title,
                e.Monitored,
                e.EpisodeFileId,
                e.AirDateUtc,
                e.Reason
            })
            .ToListAsync();
        var seasonGroups = episodes.GroupBy(e => new { e.SeriesId, e.SeasonNumber }).Select(g => new
        {
            g.Key.SeriesId,
            g.Key.SeasonNumber,
            TotalCount = g.Count(),
            HasFileCount = g.Count(e => e.EpisodeFileId != null && e.EpisodeFileId != 0),
            MonitoredCount = g.Count(e => e.Monitored == true),
            Episodes = g.Select(e => new { episodeNumber = e.EpisodeNumber, title = e.Title, monitored = e.Monitored, hasFile = e.EpisodeFileId != null && e.EpisodeFileId != 0, airDateUtc = e.AirDateUtc, reason = e.Reason }).ToArray()
        }).ToList();

        var seriesList = seriesPage.Select(s =>
        {
            var seriesSeasonGroups = seasonGroups.Where(g => g.SeriesId == s.EntryId).ToList();
            var seriesAvailable = seriesSeasonGroups.Sum(g => g.HasFileCount);
            var seriesMonitored = seriesSeasonGroups.Sum(g => g.MonitoredCount);
            var seriesTotal = seriesSeasonGroups.Sum(g => g.TotalCount);

            var seasons = seriesSeasonGroups
                .ToDictionary(
                    g => g.SeasonNumber.ToString(),
                    g => (object)new
                    {
                        monitored = g.MonitoredCount,
                        available = g.HasFileCount,
                        missing = g.TotalCount - g.HasFileCount,
                        episodes = g.Episodes
                    });

            return new
            {
                series = new
                {
                    id = s.EntryId,
                    title = s.Title,
                    monitored = s.Monitored,
                    qualityProfileId = s.QualityProfileId,
                    qualityProfileName = s.QualityProfileName
                },
                totals = new { available = seriesAvailable, monitored = seriesMonitored, missing = seriesTotal - seriesAvailable },
                seasons
            };
        }).ToList();

        return Results.Ok(new
        {
            category,
            total,
            page = currentPage,
            page_size = currentPageSize,
            counts = new
            {
                available = episodeRollups.Available,
                monitored = episodeRollups.Monitored,
                missing = episodeRollups.Missing
            },
            series = seriesList
        });
    });

    // Web Lidarr Albums — tracks populated from tracks table
    app.MapGet("/web/lidarr/{category}/albums", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, CatalogRollupService rollups, int? page, int? page_size, string? q, bool? monitored, bool? has_file, bool? quality_met, bool? is_request, bool? flat_mode) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Albums.Where(a => keys.Contains(a.ArrInstance));
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(a => a.Title.Contains(q));
        if (monitored.HasValue)
            query = query.Where(a => a.Monitored == monitored.Value);
        if (has_file.HasValue)
            query = query.Where(a => has_file.Value ? a.AlbumFileId != 0 : a.AlbumFileId == 0);
        if (quality_met.HasValue)
            query = query.Where(a => a.QualityMet == quality_met.Value);
        if (is_request.HasValue)
            query = query.Where(a => a.IsRequest == is_request.Value);

        // flat_mode=true: return tracks instead of album-grouped response
        if (flat_mode == true)
        {
            var (_, _, trackRollupsFlat) = await rollups.GetLidarrRollupsAsync(keys);
            var trackBaseQuery = db.Tracks.Where(t => keys.Contains(t.ArrInstance));
            var trackTotal = await trackBaseQuery.CountAsync();
            var trackAvailable = trackRollupsFlat.Available;
            var trackMonitored = trackRollupsFlat.Monitored;
            var tracksFlat = await (
                from t in trackBaseQuery
                join a in db.Albums on t.AlbumId equals a.EntryId into aj
                from album in aj.DefaultIfEmpty()
                orderby t.TrackNumber
                select new
                {
                    id = t.EntryId,
                    trackNumber = t.TrackNumber,
                    title = t.Title,
                    hasFile = t.HasFile,
                    duration = t.Duration,
                    monitored = t.Monitored,
                    trackFileId = t.TrackFileId,
                    albumId = t.AlbumId,
                    albumTitle = album != null ? album.Title : null,
                    artistTitle = album != null ? album.ArtistTitle : null,
                    artistId = album != null ? (int?)album.ArtistId : null
                })
                .Skip(skip).Take(currentPageSize).ToListAsync();
            return Results.Ok(new
            {
                category,
                counts = new { available = trackAvailable, monitored = trackMonitored },
                total = trackTotal,
                page = currentPage,
                page_size = currentPageSize,
                tracks = tracksFlat
            });
        }

        var total = await baseQuery.CountAsync();
        var (albumRollups, _, trackRollups) = await rollups.GetLidarrRollupsAsync(keys);

        var albumPage = await query
            .OrderBy(a => a.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(a => new
            {
                a.EntryId,
                a.Title,
                a.ArtistId,
                a.ArtistTitle,
                a.ReleaseDate,
                a.Monitored,
                a.AlbumFileId,
                a.Reason,
                a.QualityProfileId,
                a.QualityProfileName
            })
            .ToListAsync();

        var albumIds = albumPage.Select(a => a.EntryId).ToList();

        var tracksForPage = await db.Tracks
            .Where(t => keys.Contains(t.ArrInstance) && albumIds.Contains(t.AlbumId))
            .OrderBy(t => t.TrackNumber)
            .Select(t => new
            {
                albumId = t.AlbumId,
                id = t.EntryId,
                trackNumber = t.TrackNumber,
                title = t.Title,
                hasFile = t.HasFile,
                duration = t.Duration,
                monitored = t.Monitored,
                trackFileId = t.TrackFileId
            })
            .ToListAsync();

        var albums = albumPage.Select(a => new
        {
            album = new
            {
                id = a.EntryId,
                title = a.Title,
                artistId = a.ArtistId,
                artistName = a.ArtistTitle,
                releaseDate = a.ReleaseDate,
                monitored = a.Monitored,
                hasFile = a.AlbumFileId != 0,
                reason = a.Reason,
                qualityProfileId = a.QualityProfileId,
                qualityProfileName = a.QualityProfileName
            },
            totals = new
            {
                available = tracksForPage.Count(t => t.albumId == a.EntryId && t.hasFile),
                monitored = a.Monitored ? 1 : 0,
                missing = tracksForPage.Count(t => t.albumId == a.EntryId && !t.hasFile)
            },
            tracks = tracksForPage.Where(t => t.albumId == a.EntryId).Cast<object>().ToList()
        }).ToList();

        return Results.Ok(new
        {
            category,
            counts = new
            {
                available = albumRollups.Available,
                monitored = albumRollups.Monitored,
                missing = albumRollups.Missing,
                quality_met = albumRollups.QualityMet,
                requests = albumRollups.Requests
            },
            counts_tracks = new
            {
                available = trackRollups.Available,
                monitored = trackRollups.Monitored,
                missing = trackRollups.Missing
            },
            total,
            page = currentPage,
            page_size = currentPageSize,
            albums
        });
    });

    // Web Lidarr Tracks — paginated flat track list for a Lidarr instance
    app.MapGet("/web/lidarr/{category}/tracks", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, int? page, int? page_size, string? q) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Tracks.Where(t => keys.Contains(t.ArrInstance));
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(t => t.Title != null && t.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var availableCount = await baseQuery.CountAsync(t => t.HasFile);
        var monitoredCount = await baseQuery.CountAsync(t => t.Monitored);

        // Left join with albums for album/artist context
        var tracksPage = await (
            from t in query
            join a in db.Albums on t.AlbumId equals a.EntryId into aj
            from album in aj.DefaultIfEmpty()
            orderby t.TrackNumber
            select new
            {
                id = t.EntryId,
                trackNumber = t.TrackNumber,
                title = t.Title,
                hasFile = t.HasFile,
                duration = t.Duration,
                monitored = t.Monitored,
                trackFileId = t.TrackFileId,
                albumId = t.AlbumId,
                albumTitle = album != null ? album.Title : null,
                artistTitle = album != null ? album.ArtistTitle : null,
                artistId = album != null ? (int?)album.ArtistId : null
            })
            .Skip(skip)
            .Take(currentPageSize)
            .ToListAsync();

        return Results.Ok(new
        {
            category,
            counts = new { available = availableCount, monitored = monitoredCount, missing = total - availableCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            tracks = tracksPage
        });
    });

    // Web Arr Restart
    app.MapPost("/web/arr/{category}/restart", async (string category, TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        var instanceName = cfg.ArrInstances.ContainsKey(category) ? category : cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { success = instanceName != null, message = instanceName != null ? $"Restarted {instanceName}" : $"No worker found for category '{category}'" });
    });

    // Web Config Get — return a FLAT structure matching Python qBitrr's config format.
    // ConfigView.tsx expects all sections at the top level (e.g. "Radarr-1080", "qBit"),
    // NOT nested under "ArrInstances" / "QBit". Keys use PascalCase to match field paths.
    app.MapGet("/web/config", (TorrentarrConfig cfg) =>
    {
        var flat = new Dictionary<string, object?>();
        flat["Settings"] = cfg.Settings;
        flat["WebUI"] = cfg.WebUI;
        foreach (var (key, qbit) in cfg.QBitInstances.Where(kv => kv.Value.Host != "CHANGE_ME"))
            flat[key] = qbit;
        flat["TorrentClient"] = cfg.TorrentClients;
        foreach (var (key, arr) in cfg.ArrInstances)
            flat[key] = arr;

        var jsonSettings = new Newtonsoft.Json.JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
            NullValueHandling = Newtonsoft.Json.NullValueHandling.Include,
        };
        // Serialize then redact sensitive keys (API keys, passwords, tokens) before sending to frontend
        var jObj = Newtonsoft.Json.Linq.JObject.FromObject(flat, Newtonsoft.Json.JsonSerializer.Create(jsonSettings));
        var redacted = StripSensitiveKeys(jObj);

        // Config version mismatch warning (qBitrr parity): return { config, warning } so frontend can show toast
        var validation = ConfigurationLoader.ValidateConfigVersion(cfg);
        if (!validation.IsValid && validation.Message != null)
            return Results.Json(new { config = redacted, warning = new { type = "config_version_mismatch", message = validation.Message, currentVersion = validation.CurrentVersion } });

        return Results.Content(redacted.ToString(Newtonsoft.Json.Formatting.None), "application/json");
    });

    // Web Config Update — frontend sends { changes: { "Section.Key": value, ... } } (dotted keys).
    // ConfigView.tsx flatten()s the hierarchical config into dotted paths before sending only the
    // changed keys.  We apply those changes onto the current in-memory config and save.
    app.MapPost("/web/config", async (HttpRequest request, TorrentarrConfig cfg, ConfigurationLoader loader, WorkerProcessSupervisor workerMgr, QBitCategoryWorkerManager qbitCategoryMgr, ITorrentClientRegistry registry, TorrentarrDbContext db, IServiceProvider services) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (!payload.TryGetProperty("changes", out var changesEl))
                return Results.BadRequest(new { error = "Missing 'changes' field" });

            var changesObj = Newtonsoft.Json.Linq.JObject.Parse(changesEl.GetRawText());
            var renames = payload.TryGetProperty("renames", out var renamesEl)
                ? Newtonsoft.Json.Linq.JObject.Parse(renamesEl.GetRawText()) : null;
            ConfigRenameHelper.PreserveSecrets(cfg, changesObj, renames);
            var (updatedConfig, applyError) = ApplyDottedConfigChanges(cfg, changesObj);
            if (applyError != null)
                return applyError;
            if (updatedConfig == null)
                return Results.BadRequest(new { error = "Invalid config payload" });

            var (valid, validationError) = ConfigValidationHelper.ValidateAll(updatedConfig);
            if (!valid)
                return Results.BadRequest(new { error = validationError });

            return await SaveAndRespondConfigUpdate(cfg, updatedConfig, loader, workerMgr, qbitCategoryMgr, registry, db, services, renames);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    // §6.10: POST /web/update — trigger binary download + in-place apply
    app.MapPost("/web/update", async (UpdateService updater, IHostApplicationLifetime lifetime) =>
    {
        if (updater.ApplyState.InProgress)
            return Results.Ok(new { success = false, message = "Update already in progress" });

        // Ensure we have a fresh check before applying
        await updater.CheckForUpdateAsync();
        await updater.ApplyUpdateAsync(lifetime);
        if (!updater.ApplyState.InProgress && updater.ApplyState.LastResult == "error")
            return Results.Ok(new { success = false, message = updater.ApplyState.LastError });
        return Results.Ok(new { success = true, message = "Update started — application will restart when complete" });
    });

    // §6.10: GET /web/download-update — return download URL/name/size for the latest binary
    app.MapGet("/web/download-update", async (UpdateService updater) =>
    {
        await updater.CheckForUpdateAsync();
        var meta = updater.BuildMetaResponse();
        // Reflect to extract binary fields from the anonymous type
        var t = meta.GetType();
        return Results.Ok(new
        {
            download_url = (string?)t.GetProperty("binary_download_url")?.GetValue(meta),
            download_name = (string?)t.GetProperty("binary_download_name")?.GetValue(meta),
            download_size = (long?)t.GetProperty("binary_download_size")?.GetValue(meta),
            worker_download_url = (string?)t.GetProperty("worker_download_url")?.GetValue(meta),
            worker_download_name = (string?)t.GetProperty("worker_download_name")?.GetValue(meta),
            error = (string?)t.GetProperty("binary_download_error")?.GetValue(meta)
        });
    });

    // Web Test Arr Connection (no auth — frontend uses this directly)
    app.MapPost("/web/arr/test-connection", (TestConnectionRequest req, TorrentarrConfig cfg) =>
        HandleTestConnection(req, cfg));

    // Web Torrents Distribution — count media items per qBit category per Arr instance
    app.MapGet("/web/torrents/distribution", async (TorrentarrConfig cfg, TorrentarrDbContext db) =>
    {
        var distribution = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (name, instanceCfg) in cfg.ArrInstances)
        {
            if (string.IsNullOrEmpty(instanceCfg.Category)) continue;
            if (!distribution.ContainsKey(instanceCfg.Category))
                distribution[instanceCfg.Category] = new Dictionary<string, int>();
            var count = instanceCfg.Type.ToLowerInvariant() switch
            {
                "radarr" => await db.Movies.CountAsync(m => m.ArrInstance == name),
                "sonarr" => await db.Episodes.CountAsync(e => e.ArrInstance == name),
                "lidarr" => await db.Tracks.CountAsync(t => t.ArrInstance == name),
                "readarr" => await db.Books.CountAsync(b => b.ArrInstance == name),
                _ => 0
            };
            distribution[instanceCfg.Category][name] = count;
        }
        return Results.Ok(new { distribution });
    });

    // Web Token — only returned when already authenticated (middleware enforces when auth enabled)
    app.MapGet("/web/token", (TorrentarrConfig cfg, HttpContext ctx) =>
    {
        var isAuthenticated = ctx.User?.Identity?.IsAuthenticated == true;
        if (!isAuthenticated && IsAuthRequired(cfg))
            return Results.Json(new { token = "" }, statusCode: 401);
        return Results.Ok(new { token = cfg.WebUI.Token });
    });

    // Local login: username + password → session cookie
    app.MapPost("/web/login", async (HttpContext ctx, TorrentarrConfig cfg, IPasswordHasher hasher, ILogger<Program> log) =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (ip != null && !LoginRateLimiter.TryAcquire(ip))
            return Results.Json(new { error = "Too many login attempts. Try again later." }, statusCode: 429);

        var body = await ctx.Request.ReadFromJsonAsync<LoginRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "Username and password required" });

        if (!cfg.WebUI.LocalAuthEnabled)
            return Results.Json(new { error = "Local login not configured" }, statusCode: 400);

        if (string.IsNullOrEmpty(cfg.WebUI.PasswordHash))
            return Results.Json(new { error = "Password not set", code = "SETUP_REQUIRED" }, statusCode: 403);

        // Always run bcrypt verification to avoid timing leak (username enumeration)
        var passwordValid = hasher.VerifyPassword(body.Password, cfg.WebUI.PasswordHash);
        var usernameMatch = string.Equals(body.Username.Trim(), cfg.WebUI.Username?.Trim(), StringComparison.Ordinal);
        if (!usernameMatch || !passwordValid)
            return Results.Json(new { error = "Unauthorized" }, statusCode: 401);

        var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, body.Username.Trim()));
        var principal = new ClaimsPrincipal(identity);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(7)
        });
        log.LogInformation("User {User} logged in via local auth", body.Username);
        return Results.Ok(new { success = true });
    });

    // Logout: sign out cookie and redirect to login (GET or POST for link/form compatibility).
    app.MapGet("/web/logout", async (HttpContext ctx, TorrentarrConfig cfg) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect(UrlBaseHelper.WithUrlBase(
            UrlBaseHelper.NormalizeUrlBase(cfg.WebUI.UrlBase), "/login"), false);
    });
    app.MapPost("/web/logout", async (HttpContext ctx, TorrentarrConfig cfg) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect(UrlBaseHelper.WithUrlBase(
            UrlBaseHelper.NormalizeUrlBase(cfg.WebUI.UrlBase), "/login"), false);
    });

    // Set password (first-time or reset): hash and write to config. Allowed when PasswordHash is empty or via setup token.
    app.MapPost("/web/auth/set-password", async (HttpContext ctx, TorrentarrConfig cfg, ConfigurationLoader loader, IPasswordHasher hasher, ILogger<Program> log) =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.ToString();
        if (ip != null && !SetPasswordRateLimiter.TryAcquire(ip))
            return Results.Json(new { error = "Too many set-password attempts. Try again later." }, statusCode: 429);

        var body = await ctx.Request.ReadFromJsonAsync<SetPasswordRequest>();
        if (body == null || string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Password))
            return Results.BadRequest(new { error = "Username and password required" });
        if (body.Password.Length < 8)
            return Results.BadRequest(new { error = "Password must be at least 8 characters" });

        var bearerToken = ctx.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
        var queryToken = ctx.Request.Query["token"].FirstOrDefault();
        var suppliedToken = !string.IsNullOrWhiteSpace(bearerToken) ? bearerToken : queryToken;
        var isAuthenticated = ctx.User.Identity?.IsAuthenticated == true;
        if (!WebUIAuthHelpers.IsSetPasswordAllowed(cfg, body.SetupToken, isAuthenticated, suppliedToken))
        {
            var firstTime = string.IsNullOrEmpty(cfg.WebUI.PasswordHash);
            return Results.Json(new
            {
                error = firstTime
                    ? "Setup token required. Use TORRENTARR_SETUP_TOKEN or the WebUI.Token value from config.toml."
                    : "Set password not allowed"
            }, statusCode: 403);
        }

        // Capture current values so we can revert if SaveConfig fails
        var prevUsername = cfg.WebUI.Username;
        var prevPasswordHash = cfg.WebUI.PasswordHash;
        var prevAuthDisabled = cfg.WebUI.AuthDisabled;
        var prevLocalAuthEnabled = cfg.WebUI.LocalAuthEnabled;

        cfg.WebUI.Username = body.Username.Trim();
        cfg.WebUI.PasswordHash = hasher.HashPassword(body.Password);
        // Always enable local auth after setting a password, regardless of previous mode
        if (cfg.WebUI.AuthDisabled)
            cfg.WebUI.AuthDisabled = false;
        cfg.WebUI.LocalAuthEnabled = true;
        try
        {
            loader.SaveConfig(cfg);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to save config after set-password");
            // Revert in-memory config so it stays in sync with persisted file
            cfg.WebUI.Username = prevUsername;
            cfg.WebUI.PasswordHash = prevPasswordHash;
            cfg.WebUI.AuthDisabled = prevAuthDisabled;
            cfg.WebUI.LocalAuthEnabled = prevLocalAuthEnabled;
            return Results.Json(new { error = "Failed to save configuration" }, statusCode: 500);
        }
        log.LogInformation("Password set for user {User}", cfg.WebUI.Username);
        return Results.Ok(new { success = true });
    });

    // OIDC challenge: redirect to IdP (used by login page "Sign in with OIDC" button)
    app.MapGet("/web/auth/oidc/challenge", async (HttpContext ctx, TorrentarrConfig cfg) =>
    {
        if (!cfg.WebUI.OIDCEnabled || cfg.WebUI.OIDC is not { } oidc
            || string.IsNullOrWhiteSpace(oidc.Authority) || string.IsNullOrWhiteSpace(oidc.ClientId))
            return Results.BadRequest(new { error = "OIDC not configured" });
        await ctx.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme);
        // ChallengeAsync already wrote the 302 response; do not return a result that writes (would conflict).
        return NoOpAfterChallengeResult.Instance;
    });

    // Web Qbit Categories (api mirror — same logic as /web/qbit/categories, token-protected)
    app.MapGet("/api/qbit/categories", async (ITorrentClientRegistry registry, TorrentarrConfig cfg, CancellationToken ct) =>
    {
        var categories = await TorrentClientApiBuilder.CategoriesAsync(cfg, registry, ct: ct);
        return Results.Ok(new { categories, ready = true });
    });

    // ==================== /api/* endpoints (Bearer token protected via middleware) ====================

    app.MapGet("/api/meta", async (HttpContext ctx, UpdateService updater, TorrentarrConfig cfg, int? force) =>
    {
        var forceRefresh = force.GetValueOrDefault() != 0;
        if (forceRefresh && !MetaForceAllowed(ctx, cfg))
            return Results.Json(new { error = "rate_limited", message = "Too many force refresh requests" }, statusCode: 429);
        await updater.CheckForUpdateAsync(forceRefresh: forceRefresh);
        return Results.Ok(updater.BuildMetaResponse());
    });

    app.MapGet("/api/status", async (TorrentarrConfig cfg, ITorrentClientRegistry registry, CancellationToken ct) =>
    {
        var qbitInstances = await TorrentClientApiBuilder.StatusAsync(cfg, registry, ct);
        return Results.Ok(new
        {
            qbitInstances,
            arrs = cfg.ArrInstances.Select(kvp => new
            {
                category = kvp.Value.Category,
                name = kvp.Key,
                type = kvp.Value.Type,
                alive = kvp.Value.URI != "CHANGE_ME"
            }).ToList(),
            ready = true
        });
    });

    app.MapGet("/api/processes", async (ProcessStateManager stateMgr, TorrentarrConfig cfg, WorkerProcessSupervisor workers) =>
    {
        var processes = stateMgr.GetAll().Select(s => new
        {
            category = s.Category,
            name = s.Name,
            kind = s.Kind,
            pid = s.Pid,
            alive = s.Alive,
            rebuilding = s.Rebuilding,
            searchSummary = s.SearchSummary,
            searchTimestamp = s.SearchTimestamp,
            queueCount = s.QueueCount,
            categoryCount = s.CategoryCount,
            metricType = s.MetricType,
            freeSpacePaused = s.MetricType == "free-space" ? s.CategoryCount : null,
            restartable = false,
            status = s.Status
        }).ToList<object>();
        foreach (var status in (await workers.GetProcessStatusAsync()).Values)
            processes.Add(new { category = cfg.ArrInstances.GetValueOrDefault(status.Name)?.Category ?? status.Name, name = status.Name, kind = status.Kind, pid = status.ProcessId, alive = status.IsAlive, rebuilding = false, searchSummary = status.SearchSummary, searchTimestamp = status.SearchTimestamp, queueCount = status.QueueCount, categoryCount = status.CategoryCount, metricType = status.MetricType, status = status.Status ?? (status.IsAlive ? "Running" : "Stopped"), restartKey = status.Name, restartable = true, torrent = status.Torrent, search = status.Search });
        return Results.Ok(new { processes });
    });

    app.MapPost("/api/processes/{category}/{kind}/restart", async (string category, string kind, TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        var kindNorm = (kind ?? "").Trim().ToLowerInvariant();
        if (kindNorm != "search" && kindNorm != "torrent" && kindNorm != "category" && kindNorm != "arr" && kindNorm != "worker")
            return Results.BadRequest(new { error = "kind must be search, torrent, category, arr, or worker" });

        var instanceName = cfg.ArrInstances.ContainsKey(category) ? category : cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { status = "restarted", restarted = instanceName != null ? new[] { instanceName } : Array.Empty<string>() });
    });

    app.MapPost("/api/processes/restart_all", async (TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    app.MapPost("/api/arr/rebuild", async (TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    app.MapPost("/api/loglevel", (LoggerConfigurationRequest req, LoggingLevelSwitch ls, TorrentarrConfig cfg) =>
    {
        ls.MinimumLevel = req.Level?.ToUpperInvariant() switch
        {
            "DEBUG" or "VERBOSE" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
        foreach (var instanceName in cfg.ArrInstances.Keys)
            File.WriteAllText(Path.Combine(logsPath, $"worker-{instanceName}.loglevel"), ls.MinimumLevel.ToString());
        return Results.Ok(new { success = true, level = ls.MinimumLevel.ToString() });
    });

    app.MapGet("/api/logs", () =>
    {
        var files = new List<string>();
        if (Directory.Exists(logsPath))
        {
            foreach (var file in Directory.GetFiles(logsPath, "*.log").OrderByDescending(f => f))
                files.Add(Path.GetFileName(file));
        }
        return Results.Ok(new { files });
    });

    app.MapGet("/api/logs/{name}", async (string name, HttpRequest request) =>
    {
        if (request.Query["format"] == "json")
            return await LogFileApi.TailFromRequestAsync(logsPath, name, request, request.HttpContext.RequestAborted);

        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        var logFile = Path.Combine(logsPath, name);
        if (!File.Exists(logFile))
            return Results.NotFound(new { error = "Log file not found" });
        var lines = await File.ReadAllLinesAsync(logFile);
        return Results.Text(string.Join("\n", lines.TakeLast(500)), "text/plain");
    });

    app.MapGet("/api/logs/{name}/download", (string name) =>
    {
        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        var logFile = Path.Combine(logsPath, name);
        if (!File.Exists(logFile))
            return Results.NotFound();
        return Results.File(logFile, "text/plain", name);
    });

    app.MapGet("/api/logs/{name}/search", (string name, HttpRequest request) =>
    {
        if (!IsValidLogFileName(name))
            return Results.BadRequest(new { error = "Invalid log file name" });
        return LogFileApi.SearchFromRequest(logsPath, name, request);
    });

    app.MapGet("/api/logs/{name}/stream", async (string name, HttpContext ctx) =>
    {
        if (!IsValidLogFileName(name))
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(new { error = "Invalid log file name" });
            return;
        }
        await LogFileApi.StreamAsync(ctx, logsPath, name, ctx.RequestAborted);
    });

    app.MapGet("/api/config/schema", () => Results.Ok(ConfigSchemaBuilder.Build()));

    app.MapGet("/api/arr", async (TorrentarrConfig cfg, CatalogRollupService rollups) =>
    {
        var arr = cfg.ArrInstances.Select(kvp => new
        {
            category = kvp.Value.Category,
            name = kvp.Key,
            type = kvp.Value.Type,
            alive = kvp.Value.URI != "CHANGE_ME"
        }).ToList();

        var (radarr, sonarr, lidarr, readarr) = await rollups.GetAggregatedTypeCountsAsync(cfg);
        var counts = new
        {
            radarr = new { available = radarr.Available, monitored = radarr.Monitored, missing = radarr.Missing },
            sonarr = new { available = sonarr.Available, monitored = sonarr.Monitored, missing = sonarr.Missing },
            lidarr = new { available = lidarr.Available, monitored = lidarr.Monitored, missing = lidarr.Missing },
            readarr = new { available = readarr.Available, monitored = readarr.Monitored, missing = readarr.Missing }
        };

        return Results.Ok(new { arr, ready = true, counts });
    });

    app.MapPost("/api/arr/{section}/restart", async (string section, TorrentarrConfig cfg, WorkerProcessSupervisor workerMgr) =>
    {
        var instanceName = cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(section, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { success = instanceName != null, message = instanceName != null ? $"Restarted {instanceName}" : $"No worker found for category '{section}'" });
    });



    app.MapGet("/api/sonarr/{category}/series", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, CatalogRollupService rollups, int? page, int? page_size, string? q, int? missing, string? reason) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Series.Where(s => keys.Contains(s.ArrInstance));
        var query = baseQuery;

        var reasonFilter = string.IsNullOrWhiteSpace(reason) || reason.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : reason.Trim();
        if (missing == 1 || reasonFilter is not null)
        {
            var episodeQuery = db.Episodes.Where(e => keys.Contains(e.ArrInstance));
            if (missing == 1)
                episodeQuery = episodeQuery.Where(e => e.EpisodeFileId == null || e.EpisodeFileId == 0);
            if (reasonFilter is not null)
            {
                if (reasonFilter.Equals("Not being searched", StringComparison.OrdinalIgnoreCase))
                    episodeQuery = episodeQuery.Where(e => e.Reason == null || e.Reason == "Not being searched");
                else
                    episodeQuery = episodeQuery.Where(e => e.Reason == reasonFilter);
            }
            var seriesIdsWithMatchingEpisodes = await episodeQuery
                .Select(e => e.SeriesId)
                .Distinct()
                .ToListAsync();
            baseQuery = baseQuery.Where(s => seriesIdsWithMatchingEpisodes.Contains(s.EntryId));
            query = baseQuery;
        }

        if (!string.IsNullOrEmpty(q))
            query = query.Where(s => s.Title != null && s.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var (episodeRollupsApi, _) = await rollups.GetSonarrRollupsAsync(keys);

        var seriesPage = await query
            .OrderBy(s => s.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(s => new { s.EntryId, s.Title, s.Monitored, s.QualityProfileId, s.QualityProfileName })
            .ToListAsync();

        var seriesIds = seriesPage.Select(s => s.EntryId).ToList();

        var episodes = await db.Episodes
            .Where(e => keys.Contains(e.ArrInstance) && seriesIds.Contains(e.SeriesId))
            .Select(e => new
            {
                e.SeriesId,
                e.SeasonNumber,
                e.EpisodeNumber,
                e.Title,
                e.Monitored,
                e.EpisodeFileId,
                e.AirDateUtc,
                e.Reason
            })
            .ToListAsync();
        var seasonGroups = episodes.GroupBy(e => new { e.SeriesId, e.SeasonNumber }).Select(g => new
        {
            g.Key.SeriesId,
            g.Key.SeasonNumber,
            TotalCount = g.Count(),
            HasFileCount = g.Count(e => e.EpisodeFileId != null && e.EpisodeFileId != 0),
            MonitoredCount = g.Count(e => e.Monitored == true),
            Episodes = g.Select(e => new { episodeNumber = e.EpisodeNumber, title = e.Title, monitored = e.Monitored, hasFile = e.EpisodeFileId != null && e.EpisodeFileId != 0, airDateUtc = e.AirDateUtc, reason = e.Reason }).ToArray()
        }).ToList();

        var seriesList = seriesPage.Select(s =>
        {
            var seriesSeasonGroups = seasonGroups.Where(g => g.SeriesId == s.EntryId).ToList();
            var seriesAvailable = seriesSeasonGroups.Sum(g => g.HasFileCount);
            var seriesMonitored = seriesSeasonGroups.Sum(g => g.MonitoredCount);
            var seriesTotal = seriesSeasonGroups.Sum(g => g.TotalCount);

            var seasons = seriesSeasonGroups
                .ToDictionary(
                    g => g.SeasonNumber.ToString(),
                    g => (object)new
                    {
                        monitored = g.MonitoredCount,
                        available = g.HasFileCount,
                        missing = g.TotalCount - g.HasFileCount,
                        episodes = g.Episodes
                    });

            return new
            {
                series = new
                {
                    id = s.EntryId,
                    title = s.Title,
                    monitored = s.Monitored,
                    qualityProfileId = s.QualityProfileId,
                    qualityProfileName = s.QualityProfileName
                },
                totals = new { available = seriesAvailable, monitored = seriesMonitored, missing = seriesTotal - seriesAvailable },
                seasons
            };
        }).ToList();

        return Results.Ok(new
        {
            category,
            total,
            page = currentPage,
            page_size = currentPageSize,
            counts = new
            {
                available = episodeRollupsApi.Available,
                monitored = episodeRollupsApi.Monitored,
                missing = episodeRollupsApi.Missing
            },
            series = seriesList
        });
    });

    app.MapGet("/api/lidarr/{category}/albums", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, CatalogRollupService rollups, int? page, int? page_size, string? q, bool? monitored, bool? has_file, bool? quality_met, bool? is_request, bool? flat_mode) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Albums.Where(a => keys.Contains(a.ArrInstance));
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(a => a.Title.Contains(q));
        if (monitored.HasValue)
            query = query.Where(a => a.Monitored == monitored.Value);
        if (has_file.HasValue)
            query = query.Where(a => has_file.Value ? a.AlbumFileId != 0 : a.AlbumFileId == 0);
        if (quality_met.HasValue)
            query = query.Where(a => a.QualityMet == quality_met.Value);
        if (is_request.HasValue)
            query = query.Where(a => a.IsRequest == is_request.Value);

        // flat_mode=true: return tracks instead of album-grouped response
        if (flat_mode == true)
        {
            var (_, _, trackRollupsApiFlat) = await rollups.GetLidarrRollupsAsync(keys);
            var trackBaseQuery = db.Tracks.Where(t => keys.Contains(t.ArrInstance));
            var trackTotal = await trackBaseQuery.CountAsync();
            var trackAvailable = trackRollupsApiFlat.Available;
            var trackMonitored = trackRollupsApiFlat.Monitored;
            var tracksFlat = await (
                from t in trackBaseQuery
                join a in db.Albums on t.AlbumId equals a.EntryId into aj
                from album in aj.DefaultIfEmpty()
                orderby t.TrackNumber
                select new
                {
                    id = t.EntryId,
                    trackNumber = t.TrackNumber,
                    title = t.Title,
                    hasFile = t.HasFile,
                    duration = t.Duration,
                    monitored = t.Monitored,
                    trackFileId = t.TrackFileId,
                    albumId = t.AlbumId,
                    albumTitle = album != null ? album.Title : null,
                    artistTitle = album != null ? album.ArtistTitle : null,
                    artistId = album != null ? (int?)album.ArtistId : null
                })
                .Skip(skip).Take(currentPageSize).ToListAsync();
            return Results.Ok(new
            {
                category,
                counts = new { available = trackAvailable, monitored = trackMonitored },
                total = trackTotal,
                page = currentPage,
                page_size = currentPageSize,
                tracks = tracksFlat
            });
        }

        var total = await baseQuery.CountAsync();
        var (albumRollupsApi, _, trackRollupsApi) = await rollups.GetLidarrRollupsAsync(keys);

        var albumPage = await query
            .OrderBy(a => a.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(a => new
            {
                a.EntryId,
                a.Title,
                a.ArtistId,
                a.ArtistTitle,
                a.ReleaseDate,
                a.Monitored,
                a.AlbumFileId,
                a.Reason,
                a.QualityProfileId,
                a.QualityProfileName
            })
            .ToListAsync();

        var albumIds = albumPage.Select(a => a.EntryId).ToList();

        var tracksForPage = await db.Tracks
            .Where(t => keys.Contains(t.ArrInstance) && albumIds.Contains(t.AlbumId))
            .OrderBy(t => t.TrackNumber)
            .Select(t => new
            {
                albumId = t.AlbumId,
                id = t.EntryId,
                trackNumber = t.TrackNumber,
                title = t.Title,
                hasFile = t.HasFile,
                duration = t.Duration,
                monitored = t.Monitored,
                trackFileId = t.TrackFileId
            })
            .ToListAsync();

        var albums = albumPage.Select(a => new
        {
            album = new
            {
                id = a.EntryId,
                title = a.Title,
                artistId = a.ArtistId,
                artistName = a.ArtistTitle,
                releaseDate = a.ReleaseDate,
                monitored = a.Monitored,
                hasFile = a.AlbumFileId != 0,
                reason = a.Reason,
                qualityProfileId = a.QualityProfileId,
                qualityProfileName = a.QualityProfileName
            },
            totals = new
            {
                available = tracksForPage.Count(t => t.albumId == a.EntryId && t.hasFile),
                monitored = a.Monitored ? 1 : 0,
                missing = tracksForPage.Count(t => t.albumId == a.EntryId && !t.hasFile)
            },
            tracks = tracksForPage.Where(t => t.albumId == a.EntryId).Cast<object>().ToList()
        }).ToList();

        return Results.Ok(new
        {
            category,
            counts = new
            {
                available = albumRollupsApi.Available,
                monitored = albumRollupsApi.Monitored,
                missing = albumRollupsApi.Missing,
                quality_met = albumRollupsApi.QualityMet,
                requests = albumRollupsApi.Requests
            },
            counts_tracks = new
            {
                available = trackRollupsApi.Available,
                monitored = trackRollupsApi.Monitored,
                missing = trackRollupsApi.Missing
            },
            total,
            page = currentPage,
            page_size = currentPageSize,
            albums
        });
    });

    app.MapGet("/api/lidarr/{category}/tracks", async (string category, TorrentarrConfig cfg, TorrentarrDbContext db, int? page, int? page_size, string? q) =>
    {
        var keys = ArrCatalogIdentity.QueryKeys(cfg, category);
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Tracks.Where(t => keys.Contains(t.ArrInstance));
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(t => t.Title != null && t.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var availableCount = await baseQuery.CountAsync(t => t.HasFile);
        var monitoredCount = await baseQuery.CountAsync(t => t.Monitored);

        var tracksPage = await (
            from t in query
            join a in db.Albums on t.AlbumId equals a.EntryId into aj
            from album in aj.DefaultIfEmpty()
            orderby t.TrackNumber
            select new
            {
                id = t.EntryId,
                trackNumber = t.TrackNumber,
                title = t.Title,
                hasFile = t.HasFile,
                duration = t.Duration,
                monitored = t.Monitored,
                trackFileId = t.TrackFileId,
                albumId = t.AlbumId,
                albumTitle = album != null ? album.Title : null,
                artistTitle = album != null ? album.ArtistTitle : null,
                artistId = album != null ? (int?)album.ArtistId : null
            })
            .Skip(skip)
            .Take(currentPageSize)
            .ToListAsync();

        return Results.Ok(new
        {
            category,
            counts = new { available = availableCount, monitored = monitoredCount, missing = total - availableCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            tracks = tracksPage
        });
    });

    app.MapGet("/api/config", (TorrentarrConfig cfg) =>
    {
        var token = Newtonsoft.Json.Linq.JObject.FromObject(cfg);
        return Results.Content(StripSensitiveKeys(token).ToString(Newtonsoft.Json.Formatting.None), "application/json");
    });

    app.MapPost("/api/config", async (HttpRequest request, TorrentarrConfig cfg, ConfigurationLoader loader, WorkerProcessSupervisor workerMgr, QBitCategoryWorkerManager qbitCategoryMgr, ITorrentClientRegistry registry, TorrentarrDbContext db, IServiceProvider services) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (!payload.TryGetProperty("changes", out var changesEl))
                return Results.BadRequest(new { error = "Missing 'changes' field" });

            TorrentarrConfig? updatedConfig;
            var changesObj = Newtonsoft.Json.Linq.JObject.Parse(changesEl.GetRawText());
            var renames = payload.TryGetProperty("renames", out var renamesEl)
                ? Newtonsoft.Json.Linq.JObject.Parse(renamesEl.GetRawText()) : null;
            ConfigRenameHelper.PreserveSecrets(cfg, changesObj, renames);
            var (mergedConfig, applyError) = ApplyDottedConfigChanges(cfg, changesObj);
            if (applyError != null)
                return applyError;
            updatedConfig = mergedConfig;

            if (updatedConfig == null)
                return Results.BadRequest(new { error = "Invalid config payload" });

            var (valid, validationError) = ConfigValidationHelper.ValidateAll(updatedConfig);
            if (!valid)
                return Results.BadRequest(new { error = validationError });

            return await SaveAndRespondConfigUpdate(cfg, updatedConfig, loader, workerMgr, qbitCategoryMgr, registry, db, services, renames);
        }
        catch (Exception ex)
        {
            return Results.BadRequest(new { error = ex.Message });
        }
    });

    app.MapPost("/api/update", async (UpdateService updater, IHostApplicationLifetime lifetime) =>
    {
        if (updater.ApplyState.InProgress)
            return Results.Ok(new { success = false, message = "Update already in progress" });
        await updater.CheckForUpdateAsync();
        await updater.ApplyUpdateAsync(lifetime);
        if (!updater.ApplyState.InProgress && updater.ApplyState.LastResult == "error")
            return Results.Ok(new { success = false, message = updater.ApplyState.LastError });
        return Results.Ok(new { success = true, message = "Update started — application will restart when complete" });
    });

    app.MapGet("/api/download-update", async (UpdateService updater) =>
    {
        await updater.CheckForUpdateAsync();
        var meta = updater.BuildMetaResponse();
        var t = meta.GetType();
        return Results.Ok(new
        {
            download_url = (string?)t.GetProperty("binary_download_url")?.GetValue(meta),
            download_name = (string?)t.GetProperty("binary_download_name")?.GetValue(meta),
            download_size = (long?)t.GetProperty("binary_download_size")?.GetValue(meta),
            worker_download_url = (string?)t.GetProperty("worker_download_url")?.GetValue(meta),
            worker_download_name = (string?)t.GetProperty("worker_download_name")?.GetValue(meta),
            error = (string?)t.GetProperty("binary_download_error")?.GetValue(meta)
        });
    });

    app.MapPost("/api/arr/test-connection", (TestConnectionRequest req, TorrentarrConfig cfg) =>
        HandleTestConnection(req, cfg));

    app.MapGet("/api/token", (TorrentarrConfig cfg) =>
        Results.Ok(new { token = cfg.WebUI.Token }));

    app.MapGet("/api/torrents/distribution", async (TorrentarrConfig cfg, TorrentarrDbContext db) =>
    {
        var distribution = new Dictionary<string, Dictionary<string, int>>();
        foreach (var (name, instanceCfg) in cfg.ArrInstances)
        {
            if (string.IsNullOrEmpty(instanceCfg.Category)) continue;
            if (!distribution.ContainsKey(instanceCfg.Category))
                distribution[instanceCfg.Category] = new Dictionary<string, int>();
            var count = instanceCfg.Type.ToLowerInvariant() switch
            {
                "radarr" => await db.Movies.CountAsync(m => m.ArrInstance == name),
                "sonarr" => await db.Episodes.CountAsync(e => e.ArrInstance == name),
                "lidarr" => await db.Tracks.CountAsync(t => t.ArrInstance == name),
                "readarr" => await db.Books.CountAsync(b => b.ArrInstance == name),
                _ => 0
            };
            distribution[instanceCfg.Category][name] = count;
        }
        return Results.Ok(new { distribution });
    });

    // SPA fallback
    app.MapFallbackToFile("index.html");

    Log.Information("Torrentarr WebUI starting on http://localhost:{Port}", config.WebUI.Port);
    Log.Information("Access the WebUI at: http://localhost:{Port}", config.WebUI.Port);

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// ── Log file helpers ──────────────────────────────────────────────────────

/// <summary>
/// Validates that a log file name is a plain filename ending in .log with no path components.
/// Prevents directory traversal attacks.
/// </summary>
static bool IsValidLogFileName(string name) =>
    !string.IsNullOrWhiteSpace(name)
    && !name.Contains('/')
    && !name.Contains('\\')
    && !name.Contains("..")
    && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase);

/// <summary>
/// Reads the last <paramref name="maxLines"/> lines from a log file efficiently,
/// using FileShare.ReadWrite so Serilog's active write lock is never contested.
/// Uses a seek heuristic to avoid loading the entire file for large files.
/// </summary>
static async Task<string> TailLogFileAsync(string path, int maxLines)
{
    await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    if (fs.Length == 0) return string.Empty;

    // Heuristic: assume ~200 bytes per log line on average.
    // Seek to an estimated position from the end and read forward from there.
    const long bytesPerLineEstimate = 200;
    var seekPos = Math.Max(0, fs.Length - maxLines * bytesPerLineEstimate);
    fs.Seek(seekPos, SeekOrigin.Begin);

    using var reader = new StreamReader(fs, System.Text.Encoding.UTF8,
        detectEncodingFromByteOrderMarks: false, bufferSize: 65536, leaveOpen: true);

    // If we landed in the middle of a line, discard the partial first line.
    if (seekPos > 0) _ = await reader.ReadLineAsync();

    var lines = new List<string>(maxLines + 1);
    string? line;
    while ((line = await reader.ReadLineAsync()) != null)
        lines.Add(line);

    if (lines.Count >= maxLines)
        return string.Join("\n", lines.TakeLast(maxLines));

    // Heuristic undershot — not enough lines captured; fall back to full read.
    fs.Seek(0, SeekOrigin.Begin);
    reader.DiscardBufferedData();
    lines.Clear();
    while ((line = await reader.ReadLineAsync()) != null)
        lines.Add(line);

    return string.Join("\n", lines.TakeLast(maxLines));
}

/// <summary>
/// Shared handler for both /web/arr/test-connection and /api/arr/test-connection.
/// Supports instanceKey for redacted API key lookups and includes retry logic for quality profiles.
/// Always returns 200 so the frontend doesn't treat Arr errors as WebUI auth failures.
/// </summary>
static async Task<IResult> HandleTestConnection(TestConnectionRequest req, TorrentarrConfig cfg)
{
    try
    {
        var uri = req.Uri;
        var apiKey = req.ApiKey;
        var skipTls = req.SkipTlsVerify ?? false;

        // When instanceKey is provided, load URI and APIKey from config (e.g. when API key is redacted in UI)
        if (!string.IsNullOrEmpty(req.InstanceKey))
        {
            if (string.IsNullOrEmpty(req.ArrType))
                return Results.BadRequest(new { success = false, message = "Missing required field: arrType" });

            if (!cfg.ArrInstances.TryGetValue(req.InstanceKey, out var arrCfg))
                return Results.Ok(new { success = false, message = "Instance not found or missing URI/APIKey in config" });

            uri = req.Uri ?? arrCfg.URI;
            apiKey = arrCfg.APIKey;
            skipTls = req.SkipTlsVerify ?? arrCfg.SkipTLSVerify;
        }

        if (string.IsNullOrEmpty(req.ArrType) || string.IsNullOrEmpty(uri) || string.IsNullOrEmpty(apiKey))
            return Results.Ok(new { success = false, message = "Missing required fields: arrType, uri, or apiKey" });

        // Validate URI scheme
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri) ||
            (parsedUri.Scheme != "http" && parsedUri.Scheme != "https"))
            return Results.Ok(new { success = false, message = "URI must use http or https scheme" });

        // Create the appropriate Arr client and fetch system info + quality profiles
        SystemInfo? systemInfo;
        var profiles = new List<QualityProfile>();
        var arrType = req.ArrType.ToLowerInvariant();

        Func<Task<SystemInfo>> getSystemInfo;
        Func<Task<List<QualityProfile>>> getProfiles;

        switch (arrType)
        {
            case "radarr":
                var radarr = new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(uri, apiKey, skipTls);
                getSystemInfo = () => radarr.GetSystemInfoAsync();
                getProfiles = () => radarr.GetQualityProfilesAsync();
                break;
            case "sonarr":
                var sonarr = new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(uri, apiKey, skipTls);
                getSystemInfo = () => sonarr.GetSystemInfoAsync();
                getProfiles = () => sonarr.GetQualityProfilesAsync();
                break;
            case "lidarr":
                var lidarr = new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(uri, apiKey, skipTls);
                getSystemInfo = () => lidarr.GetSystemInfoAsync();
                getProfiles = () => lidarr.GetQualityProfilesAsync();
                break;
            case "readarr":
                var readarr = new Torrentarr.Infrastructure.ApiClients.Arr.ReadarrClient(uri, apiKey, skipTls);
                getSystemInfo = () => readarr.GetSystemInfoAsync();
                getProfiles = () => readarr.GetQualityProfilesAsync();
                break;
            default:
                return Results.BadRequest(new { error = $"Invalid arrType: {req.ArrType}" });
        }

        systemInfo = await getSystemInfo();
        if (string.IsNullOrWhiteSpace(systemInfo?.Version))
            return Results.Ok(new { success = false, message = "Connection test failed" });

        Log.Information("Arr connection test succeeded for {ArrType} at {Uri}: version {Version}",
            req.ArrType, uri, systemInfo.Version);

        // Fetch quality profiles with retry logic for transient errors
        const int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                profiles = await getProfiles();
                break;
            }
            catch (Exception) when (attempt < maxRetries)
            {
                await Task.Delay(1000);
            }
        }

        return Results.Ok(new
        {
            success = true,
            message = $"Connected to {req.ArrType} {systemInfo.Version}",
            systemInfo = new { version = systemInfo.Version, branch = (string?)null },
            qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
        });
    }
    catch (Exception ex)
    {
        // Return 200 with success: false so the frontend doesn't treat Arr errors as WebUI auth failure
        var errorMsg = ex.Message;
        string message;
        if (errorMsg.Contains("401") || errorMsg.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
            message = "Unauthorized: Invalid API key";
        else if (errorMsg.Contains("404"))
            message = $"Not found: Check URI";
        else if (errorMsg.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
                 errorMsg.Contains("No connection could be made", StringComparison.OrdinalIgnoreCase))
            message = $"Connection refused: Cannot reach server";
        else
            message = "Connection test failed";

        return Results.Ok(new { success = false, message });
    }
}

static Newtonsoft.Json.JsonSerializerSettings CreateConfigMergeSerializerSettings() => new()
{
    ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
    NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
    ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace,
};

/// <summary>
/// Apply dotted-key config changes onto the current in-memory config snapshot.
/// Used by POST /web/config and POST /api/config when a <c>changes</c> object is supplied.
/// </summary>
static (TorrentarrConfig? updatedConfig, IResult? error) ApplyDottedConfigChanges(
    TorrentarrConfig cfg,
    Newtonsoft.Json.Linq.JObject changesObj)
{
    var serializer = Newtonsoft.Json.JsonSerializer.Create(CreateConfigMergeSerializerSettings());

    var currentObj = new Newtonsoft.Json.Linq.JObject();
    currentObj["Settings"] = Newtonsoft.Json.Linq.JObject.FromObject(cfg.Settings, serializer);
    currentObj["WebUI"] = Newtonsoft.Json.Linq.JObject.FromObject(cfg.WebUI, serializer);
    foreach (var (key, qbit) in cfg.QBitInstances)
        currentObj[key] = Newtonsoft.Json.Linq.JObject.FromObject(qbit, serializer);
    currentObj["TorrentClient"] = Newtonsoft.Json.Linq.JObject.FromObject(cfg.TorrentClients, serializer);
    foreach (var (key, arr) in cfg.ArrInstances)
        currentObj[key] = Newtonsoft.Json.Linq.JObject.FromObject(arr, serializer);

    foreach (var change in changesObj.Properties())
    {
        if (string.Equals(change.Name, "Settings.ConfigVersion", StringComparison.OrdinalIgnoreCase))
            return (null, Results.Json(new { error = "Cannot modify protected configuration key: Settings.ConfigVersion" }, statusCode: 403));

        if (IsSensitiveDottedKey(change.Name) &&
            change.Value.Type == Newtonsoft.Json.Linq.JTokenType.String &&
            change.Value.ToString() == REDACTED_PLACEHOLDER)
            continue;

        var passwordHashError = WebUIAuthHelpers.RejectPasswordHashConfigChange(
            change.Name,
            change.Value.Type == Newtonsoft.Json.Linq.JTokenType.String ? change.Value.ToString() : change.Value.ToString());
        if (passwordHashError != null)
            return (null, Results.Json(new { error = passwordHashError }, statusCode: 403));

        var parts = change.Name.Split('.');
        var rawSectionKey = parts[0];
        var sectionKey = currentObj.Properties()
            .FirstOrDefault(p => p.Name.Equals(rawSectionKey, StringComparison.OrdinalIgnoreCase))?.Name
            ?? rawSectionKey;
        if (change.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null)
        {
            if (parts.Length == 1)
                currentObj.Remove(sectionKey);
            else if (currentObj[sectionKey] is Newtonsoft.Json.Linq.JObject sect)
                DeleteNestedToken(sect, parts, 1);
        }
        else if (parts.Length == 1)
        {
            currentObj[sectionKey] = change.Value;
        }
        else
        {
            if (currentObj[sectionKey] is not Newtonsoft.Json.Linq.JObject sect)
            {
                sect = new Newtonsoft.Json.Linq.JObject();
                currentObj[sectionKey] = sect;
            }
            SetNestedToken(sect, parts, 1, change.Value);
        }
    }

    foreach (var emptyProp in currentObj.Properties().ToList())
    {
        if (emptyProp.Value is Newtonsoft.Json.Linq.JObject emptyObj && !emptyObj.Properties().Any())
            currentObj.Remove(emptyProp.Name);
    }

    var updatedConfig = new TorrentarrConfig();
    if (currentObj["Settings"] is Newtonsoft.Json.Linq.JObject settingsObj)
        updatedConfig.Settings = settingsObj.ToObject<SettingsConfig>(serializer) ?? new SettingsConfig();
    if (currentObj["WebUI"] is Newtonsoft.Json.Linq.JObject webuiObj)
        updatedConfig.WebUI = webuiObj.ToObject<WebUIConfig>(serializer) ?? new WebUIConfig();
    if (currentObj["TorrentClient"] is Newtonsoft.Json.Linq.JObject torrentClientObj)
        updatedConfig.TorrentClients = torrentClientObj.ToObject<Dictionary<string, TorrentClientInstanceConfig>>(serializer)
            ?? new Dictionary<string, TorrentClientInstanceConfig>();

    foreach (var prop in currentObj.Properties())
    {
        if (prop.Name.Equals("TorrentClient", StringComparison.OrdinalIgnoreCase)) continue;
        if (prop.Value is not Newtonsoft.Json.Linq.JObject sectionObj) continue;
        var lower = prop.Name.ToLowerInvariant();
        var arrType = ArrSectionHelper.ArrTypeFromSectionName(prop.Name);
        bool isQbit = lower == "qbit" || lower.StartsWith("qbit-");
        if (arrType != null)
        {
            var arrConfig = sectionObj.ToObject<ArrInstanceConfig>(serializer) ?? new ArrInstanceConfig();
            if (string.IsNullOrEmpty(arrConfig.Type))
                arrConfig.Type = arrType;
            updatedConfig.ArrInstances[prop.Name] = arrConfig;
        }
        else if (isQbit)
        {
            updatedConfig.QBitInstances[prop.Name] = sectionObj.ToObject<QBitConfig>(serializer) ?? new QBitConfig();
        }
    }

    return (updatedConfig, null);
}

static async Task<IResult> SaveAndRespondConfigUpdate(
    TorrentarrConfig cfg,
    TorrentarrConfig updatedConfig,
    ConfigurationLoader loader,
    WorkerProcessSupervisor? workerMgr = null,
    QBitCategoryWorkerManager? qbitCategoryMgr = null, ITorrentClientRegistry? registry = null,
    TorrentarrDbContext? db = null, IServiceProvider? services = null, Newtonsoft.Json.Linq.JObject? renames = null)
{
    var passwordHashError = WebUIAuthHelpers.ValidatePasswordHashForConfigApiSave(cfg, updatedConfig);
    if (passwordHashError != null)
        return Results.Json(new { error = passwordHashError }, statusCode: 403);


    var (reloadType, affectedInstancesList) = DetermineReloadType(cfg, updatedConfig);
    var reloader = services?.GetService<IConfigReloader>();
    var validation = reloader?.Validate(updatedConfig) ?? ConfigValidationHelper.ValidateAll(updatedConfig);
    if (!validation.Ok) return Results.BadRequest(new { error = validation.Error });


    if (db != null)
        await InstanceRenamePersistence.SaveAsync(cfg, updatedConfig, renames, loader, db, suspend: () => Task.CompletedTask);
    else loader.SaveConfig(updatedConfig);
    var renameMap = renames?.Properties().ToDictionary(p => p.Name, p => p.Value.ToString());
    var applied = reloader?.Apply(updatedConfig, renameMap) ?? false;
    if (reloader == null) cfg.Apply(updatedConfig, cfg.Revision + 1);
    services?.GetServices<IHostedService>().OfType<ProcessOrchestratorService>().FirstOrDefault()?.NotifyConfigurationChanged();
    var application = reloader?.GetStatus();
    var warning = applied ? null : application?.Error;
    reloadType = "live";

    return Results.Ok(new
    {
        status = "ok",
        configReloaded = application?.State == "applied",
        application,
        reloadType,
        affectedInstances = affectedInstancesList,
        warning
    });
}

/// <summary>
/// Recursively redact string values whose keys match <see cref="SensitiveKeyPatternRegex"/>.
/// Returns a new JToken with sensitive values replaced by <see cref="REDACTED_PLACEHOLDER"/>.
/// </summary>
static Newtonsoft.Json.Linq.JToken StripSensitiveKeys(Newtonsoft.Json.Linq.JToken token)
{
    if (token is Newtonsoft.Json.Linq.JObject obj)
    {
        var result = new Newtonsoft.Json.Linq.JObject();
        foreach (var prop in obj.Properties())
        {
            if (prop.Value.Type == Newtonsoft.Json.Linq.JTokenType.String && System.Text.RegularExpressions.Regex.IsMatch(prop.Name, SensitiveKeyPatternRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                result[prop.Name] = REDACTED_PLACEHOLDER;
            else
                result[prop.Name] = StripSensitiveKeys(prop.Value);
        }
        return result;
    }
    if (token is Newtonsoft.Json.Linq.JArray arr)
    {
        var result = new Newtonsoft.Json.Linq.JArray();
        foreach (var item in arr)
            result.Add(StripSensitiveKeys(item));
        return result;
    }
    return token.DeepClone();
}

/// <summary>
/// Returns true if a dotted config key refers to a sensitive value (e.g. "Radarr-1080.APIKey").
/// </summary>
static bool IsSensitiveDottedKey(string dottedKey)
{
    if (string.IsNullOrEmpty(dottedKey) || !dottedKey.Contains('.')) return false;
    var lastPart = dottedKey[(dottedKey.LastIndexOf('.') + 1)..];
    return System.Text.RegularExpressions.Regex.IsMatch(lastPart, SensitiveKeyPatternRegex, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
}

/// <summary>
/// Recursively sets a value in a JObject following a dot-split path array starting at startIndex.
/// Creates intermediate JObjects as needed.
/// </summary>
static void SetNestedToken(Newtonsoft.Json.Linq.JObject obj, string[] parts, int startIndex, Newtonsoft.Json.Linq.JToken value)
{
    if (startIndex == parts.Length - 1)
    {
        obj[parts[startIndex]] = value;
        return;
    }
    if (obj[parts[startIndex]] is not Newtonsoft.Json.Linq.JObject next)
    {
        next = new Newtonsoft.Json.Linq.JObject();
        obj[parts[startIndex]] = next;
    }
    SetNestedToken(next, parts, startIndex + 1, value);
}

/// <summary>
/// Recursively deletes a key in a JObject following a dot-split path array starting at startIndex.
/// </summary>
static void DeleteNestedToken(Newtonsoft.Json.Linq.JObject obj, string[] parts, int startIndex)
{
    if (startIndex == parts.Length - 1)
    {
        obj.Remove(parts[startIndex]);
        return;
    }
    if (obj[parts[startIndex]] is Newtonsoft.Json.Linq.JObject next)
        DeleteNestedToken(next, parts, startIndex + 1);
}

/// <summary>
/// Determine the appropriate config reload type by comparing old vs new config.
/// Mirrors qBitrr's reload strategy: full > multi_arr/single_arr > webui > frontend > none.
/// </summary>
static (string reloadType, List<string> affectedInstances) DetermineReloadType(
    TorrentarrConfig oldCfg, TorrentarrConfig newCfg)
{
    var serialize = (object? o) => Newtonsoft.Json.JsonConvert.SerializeObject(o);

    // QBit instance changes → full reload (requires process restart)
    bool hasQBitChanges = serialize(oldCfg.QBitInstances) != serialize(newCfg.QBitInstances)
        || serialize(oldCfg.TorrentClients) != serialize(newCfg.TorrentClients);

    // Settings changes → webui reload; the isolated workers need a restart to load them.
    bool hasSettingsChanges = serialize(oldCfg.Settings) != serialize(newCfg.Settings);

    // WebUI connection fields (host/port/token) → webui restart
    bool hasWebuiKeyChanges = oldCfg.WebUI.Host != newCfg.WebUI.Host
                           || oldCfg.WebUI.Port != newCfg.WebUI.Port
                           || oldCfg.WebUI.Token != newCfg.WebUI.Token;

    // Other WebUI fields (theme, density, grouping, liveArr) → frontend-only, no restart
    bool hasFrontendOnlyChanges = !hasWebuiKeyChanges
                               && serialize(oldCfg.WebUI) != serialize(newCfg.WebUI);

    // Per-Arr instance changes
    var oldArrKeys = oldCfg.ArrInstances.Keys.ToHashSet();
    var newArrKeys = newCfg.ArrInstances.Keys.ToHashSet();
    var affectedArr = new List<string>();
    foreach (var key in oldArrKeys.Union(newArrKeys))
    {
        var oldVal = oldCfg.ArrInstances.TryGetValue(key, out var ov) ? serialize(ov) : null;
        var newVal = newCfg.ArrInstances.TryGetValue(key, out var nv) ? serialize(nv) : null;
        if (oldVal != newVal) affectedArr.Add(key);
    }
    affectedArr.Sort();

    if (hasQBitChanges)
        return ("full", newCfg.ArrInstances.Keys.OrderBy(k => k).ToList());
    if (affectedArr.Count > 0)
        return (affectedArr.Count > 1 ? "multi_arr" : "single_arr", affectedArr);
    if (hasSettingsChanges || hasWebuiKeyChanges)
        return ("webui", []);
    if (hasFrontendOnlyChanges)
        return ("frontend", []);
    return ("none", []);
}

/// <summary>
/// Background service that orchestrates all processes.
/// </summary>
class ProcessOrchestratorService : BackgroundService
{
    private readonly ILogger<ProcessOrchestratorService> _logger;
    private readonly TorrentarrConfig _config;
    private readonly ITorrentClientRegistry _qbitManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessStateManager _stateManager;
    private readonly ISafeDeletionService _safeDeletion;
    private bool _qbitConfigured;
    private readonly IConfigReloader? _reloader;
    private readonly SemaphoreSlim _configurationChanged = new(0, 1);

    public ProcessOrchestratorService(
        ILogger<ProcessOrchestratorService> logger,
        TorrentarrConfig config,
        ITorrentClientRegistry qbitManager,
        IServiceScopeFactory scopeFactory,
        ProcessStateManager stateManager,
        ISafeDeletionService safeDeletion, IConfigReloader? reloader = null)
    {
        _reloader = reloader;
        if (_reloader != null) _reloader.ConfigReloaded += OnConfigurationChanged;
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _scopeFactory = scopeFactory;
        _stateManager = stateManager;
        _safeDeletion = safeDeletion;
        RefreshConfiguration();
    }

    private void OnConfigurationChanged(object? sender, ConfigReloadedEventArgs args) { if (args.Success) NotifyConfigurationChanged(); }

    public void NotifyConfigurationChanged()
    {
        // Coalesce saves while the current policy pass finishes.
        try { _configurationChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private void RefreshConfiguration()
    {
        _qbitConfigured = _config.GetAllTorrentClients().Values.Any(q =>
            !q.Disabled && q.Host != "CHANGE_ME" && q.UserName != "CHANGE_ME" && q.Password != "CHANGE_ME");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Process Orchestrator starting");

        try
        {
            if (!_qbitConfigured)
            {
                _logger.LogWarning("No qBittorrent instances configured - WebUI only mode");
            }
            else
            {
                var connected = await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), stoppingToken);
                if (connected == 0)
                    _logger.LogWarning("Failed to connect to any qBittorrent instance. WebUI is still available; will retry.");
            }

            var initialManaged = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(_config);
            if (initialManaged.Count > 0)
                _logger.LogInformation("FreeSpace categories: {Categories}", string.Join(", ", initialManaged));

            _stateManager.Initialize("FreeSpaceManager", new ArrProcessState
            {
                Name = "FreeSpaceManager",
                Category = "FreeSpaceManager",
                Kind = "torrent",
                MetricType = "free-space",
                Alive = false,
                CategoryCount = null
            });

            // Special categories are initialized when torrent clients are configured.
            if (_qbitConfigured)
            {
                _stateManager.Initialize("Recheck", new ArrProcessState
                {
                    Name = "Recheck",
                    Category = "Recheck",
                    Kind = "category",
                    Alive = false,
                    CategoryCount = null
                });
                _stateManager.Initialize("Failed", new ArrProcessState
                {
                    Name = "Failed",
                    Category = "Failed",
                    Kind = "category",
                    Alive = false,
                    CategoryCount = null
                });
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var passRevision = _config.Revision;
                try
                {
                    using var configuration = _config.BeginOperation();
                    RefreshConfiguration();
                    if (!TorrentPolicyHelper.EnableFreeSpace(_config))
                        _stateManager.Update("FreeSpaceManager", state => state.Alive = false);
                    if (_qbitConfigured)
                    {
                        await _qbitManager.EnsureAllConnectedAsync(_config.GetAllTorrentClients(), stoppingToken);
                        if (_qbitManager.IsConnected())
                        {
                            await ProcessSpecialCategoriesAsync(stoppingToken);

                            var enableTrackerSort = TorrentPolicyHelper.EnableTrackerSort(_config);
                            var enableFreeSpace = TorrentPolicyHelper.EnableFreeSpace(_config);
                            var managedCategories = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(_config);
                            if (managedCategories.Count > 0 && (enableTrackerSort || enableFreeSpace))
                                await ProcessTorrentPolicyAsync(managedCategories, enableTrackerSort, enableFreeSpace, stoppingToken);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in orchestrator loop");
                }

                _reloader?.Acknowledge("torrent-policy", passRevision);
                await _configurationChanged.WaitAsync(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Orchestrator shutting down gracefully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process orchestrator");
        }
    }

    private async Task ProcessSpecialCategoriesAsync(CancellationToken cancellationToken)
    {
        using var processing = ConfigMutationGuard.EnterProcessing();
        int totalFailed = 0;
        int totalRecheck = 0;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                using var recoveryScope = _scopeFactory.CreateScope();
                var recoveryDb = recoveryScope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
                var allTorrents = await client.GetTorrentsAsync(cancellationToken: cancellationToken);
                MaintenanceRecovery.ObserveExplicitExits(recoveryDb, instanceName, allTorrents, _config.Settings.RecheckCategory);
                var failedTorrents = allTorrents.Where(t => t.Category.Equals(_config.Settings.FailedCategory, StringComparison.OrdinalIgnoreCase)).ToList();
                totalFailed += failedTorrents.Count;
                foreach (var torrent in failedTorrents)
                {
                    // §2.13: Settings-level IgnoreTorrentsYoungerThan applies to failed/recheck
                    if (torrent.AddedOn > 0)
                    {
                        var addedAt = DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).UtcDateTime;
                        if ((DateTime.UtcNow - addedAt).TotalSeconds < _config.Settings.IgnoreTorrentsYoungerThan)
                        {
                            _logger.LogTrace("[{Instance}] Skipping failed torrent too young: {Name} (age {Age:F0}s < {Threshold}s)",
                                instanceName, torrent.Name,
                                (DateTime.UtcNow - addedAt).TotalSeconds,
                                _config.Settings.IgnoreTorrentsYoungerThan);
                            continue;
                        }
                    }
                    _logger.LogWarning("[{Instance}] Deleting failed torrent: {Name}", instanceName, torrent.Name);
                    var deletion = await _safeDeletion.RemoveTorrentAsync(instanceName, torrent.Hash, removeContent: true, ct: cancellationToken);
                    if (!deletion.Success)
                        _logger.LogWarning("[{Instance}] Safe deletion skipped for {Name}: {Reason}", instanceName, torrent.Name, deletion.Error);
                }

                var recheckTorrents = allTorrents.Where(t => t.Category.Equals(_config.Settings.RecheckCategory, StringComparison.OrdinalIgnoreCase)).ToList();
                totalRecheck += recheckTorrents.Count;
                foreach (var torrent in recheckTorrents)
                {
                    // §2.13: Settings-level IgnoreTorrentsYoungerThan applies to failed/recheck
                    if (torrent.AddedOn > 0)
                    {
                        var addedAt = DateTimeOffset.FromUnixTimeSeconds(torrent.AddedOn).UtcDateTime;
                        if ((DateTime.UtcNow - addedAt).TotalSeconds < _config.Settings.IgnoreTorrentsYoungerThan)
                        {
                            _logger.LogTrace("[{Instance}] Skipping recheck torrent too young: {Name} (age {Age:F0}s < {Threshold}s)",
                                instanceName, torrent.Name,
                                (DateTime.UtcNow - addedAt).TotalSeconds,
                                _config.Settings.IgnoreTorrentsYoungerThan);
                            continue;
                        }
                    }
                    torrent.ClientInstanceId = instanceName;
                    if (await MaintenanceRecovery.RequestExplicitAsync(recoveryDb, client, torrent, cancellationToken))
                        _logger.LogInformation("[{Instance}] Requested recheck for torrent: {Name}", instanceName, torrent.Name);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error processing special categories", instanceName);
            }
        }
        _stateManager.Update("Failed", s => { s.CategoryCount = totalFailed; s.Alive = true; });
        _stateManager.Update("Recheck", s => { s.CategoryCount = totalRecheck; s.Alive = true; });
    }

    /// <summary>
    /// qBitrr <c>TorrentPolicyManager.process_torrents</c>: optional pre-sort sync + queue sort, then optional free-space.
    /// </summary>
    private async Task ProcessTorrentPolicyAsync(
        HashSet<string> managedCategories,
        bool enableTrackerSort,
        bool enableFreeSpace,
        CancellationToken cancellationToken)
    {
        using var processing = ConfigMutationGuard.EnterProcessing();
        IServiceScope? policyScope = null;
        try
        {
            ISeedingService? seeding = null;
            if (enableTrackerSort)
            {
                policyScope = _scopeFactory.CreateScope();
                seeding = policyScope.ServiceProvider.GetRequiredService<ISeedingService>();
            }

            if (enableTrackerSort && seeding != null)
            {
                _logger.LogDebug(
                    "TorrentPolicyManager workflow: pre-sort tracker/tag sync -> queue sort{Tail}",
                    enableFreeSpace ? " -> free-space" : "");
                await PreSortTrackerTagSyncAsync(seeding, managedCategories, cancellationToken);
                await SortManagedTorrentsByTrackerPriorityAsync(seeding, managedCategories, cancellationToken);
            }
            else if (enableFreeSpace)
            {
                _logger.LogDebug(
                    "TorrentPolicyManager tracker sorting disabled: Arr loops retain tracker/tag sync ownership");
            }

            if (enableFreeSpace)
                await ProcessFreeSpaceManagerAsync(managedCategories, cancellationToken);
        }
        finally
        {
            policyScope?.Dispose();
        }
    }

    /// <summary>
    /// qBitrr <c>TorrentPolicyManager._sync_tracker_tags_before_sort</c>.
    /// </summary>
    private async Task PreSortTrackerTagSyncAsync(
        ISeedingService seeding,
        HashSet<string> managedCategories,
        CancellationToken cancellationToken)
    {
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            foreach (var category in managedCategories)
            {
                List<TorrentInfo> torrents;
                try
                {
                    torrents = await client.GetTorrentsAsync(category, cancellationToken: cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{Instance}] Pre-sort sync: skip category {Category}", instanceName, category);
                    continue;
                }

                foreach (var t in torrents)
                {
                    if (t.Tags?.Contains("qBitrr-ignored", StringComparison.OrdinalIgnoreCase) == true)
                        continue;
                    t.QBitInstanceName = instanceName;
                    await seeding.ApplyTrackerActionsForTorrentAsync(t, cancellationToken);
                }
            }
        }
    }

    private async Task ProcessFreeSpaceManagerAsync(HashSet<string> managedCategories, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<IFreeSpaceService>();
        var result = await manager.ProcessAsync(managedCategories, cancellationToken);
        _stateManager.Update("FreeSpaceManager", state =>
        {
            state.CategoryCount = result.MarkedCount;
            state.MetricType = "free-space";
            state.Alive = result.Available;
        });
    }

    /// <summary>
    /// qBitrr <c>Arr._sort_torrents_by_tracker_priority</c> when <c>categories</c> is set (TorrentPolicyManager).
    /// </summary>
    private async Task SortManagedTorrentsByTrackerPriorityAsync(
        ISeedingService seeding,
        HashSet<string> managedCategories,
        CancellationToken cancellationToken)
    {
        var tagToPriority = TorrentPolicyHelper.MergeGlobalTrackerTagToPriorityMax(_config);
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                List<TorrentInfo> torrentList;
                try
                {
                    torrentList = await client.GetTorrentsAsync(category: null, "priority", cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[{Instance}] SortTorrents: falling back to added_on sort", instanceName);
                    torrentList = await client.GetTorrentsAsync(category: null, "added_on", cancellationToken);
                }

                torrentList = torrentList
                    .Where(t => !string.IsNullOrEmpty(t.Category) && managedCategories.Contains(t.Category))
                    .ToList();
                foreach (var t in torrentList)
                    t.QBitInstanceName = instanceName;

                if (torrentList.Count <= 1)
                    continue;

                var sortPriorities = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in torrentList)
                {
                    sortPriorities[t.Hash] = await seeding.GetTorrentQueueSortPriorityAsync(t, tagToPriority, cancellationToken);
                }

                var sortedTorrents = torrentList
                    .OrderBy(t => -sortPriorities.GetValueOrDefault(t.Hash, -100))
                    .ThenBy(t => -t.AddedOn)
                    .ThenBy(t => t.Name ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.Hash ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var queueMembership = torrentList.ToDictionary(
                    t => t.Hash,
                    t => TorrentPolicyHelper.IsQueueSeedingForSort(t.State),
                    StringComparer.OrdinalIgnoreCase);

                var currentByPosition = torrentList
                    .Select(t => (t, key: TorrentPolicyHelper.TorrentQueuePositionSortKey(t)))
                    .OrderBy(x => x.key.InactiveQueueGroup)
                    .ThenBy(x => x.key.Nq)
                    .Select(x => x.t)
                    .ToList();

                var currentDownloadingOrder = currentByPosition
                    .Where(t => !queueMembership.GetValueOrDefault(t.Hash))
                    .Select(t => t.Hash)
                    .ToList();
                var currentSeedingOrder = currentByPosition
                    .Where(t => queueMembership.GetValueOrDefault(t.Hash))
                    .Select(t => t.Hash)
                    .ToList();

                var desiredDownloadingOrder = sortedTorrents
                    .Where(t => !queueMembership.GetValueOrDefault(t.Hash))
                    .Select(t => t.Hash)
                    .ToList();
                var desiredSeedingOrder = sortedTorrents
                    .Where(t => queueMembership.GetValueOrDefault(t.Hash))
                    .Select(t => t.Hash)
                    .ToList();

                if (currentDownloadingOrder.SequenceEqual(desiredDownloadingOrder)
                    && currentSeedingOrder.SequenceEqual(desiredSeedingOrder))
                    continue;

                foreach (var queueIsSeeding in new[] { true, false })
                {
                    var queueTorrents = sortedTorrents.Where(t => queueMembership.GetValueOrDefault(t.Hash) == queueIsSeeding).ToList();
                    foreach (var torrent in queueTorrents.AsEnumerable().Reverse())
                        await client.SetTopPriorityAsync(torrent.Hash, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] SortTorrents policy step failed", instanceName);
            }
        }
    }


}

// Request models for API endpoints
public record TestConnectionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("arrType")] string ArrType,
    [property: System.Text.Json.Serialization.JsonPropertyName("uri")] string? Uri,
    [property: System.Text.Json.Serialization.JsonPropertyName("apiKey")] string? ApiKey,
    [property: System.Text.Json.Serialization.JsonPropertyName("instanceKey")] string? InstanceKey = null,
    [property: System.Text.Json.Serialization.JsonPropertyName("skipTlsVerify")] bool? SkipTlsVerify = null);
public record LoggerConfigurationRequest(string Level);
public record LoginRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("username")] string? Username,
    [property: System.Text.Json.Serialization.JsonPropertyName("password")] string? Password);
public record SetPasswordRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("username")] string? Username,
    [property: System.Text.Json.Serialization.JsonPropertyName("password")] string? Password,
    [property: System.Text.Json.Serialization.JsonPropertyName("setupToken")] string? SetupToken = null);

// Make Program accessible to test projects (WebApplicationFactory<Program>)
public partial class Program
{
    /// <summary>Result that does nothing when executed; used after ChallengeAsync() has already written the redirect.</summary>
    sealed class NoOpAfterChallengeResult : IResult
    {
        public static readonly NoOpAfterChallengeResult Instance = new();
        public Task ExecuteAsync(HttpContext ctx) => Task.CompletedTask;
    }
}
