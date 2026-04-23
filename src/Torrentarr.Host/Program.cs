using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Torrentarr.Host;
using Torrentarr.Host.Sinks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Security.Claims;
using System.Security.Cryptography;

// Data directory: aligned with resolved config path (see ConfigurationLoader.GetDataDirectoryPath)
var basePath = ConfigurationLoader.GetDataDirectoryPath();
var logsPath = Path.Combine(basePath, "logs");
var dbPath = Path.Combine(basePath, "torrentarr.db");
Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logsPath);

// CLI args (checked after host is built so WebApplicationFactory gets an IHost)
var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToList();
var firstArg = cmdArgs.Count > 0 ? cmdArgs[0].Trim().ToLowerInvariant() : "";

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

    if (!config.QBitInstances.Values.Any(q => q.Host != "CHANGE_ME" && q.UserName != "CHANGE_ME" && q.Password != "CHANGE_ME"))
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
    builder.Services.AddSingleton<QBittorrentConnectionManager>();
    builder.Services.AddSingleton<ProcessStateManager>();
    builder.Services.AddSingleton<IConnectivityService, ConnectivityService>();
    // ArrWorkerManager registered as both singleton and IHostedService so it's injectable in endpoints
    builder.Services.AddSingleton<ArrWorkerManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ArrWorkerManager>());
    builder.Services.AddHostedService<ProcessOrchestratorService>();
    // Scoped services (one per request / scope)
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddScoped<IArrImportService, ArrImportService>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaService>();
    builder.Services.AddScoped<ISearchExecutor, SearchExecutor>();
    builder.Services.AddScoped<QualityProfileSwitcherService>();
    builder.Services.AddSingleton<ITorrentCacheService, TorrentCacheService>();
    // §6.10 / §1.8: update check + auto-update
    builder.Services.AddSingleton<UpdateService>();
    builder.Services.AddHostedService<AutoUpdateBackgroundService>();

    builder.Services.AddSingleton<IPasswordHasher, BCryptPasswordHasher>();

    var authBuilder = builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.Cookie.Name = "torrentarr_session";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
            options.LoginPath = "/login";
            options.AccessDeniedPath = "/login";
        });
    if (config.WebUI.OIDCEnabled && config.WebUI.OIDC is { } oidc
        && !string.IsNullOrWhiteSpace(oidc.Authority)
        && !string.IsNullOrWhiteSpace(oidc.ClientId))
    {
        authBuilder.AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.Authority = oidc.Authority.TrimEnd('/');
            options.ClientId = oidc.ClientId;
            options.ClientSecret = oidc.ClientSecret;
            options.CallbackPath = oidc.CallbackPath;
            options.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Scope.Clear();
            foreach (var s in (oidc.Scopes ?? "openid profile").Split(' ', StringSplitOptions.RemoveEmptyEntries))
                options.Scope.Add(s);
        });
    }

    builder.Services.AddControllers()
        .AddNewtonsoftJson(options =>
        {
            options.SerializerSettings.ContractResolver =
                new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
            options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Include;
            options.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc;
        });

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo
        {
            Title = "Torrentarr API",
            Version = "v1",
            Description = "API for qBittorrent + Arr automation (qBitrr C# port)"
        });
        c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "API token (WebUI.Token). Use for /api/* endpoints. Use the Authorize button to set."
        });
        c.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                Array.Empty<string>()
            }
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

    builder.WebHost.ConfigureKestrel(options =>
    {
        var host = config.WebUI.Host;
        var port = config.WebUI.Port;
        if (host == "0.0.0.0")
            options.ListenAnyIP(port);
        else
            options.Listen(System.Net.IPAddress.Parse(host), port);
    });

    var app = builder.Build();

    // First-run hint: Host wwwroot is build output; API still works without the SPA bundle.
    var webRoot = app.Environment.WebRootPath;
    if (!string.IsNullOrEmpty(webRoot))
    {
        var indexFile = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexFile))
        {
            Log.Warning(
                "Web UI bundle not found at {Index}. Run ./build.sh or build webui and publish to wwwroot for the full SPA. API and Swagger (/swagger) are still available.",
                indexFile);
        }
    }

    // --version / -v: print version and exit (qBitrr parity)
    if (cmdArgs.Count == 1 && (firstArg == "--version" || firstArg == "-v"))
    {
        Console.WriteLine($"Torrentarr {UpdateService.GetCurrentVersion()}");
        return 0;
    }

    // --license / -l: print license and exit (qBitrr parity)
    if (cmdArgs.Count == 1 && (firstArg == "--license" || firstArg == "-l"))
    {
        Console.WriteLine("Torrentarr is licensed under the MIT License.");
        Console.WriteLine("Copyright (c) 2024-2026 Torrentarr contributors.");
        Console.WriteLine("See https://github.com/Feramance/Torrentarr/blob/master/LICENSE for the full text.");
        return 0;
    }

    // --source / -s: print source code URL and exit (qBitrr parity)
    if (cmdArgs.Count == 1 && (firstArg == "--source" || firstArg == "-s"))
    {
        Console.WriteLine("https://github.com/Feramance/Torrentarr");
        return 0;
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

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.Database.EnsureCreated();
        db.ConfigureWalMode();
        // Manual migrations for columns added after initial release
        ApplyManualMigrations(db);
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
            else if (context.Request.Query.ContainsKey("token") && context.Request.Method == "GET")
                providedToken = context.Request.Query["token"]; // Prefer Authorization: Bearer; query token can leak via Referer or server logs
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
            else if (context.Request.Query.ContainsKey("token") && context.Request.Method == "GET")
                providedToken = context.Request.Query["token"]; // Prefer Authorization: Bearer; query token can leak via Referer or server logs

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
        context.Response.Redirect("/login");
    });

    static bool IsAuthRequired(TorrentarrConfig c) => !c.WebUI.AuthDisabled;

    app.MapControllers();

    // Home redirect: / → /ui
    app.MapGet("/", () => Results.Redirect("/ui"));

    // Health check
    app.MapGet("/health", () => Results.Ok(new
    {
        status = "healthy",
        service = "torrentarr",
        timestamp = DateTime.UtcNow
    }));

    // ==================== /web/* endpoints ====================

    // Web Meta — fetches latest release from GitHub and compares with current version
    // §6.10: GET /web/meta — version info + update state + auth flags (MetaResponse-compatible)
    app.MapGet("/web/meta", async (UpdateService updater, TorrentarrConfig cfg, int? force) =>
    {
        await updater.CheckForUpdateAsync(forceRefresh: force.GetValueOrDefault() != 0);
        return Results.Ok(updater.BuildMetaResponse(cfg.WebUI));
    });

    // Web Status — matches TypeScript StatusResponse (no extra webui field)
    app.MapGet("/web/status", async (TorrentarrConfig cfg, QBittorrentConnectionManager qbitManager) =>
    {
        var primaryQbit = (cfg.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig());
        var qbitConfigured = primaryQbit.Host != "CHANGE_ME" && !string.IsNullOrEmpty(primaryQbit.Host);
        var qbitAlive = qbitConfigured && qbitManager.IsConnected();

        string? qbitVersion = null;
        if (qbitAlive)
        {
            var client = qbitManager.GetAllClients().Values.FirstOrDefault();
            if (client != null)
                try { qbitVersion = await client.GetVersionAsync(); } catch { /* best-effort */ }
        }

        // Build qBit instances dict from QBitInstances (all named instances)
        var qbitInstances = new Dictionary<string, object>();
        foreach (var (name, qbit) in cfg.QBitInstances)
        {
            var alive = name == "qBit" ? qbitAlive : qbitManager.IsConnected(name);
            string? ver = name == "qBit" ? qbitVersion : null;
            if (alive && name != "qBit")
            {
                var addlClient = qbitManager.GetClient(name);
                if (addlClient != null)
                    try { ver = await addlClient.GetVersionAsync(); } catch { /* best-effort */ }
            }
            qbitInstances[name] = new { alive, host = qbit.Host, port = qbit.Port, version = ver };
        }

        return Results.Ok(new
        {
            qbit = new
            {
                alive = qbitAlive,
                host = qbitConfigured ? primaryQbit.Host : (string?)null,
                port = qbitConfigured ? (int?)primaryQbit.Port : null,
                version = qbitVersion
            },
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
    app.MapGet("/web/qbit/categories", async (QBittorrentConnectionManager qbitManager, TorrentarrConfig cfg) =>
    {
        var categories = new List<object>();

        // Build Arr-managed category lookup: category name → ArrInstanceConfig
        var arrCategoryToConfig = cfg.ArrInstances
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Category))
            .ToDictionary(kvp => kvp.Value.Category!, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var primaryQbit = (cfg.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig());
        var qbitManagedSet = new HashSet<string>(primaryQbit.ManagedCategories, StringComparer.OrdinalIgnoreCase);
        var arrCategorySet = new HashSet<string>(arrCategoryToConfig.Keys, StringComparer.OrdinalIgnoreCase);
        // Only show ManagedCategories from qBit config - not Arr categories
        var monitoredForDefault = qbitManagedSet;

        if (primaryQbit.Host != "CHANGE_ME" && monitoredForDefault.Count > 0)
        {
            try
            {
                var client = qbitManager.GetAllClients().Values.FirstOrDefault();
                if (client != null)
                {
                    var allTorrents = await client.GetTorrentsAsync();

                    foreach (var catName in monitoredForDefault)
                    {
                        var torrentsInCat = allTorrents.Where(t => t.Category == catName).ToList();
                        var seedingTorrents = torrentsInCat.Where(t =>
                            t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase) ||
                            t.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)).ToList();

                        var managedBy = arrCategorySet.Contains(catName) ? "arr" : "qbit";

                        double maxRatio = primaryQbit.CategorySeeding.MaxUploadRatio;
                        int maxTime = primaryQbit.CategorySeeding.MaxSeedingTime;
                        int removeMode = primaryQbit.CategorySeeding.RemoveTorrent;
                        int dlLimit = primaryQbit.CategorySeeding.DownloadRateLimitPerTorrent;
                        int ulLimit = primaryQbit.CategorySeeding.UploadRateLimitPerTorrent;

                        if (arrCategoryToConfig.TryGetValue(catName, out var arrInstCfg))
                        {
                            var sm = arrInstCfg.Torrent?.SeedingMode;
                            if (sm != null)
                            {
                                maxRatio = sm.MaxUploadRatio;
                                maxTime = sm.MaxSeedingTime;
                                removeMode = sm.RemoveTorrent;
                                dlLimit = sm.DownloadRateLimitPerTorrent;
                                ulLimit = sm.UploadRateLimitPerTorrent;
                            }
                        }

                        categories.Add(new
                        {
                            category = catName,
                            // Always the qBit instance name — ProcessesView matches on this field
                            instance = "qBit",
                            managedBy,
                            torrentCount = torrentsInCat.Count,
                            seedingCount = seedingTorrents.Count,
                            totalSize = torrentsInCat.Sum(t => t.Size),
                            avgRatio = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => t.Ratio) : 0.0,
                            avgSeedingTime = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => (double)t.SeedingTime) : 0.0,
                            seedingConfig = new
                            {
                                maxRatio,
                                maxTime,
                                removeMode,
                                downloadLimit = dlLimit,
                                uploadLimit = ulLimit
                            }
                        });
                    }
                }
            }
            catch { /* qBit not reachable */ }
        }

        // Additional qBit instances — only their own ManagedCategories are monitored
        foreach (var (instName, instCfg) in cfg.QBitInstances.Where(q => q.Key != "qBit" && q.Value.Host != "CHANGE_ME"))
        {
            if (instCfg.ManagedCategories.Count == 0) continue;
            try
            {
                var addlClient = qbitManager.GetClient(instName);
                if (addlClient == null) continue;
                var addlTorrents = await addlClient.GetTorrentsAsync();

                foreach (var catName in instCfg.ManagedCategories)
                {
                    var torrentsInCat = addlTorrents.Where(t => t.Category == catName).ToList();
                    var seedingTorrents = torrentsInCat.Where(t =>
                        t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase) ||
                        t.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)).ToList();

                    categories.Add(new
                    {
                        category = catName,
                        instance = instName,
                        managedBy = "qbit",
                        torrentCount = torrentsInCat.Count,
                        seedingCount = seedingTorrents.Count,
                        totalSize = torrentsInCat.Sum(t => t.Size),
                        avgRatio = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => t.Ratio) : 0.0,
                        avgSeedingTime = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => (double)t.SeedingTime) : 0.0,
                        seedingConfig = new
                        {
                            maxRatio = instCfg.CategorySeeding.MaxUploadRatio,
                            maxTime = instCfg.CategorySeeding.MaxSeedingTime,
                            removeMode = instCfg.CategorySeeding.RemoveTorrent,
                            downloadLimit = instCfg.CategorySeeding.DownloadRateLimitPerTorrent,
                            uploadLimit = instCfg.CategorySeeding.UploadRateLimitPerTorrent
                        }
                    });
                }
            }
            catch { /* additional qBit instance not reachable */ }
        }

        return Results.Ok(new { categories, ready = true });
    });

    // Web Processes — reads live state from ProcessStateManager + qBit connection status
    app.MapGet("/web/processes", async (ProcessStateManager stateMgr, TorrentarrConfig cfg, QBittorrentConnectionManager qbitMgr) =>
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
            status = s.Status
        }).ToList<object>();

        // Add a process card for each configured qBit instance
        foreach (var (instanceName, qbit) in cfg.QBitInstances.Where(q => q.Value.Host != "CHANGE_ME"))
        {
            // Use IsConnected() (no params) for "qBit" to match the special case in /web/status
            // This returns true if ANY qBit client is connected
            var isConnected = instanceName == "qBit" ? qbitMgr.IsConnected() : qbitMgr.IsConnected(instanceName);

            int? totalCount = null;
            int? seedingCount = null;

            if (isConnected)
            {
                try
                {
                    // For "qBit", get any client; for named instances, get by name
                    var client = instanceName == "qBit"
                        ? qbitMgr.GetAllClients().Values.FirstOrDefault()
                        : qbitMgr.GetClient(instanceName);

                    if (client != null)
                    {
                        var torrents = await client.GetTorrentsAsync(cancellationToken: CancellationToken.None);
                        if (torrents != null)
                        {
                            totalCount = torrents.Count;
                            seedingCount = torrents.Count(t => t.State == "uploading" || t.State == "forcedUploading");
                        }
                    }
                }
                catch { /* best-effort */ }
            }

            var statusText = isConnected
                ? (totalCount.HasValue ? $"{totalCount} torrents ({seedingCount} seeding)" : "Connected")
                : "Disconnected";

            processes.Add(new
            {
                category = instanceName,
                name = instanceName,
                kind = "torrent",
                pid = (int?)null,
                alive = isConnected,
                rebuilding = false,
                searchSummary = (string?)null,
                searchTimestamp = (string?)null,
                queueCount = (int?)null,
                categoryCount = (int?)null,
                metricType = (string?)null,
                status = statusText
            });
        }

        return Results.Ok(new { processes });
    });

    // Web Restart Process — stops and restarts the named instance worker (kind is advisory; one loop per Arr)
    app.MapPost("/web/processes/{category}/{kind}/restart", async (string category, string kind, TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        var kindNorm = (kind ?? "").Trim().ToLowerInvariant();
        if (kindNorm != "search" && kindNorm != "torrent" && kindNorm != "category" && kindNorm != "arr")
            return Results.BadRequest(new { error = "kind must be search, torrent, category, or arr" });

        var instanceName = cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { status = "restarted", restarted = instanceName != null ? new[] { instanceName } : Array.Empty<string>() });
    });

    // Web Restart All Processes
    app.MapPost("/web/processes/restart_all", async (TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    // Web Arr Rebuild — same shape as RestartResponse
    app.MapPost("/web/arr/rebuild", async (TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    // Web Log Level — actually changes the Serilog level at runtime
    app.MapPost("/web/loglevel", (LoggerConfigurationRequest req, LoggingLevelSwitch ls) =>
    {
        ls.MinimumLevel = req.Level?.ToUpperInvariant() switch
        {
            "DEBUG" or "VERBOSE" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
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
    app.MapGet("/web/logs/{name}", async (string name) =>
    {
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

    // Web Arr List
    app.MapGet("/web/arr", async (TorrentarrConfig cfg, TorrentarrDbContext db) =>
    {
        var arr = cfg.ArrInstances.Select(kvp => new
        {
            category = kvp.Value.Category,
            name = kvp.Key,
            type = kvp.Value.Type,
            alive = kvp.Value.URI != "CHANGE_ME"
        }).ToList();

        var radarrAvailable = await db.Movies.CountAsync(m => m.MovieFileId != 0);
        var radarrMonitored = await db.Movies.CountAsync(m => m.Monitored);
        var sonarrAvailable = await db.Episodes.CountAsync(e => e.EpisodeFileId != null && e.EpisodeFileId != 0);
        var sonarrMonitored = await db.Episodes.CountAsync(e => e.Monitored == true);
        var lidarrAvailable = await db.Tracks.CountAsync(t => t.HasFile);
        var lidarrMonitored = await db.Tracks.CountAsync(t => t.Monitored);
        var counts = new
        {
            radarr = new { available = radarrAvailable, monitored = radarrMonitored },
            sonarr = new { available = sonarrAvailable, monitored = sonarrMonitored },
            lidarr = new { available = lidarrAvailable, monitored = lidarrMonitored }
        };

        return Results.Ok(new { arr, ready = true, counts });
    });

    // Web Radarr Movies
    app.MapGet("/web/radarr/{category}/movies", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, int? year_min, int? year_max, bool? monitored, bool? has_file, bool? quality_met, bool? is_request) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Movies.Where(m => m.ArrInstance == category);
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(m => m.Title.Contains(q));
        if (year_min.HasValue)
            query = query.Where(m => m.Year >= year_min.Value);
        if (year_max.HasValue)
            query = query.Where(m => m.Year <= year_max.Value);
        if (monitored.HasValue)
            query = query.Where(m => m.Monitored == monitored.Value);
        if (has_file.HasValue)
            query = query.Where(m => has_file.Value ? m.MovieFileId != 0 : m.MovieFileId == 0);
        if (quality_met.HasValue)
            query = query.Where(m => m.QualityMet == quality_met.Value);
        if (is_request.HasValue)
            query = query.Where(m => m.IsRequest == is_request.Value);

        var total = await baseQuery.CountAsync();
        var availableCount = await baseQuery.CountAsync(m => m.MovieFileId != 0);
        var monitoredCount = await baseQuery.CountAsync(m => m.Monitored);

        var movies = await query
            .OrderBy(m => m.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(m => new
            {
                id = m.EntryId,
                title = m.Title,
                year = m.Year,
                monitored = m.Monitored,
                hasFile = m.MovieFileId != 0,
                qualityMet = m.QualityMet,
                isRequest = m.IsRequest,
                upgrade = m.Upgrade,
                customFormatScore = m.CustomFormatScore,
                minCustomFormatScore = m.MinCustomFormatScore,
                customFormatMet = m.CustomFormatMet,
                reason = m.Reason,
                qualityProfileId = m.QualityProfileId,
                qualityProfileName = m.QualityProfileName
            })
            .ToListAsync();

        return Results.Ok(new
        {
            category,
            counts = new { available = availableCount, monitored = monitoredCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            movies
        });
    });

    // Web Sonarr Series — seasons populated from episodes table
    app.MapGet("/web/sonarr/{category}/series", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, int? missing) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Series.Where(s => s.ArrInstance == category);
        var query = baseQuery;

        // Apply missing=1 filter: only series that have at least one episode without a file
        if (missing == 1)
        {
            var missingSeriesIds = await db.Episodes
                .Where(e => e.ArrInstance == category && (e.EpisodeFileId == null || e.EpisodeFileId == 0))
                .Select(e => e.SeriesId)
                .Distinct()
                .ToListAsync();
            baseQuery = baseQuery.Where(s => missingSeriesIds.Contains(s.EntryId));
            query = baseQuery;
        }

        if (!string.IsNullOrEmpty(q))
            query = query.Where(s => s.Title != null && s.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var monitoredCount = await baseQuery.CountAsync(s => s.Monitored == true);

        var seriesPage = await query
            .OrderBy(s => s.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(s => new { s.EntryId, s.Title, s.Monitored, s.QualityProfileId, s.QualityProfileName })
            .ToListAsync();

        var seriesIds = seriesPage.Select(s => s.EntryId).ToList();

        // Load per-season episode counts for this page of series
        var seasonGroups = await db.Episodes
            .Where(e => e.ArrInstance == category && seriesIds.Contains(e.SeriesId))
            .GroupBy(e => new { e.SeriesId, e.SeasonNumber })
            .Select(g => new
            {
                g.Key.SeriesId,
                g.Key.SeasonNumber,
                TotalCount = g.Count(),
                HasFileCount = g.Count(e => e.EpisodeFileId != null && e.EpisodeFileId != 0),
                MonitoredCount = g.Count(e => e.Monitored == true)
            })
            .ToListAsync();

        // Aggregate available episode count across entire instance for the counts field
        var totalAvailableEpisodes = await db.Episodes
            .CountAsync(e => e.ArrInstance == category && e.EpisodeFileId != null && e.EpisodeFileId != 0);

        var seriesList = seriesPage.Select(s =>
        {
            var seriesSeasonGroups = seasonGroups.Where(g => g.SeriesId == s.EntryId).ToList();
            var seriesAvailable = seriesSeasonGroups.Sum(g => g.HasFileCount);
            var seriesMonitored = seriesSeasonGroups.Sum(g => g.MonitoredCount);
            var seriesTotal = seriesSeasonGroups.Sum(g => g.TotalCount);

            // SonarrSeason: { monitored: number, available: number, missing?: number, episodes: [] }
            var seasons = seriesSeasonGroups
                .ToDictionary(
                    g => g.SeasonNumber.ToString(),
                    g => (object)new
                    {
                        monitored = g.MonitoredCount,
                        available = g.HasFileCount,
                        missing = g.TotalCount - g.HasFileCount,
                        episodes = Array.Empty<object>()
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
            counts = new { available = totalAvailableEpisodes, monitored = monitoredCount },
            series = seriesList
        });
    });

    // Web Lidarr Albums — tracks populated from tracks table
    app.MapGet("/web/lidarr/{category}/albums", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, bool? monitored, bool? has_file, bool? quality_met, bool? is_request, bool? flat_mode) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Albums.Where(a => a.ArrInstance == category);
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
            var trackBaseQuery = db.Tracks.Where(t => t.ArrInstance == category);
            var trackTotal = await trackBaseQuery.CountAsync();
            var trackAvailable = await trackBaseQuery.CountAsync(t => t.HasFile);
            var trackMonitored = await trackBaseQuery.CountAsync(t => t.Monitored);
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
        var availableCount = await baseQuery.CountAsync(a => a.AlbumFileId != 0);
        var monitoredCount = await baseQuery.CountAsync(a => a.Monitored);

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
            .Where(t => t.ArrInstance == category && albumIds.Contains(t.AlbumId))
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
            counts = new { available = availableCount, monitored = monitoredCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            albums
        });
    });

    // Web Lidarr Tracks — paginated flat track list for a Lidarr instance
    app.MapGet("/web/lidarr/{category}/tracks", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Tracks.Where(t => t.ArrInstance == category);
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
    app.MapPost("/web/arr/{category}/restart", async (string category, TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        var instanceName = cfg.ArrInstances
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
    app.MapPost("/web/config", async (HttpRequest request, TorrentarrConfig cfg, ConfigurationLoader loader) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (!payload.TryGetProperty("changes", out var changesEl))
                return Results.BadRequest(new { error = "Missing 'changes' field" });

            var newtonsoftSettings = new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
                // Replace collections on deserialization to avoid appending to constructor-initialized defaults
                ObjectCreationHandling = Newtonsoft.Json.ObjectCreationHandling.Replace,
            };
            var serializer = Newtonsoft.Json.JsonSerializer.Create(newtonsoftSettings);

            // Step 1: Snapshot current config as a flat-section JObject (mirrors GET /web/config).
            // Keys are section names ("Settings", "WebUI", "qBit", "Radarr-1080", …).
            var currentObj = new Newtonsoft.Json.Linq.JObject();
            currentObj["Settings"] = Newtonsoft.Json.Linq.JObject.FromObject(cfg.Settings, serializer);
            currentObj["WebUI"] = Newtonsoft.Json.Linq.JObject.FromObject(cfg.WebUI, serializer);
            foreach (var (key, qbit) in cfg.QBitInstances.Where(kv => kv.Value.Host != "CHANGE_ME"))
                currentObj[key] = Newtonsoft.Json.Linq.JObject.FromObject(qbit, serializer);
            foreach (var (key, arr) in cfg.ArrInstances)
                currentObj[key] = Newtonsoft.Json.Linq.JObject.FromObject(arr, serializer);

            // Step 2: Apply each dotted-key change onto the snapshot.
            // e.g. "Settings.ConsoleLevel" → sets currentObj["Settings"]["ConsoleLevel"].
            // null value means delete.
            var changesObj = Newtonsoft.Json.Linq.JObject.Parse(changesEl.GetRawText());
            foreach (var change in changesObj.Properties())
            {
                // Reject protected keys (qBitrr parity)
                if (string.Equals(change.Name, "Settings.ConfigVersion", StringComparison.OrdinalIgnoreCase))
                    return Results.Json(new { error = "Cannot modify protected configuration key: Settings.ConfigVersion" }, statusCode: 403);

                // Never overwrite a real secret with the redaction placeholder from the frontend
                if (IsSensitiveDottedKey(change.Name) &&
                    change.Value.Type == Newtonsoft.Json.Linq.JTokenType.String &&
                    change.Value.ToString() == REDACTED_PLACEHOLDER)
                    continue;

                var parts = change.Name.Split('.');
                var rawSectionKey = parts[0];
                // Case-insensitive section key: "webui" → "WebUI", "settings" → "Settings"
                var sectionKey = currentObj.Properties()
                    .FirstOrDefault(p => p.Name.Equals(rawSectionKey, StringComparison.OrdinalIgnoreCase))?.Name
                    ?? rawSectionKey;
                if (change.Value.Type == Newtonsoft.Json.Linq.JTokenType.Null)
                {
                    // Deletion
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

            // Cleanup: remove sections that had all their keys deleted (became empty {}).
            // This handles renames: the old section has all sub-keys set to null → empty JObject.
            foreach (var emptyProp in currentObj.Properties().ToList())
            {
                if (emptyProp.Value is Newtonsoft.Json.Linq.JObject emptyObj && !emptyObj.Properties().Any())
                    currentObj.Remove(emptyProp.Name);
            }

            // Step 3: Reconstruct TorrentarrConfig from the updated flat-section JObject.
            var updatedConfig = new TorrentarrConfig();
            if (currentObj["Settings"] is Newtonsoft.Json.Linq.JObject settingsObj)
                updatedConfig.Settings = settingsObj.ToObject<SettingsConfig>(serializer) ?? new SettingsConfig();
            if (currentObj["WebUI"] is Newtonsoft.Json.Linq.JObject webuiObj)
                updatedConfig.WebUI = webuiObj.ToObject<WebUIConfig>(serializer) ?? new WebUIConfig();

            foreach (var prop in currentObj.Properties())
            {
                if (prop.Value is not Newtonsoft.Json.Linq.JObject sectionObj) continue;
                var lower = prop.Name.ToLowerInvariant();
                bool isRadarr = lower == "radarr" || lower.StartsWith("radarr-");
                bool isSonarr = lower == "sonarr" || lower.StartsWith("sonarr-");
                bool isLidarr = lower == "lidarr" || lower.StartsWith("lidarr-");
                bool isQbit = lower == "qbit" || lower.StartsWith("qbit-");
                if (isRadarr || isSonarr || isLidarr)
                {
                    var arrConfig = sectionObj.ToObject<ArrInstanceConfig>(serializer) ?? new ArrInstanceConfig();
                    if (string.IsNullOrEmpty(arrConfig.Type))
                        arrConfig.Type = isRadarr ? "radarr" : isSonarr ? "sonarr" : "lidarr";
                    updatedConfig.ArrInstances[prop.Name] = arrConfig;
                }
                else if (isQbit)
                {
                    updatedConfig.QBitInstances[prop.Name] = sectionObj.ToObject<QBitConfig>(serializer) ?? new QBitConfig();
                }
            }

            var (reloadType, affectedInstancesList) = DetermineReloadType(cfg, updatedConfig);

            loader.SaveConfig(updatedConfig);
            cfg.Settings = updatedConfig.Settings;
            cfg.WebUI = updatedConfig.WebUI;
            cfg.ArrInstances = updatedConfig.ArrInstances;
            cfg.QBitInstances = updatedConfig.QBitInstances;
            TorrentPolicyHelper.InvalidateMonitoredPolicyCategoriesCache(cfg);
            return Results.Ok(new
            {
                status = "ok",
                configReloaded = reloadType != "none" && reloadType != "frontend",
                reloadType,
                affectedInstances = affectedInstancesList
            });
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
    app.MapGet("/web/logout", async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login", false);
    });
    app.MapPost("/web/logout", async (HttpContext ctx) =>
    {
        await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Redirect("/login", false);
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

        var setupToken = Environment.GetEnvironmentVariable("TORRENTARR_SETUP_TOKEN");
        var allowSet = string.IsNullOrEmpty(cfg.WebUI.PasswordHash)
            || (!string.IsNullOrWhiteSpace(setupToken) && body.SetupToken != null
                && WebUIAuthHelpers.TokenEquals(body.SetupToken, setupToken));
        if (!allowSet)
            return Results.Json(new { error = "Set password not allowed" }, statusCode: 403);

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
    app.MapGet("/api/qbit/categories", async (QBittorrentConnectionManager qbitManager, TorrentarrConfig cfg) =>
    {
        var categories = new List<object>();

        var arrCategoryToConfig = cfg.ArrInstances
            .Where(kvp => !string.IsNullOrEmpty(kvp.Value.Category))
            .ToDictionary(kvp => kvp.Value.Category!, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

        var primaryQbit2 = (cfg.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig());
        var qbitManagedSet = new HashSet<string>(primaryQbit2.ManagedCategories, StringComparer.OrdinalIgnoreCase);
        var arrCategorySet = new HashSet<string>(arrCategoryToConfig.Keys, StringComparer.OrdinalIgnoreCase);
        var monitoredForDefault = new HashSet<string>(qbitManagedSet.Union(arrCategorySet), StringComparer.OrdinalIgnoreCase);

        if (primaryQbit2.Host != "CHANGE_ME" && monitoredForDefault.Count > 0)
        {
            try
            {
                var client = qbitManager.GetAllClients().Values.FirstOrDefault();
                if (client != null)
                {
                    var allTorrents = await client.GetTorrentsAsync();
                    foreach (var catName in monitoredForDefault)
                    {
                        var torrentsInCat = allTorrents.Where(t => t.Category == catName).ToList();
                        var seedingTorrents = torrentsInCat.Where(t =>
                            t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase) ||
                            t.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)).ToList();

                        var managedBy = arrCategorySet.Contains(catName) ? "arr" : "qbit";

                        double maxRatio = primaryQbit2.CategorySeeding.MaxUploadRatio;
                        int maxTime = primaryQbit2.CategorySeeding.MaxSeedingTime;
                        int removeMode = primaryQbit2.CategorySeeding.RemoveTorrent;
                        int dlLimit = primaryQbit2.CategorySeeding.DownloadRateLimitPerTorrent;
                        int ulLimit = primaryQbit2.CategorySeeding.UploadRateLimitPerTorrent;

                        if (arrCategoryToConfig.TryGetValue(catName, out var arrInstCfg))
                        {
                            var sm = arrInstCfg.Torrent?.SeedingMode;
                            if (sm != null)
                            {
                                maxRatio = sm.MaxUploadRatio;
                                maxTime = sm.MaxSeedingTime;
                                removeMode = sm.RemoveTorrent;
                                dlLimit = sm.DownloadRateLimitPerTorrent;
                                ulLimit = sm.UploadRateLimitPerTorrent;
                            }
                        }

                        categories.Add(new
                        {
                            category = catName,
                            instance = "qBit",
                            managedBy,
                            torrentCount = torrentsInCat.Count,
                            seedingCount = seedingTorrents.Count,
                            totalSize = torrentsInCat.Sum(t => t.Size),
                            avgRatio = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => t.Ratio) : 0.0,
                            avgSeedingTime = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => (double)t.SeedingTime) : 0.0,
                            seedingConfig = new { maxRatio, maxTime, removeMode, downloadLimit = dlLimit, uploadLimit = ulLimit }
                        });
                    }
                }
            }
            catch { }
        }

        foreach (var (instName, instCfg) in cfg.QBitInstances.Where(q => q.Key != "qBit" && q.Value.Host != "CHANGE_ME"))
        {
            if (instCfg.ManagedCategories.Count == 0) continue;
            try
            {
                var addlClient = qbitManager.GetClient(instName);
                if (addlClient == null) continue;
                var addlTorrents = await addlClient.GetTorrentsAsync();
                foreach (var catName in instCfg.ManagedCategories)
                {
                    var torrentsInCat = addlTorrents.Where(t => t.Category == catName).ToList();
                    var seedingTorrents = torrentsInCat.Where(t =>
                        t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase) ||
                        t.State.Equals("uploading", StringComparison.OrdinalIgnoreCase)).ToList();
                    categories.Add(new
                    {
                        category = catName,
                        instance = instName,
                        managedBy = "qbit",
                        torrentCount = torrentsInCat.Count,
                        seedingCount = seedingTorrents.Count,
                        totalSize = torrentsInCat.Sum(t => t.Size),
                        avgRatio = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => t.Ratio) : 0.0,
                        avgSeedingTime = torrentsInCat.Count > 0 ? torrentsInCat.Average(t => (double)t.SeedingTime) : 0.0,
                        seedingConfig = new
                        {
                            maxRatio = instCfg.CategorySeeding.MaxUploadRatio,
                            maxTime = instCfg.CategorySeeding.MaxSeedingTime,
                            removeMode = instCfg.CategorySeeding.RemoveTorrent,
                            downloadLimit = instCfg.CategorySeeding.DownloadRateLimitPerTorrent,
                            uploadLimit = instCfg.CategorySeeding.UploadRateLimitPerTorrent
                        }
                    });
                }
            }
            catch { }
        }

        return Results.Ok(new { categories, ready = true });
    });

    // ==================== /api/* endpoints (Bearer token protected via middleware) ====================

    app.MapGet("/api/meta", async (UpdateService updater, int? force) =>
    {
        await updater.CheckForUpdateAsync(forceRefresh: force.GetValueOrDefault() != 0);
        return Results.Ok(updater.BuildMetaResponse());
    });

    app.MapGet("/api/status", async (TorrentarrConfig cfg, QBittorrentConnectionManager qbitManager) =>
    {
        var apiPrimaryQbit = (cfg.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig());
        var qbitConfigured = apiPrimaryQbit.Host != "CHANGE_ME" && !string.IsNullOrEmpty(apiPrimaryQbit.Host);
        var qbitAlive = qbitConfigured && qbitManager.IsConnected();

        string? qbitVersion = null;
        if (qbitAlive)
        {
            var client = qbitManager.GetAllClients().Values.FirstOrDefault();
            if (client != null)
                try { qbitVersion = await client.GetVersionAsync(); } catch { /* best-effort */ }
        }

        var qbitInstances2 = new Dictionary<string, object>();
        foreach (var (name2, qbit2) in cfg.QBitInstances)
        {
            var alive2 = name2 == "qBit" ? qbitAlive : qbitManager.IsConnected(name2);
            string? ver2 = name2 == "qBit" ? qbitVersion : null;
            if (alive2 && name2 != "qBit")
            {
                var addlClient2 = qbitManager.GetClient(name2);
                if (addlClient2 != null)
                    try { ver2 = await addlClient2.GetVersionAsync(); } catch { /* best-effort */ }
            }
            qbitInstances2[name2] = new { alive = alive2, host = qbit2.Host, port = qbit2.Port, version = ver2 };
        }
        return Results.Ok(new
        {
            qbit = new
            {
                alive = qbitAlive,
                host = qbitConfigured ? apiPrimaryQbit.Host : (string?)null,
                port = qbitConfigured ? (int?)apiPrimaryQbit.Port : null,
                version = qbitVersion
            },
            qbitInstances = qbitInstances2,
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

    app.MapGet("/api/processes", (ProcessStateManager stateMgr) =>
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
            metricType = s.MetricType
        }).ToList();
        return Results.Ok(new { processes });
    });

    app.MapPost("/api/processes/{category}/{kind}/restart", async (string category, string kind, TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        var kindNorm = (kind ?? "").Trim().ToLowerInvariant();
        if (kindNorm != "search" && kindNorm != "torrent" && kindNorm != "category" && kindNorm != "arr")
            return Results.BadRequest(new { error = "kind must be search, torrent, category, or arr" });

        var instanceName = cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { status = "restarted", restarted = instanceName != null ? new[] { instanceName } : Array.Empty<string>() });
    });

    app.MapPost("/api/processes/restart_all", async (TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    app.MapPost("/api/arr/rebuild", async (TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        await workerMgr.RestartAllWorkersAsync();
        return Results.Ok(new { status = "restarted", restarted = cfg.ArrInstances.Keys.ToArray() });
    });

    app.MapPost("/api/loglevel", (LoggerConfigurationRequest req, LoggingLevelSwitch ls) =>
    {
        ls.MinimumLevel = req.Level?.ToUpperInvariant() switch
        {
            "DEBUG" or "VERBOSE" => LogEventLevel.Debug,
            "WARNING" or "WARN" => LogEventLevel.Warning,
            "ERROR" => LogEventLevel.Error,
            "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
            _ => LogEventLevel.Information
        };
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

    app.MapGet("/api/logs/{name}", async (string name) =>
    {
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

    app.MapGet("/api/arr", async (TorrentarrConfig cfg, TorrentarrDbContext db) =>
    {
        var arr = cfg.ArrInstances.Select(kvp => new
        {
            category = kvp.Value.Category,
            name = kvp.Key,
            type = kvp.Value.Type,
            alive = kvp.Value.URI != "CHANGE_ME"
        }).ToList();

        var radarrAvailable = await db.Movies.CountAsync(m => m.MovieFileId != 0);
        var radarrMonitored = await db.Movies.CountAsync(m => m.Monitored);
        var sonarrAvailable = await db.Episodes.CountAsync(e => e.EpisodeFileId != null && e.EpisodeFileId != 0);
        var sonarrMonitored = await db.Episodes.CountAsync(e => e.Monitored == true);
        var lidarrAvailable = await db.Tracks.CountAsync(t => t.HasFile);
        var lidarrMonitored = await db.Tracks.CountAsync(t => t.Monitored);
        var counts = new
        {
            radarr = new { available = radarrAvailable, monitored = radarrMonitored },
            sonarr = new { available = sonarrAvailable, monitored = sonarrMonitored },
            lidarr = new { available = lidarrAvailable, monitored = lidarrMonitored }
        };

        return Results.Ok(new { arr, ready = true, counts });
    });

    app.MapPost("/api/arr/{section}/restart", async (string section, TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
        var instanceName = cfg.ArrInstances
            .FirstOrDefault(kv => kv.Value.Category.Equals(section, StringComparison.OrdinalIgnoreCase)).Key;
        if (instanceName != null)
            await workerMgr.RestartWorkerAsync(instanceName);
        return Results.Ok(new { success = instanceName != null, message = instanceName != null ? $"Restarted {instanceName}" : $"No worker found for category '{section}'" });
    });

    app.MapGet("/api/radarr/{category}/movies", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, int? year_min, int? year_max, bool? monitored, bool? has_file, bool? quality_met, bool? is_request) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Movies.Where(m => m.ArrInstance == category);
        var query = baseQuery;
        if (!string.IsNullOrEmpty(q))
            query = query.Where(m => m.Title.Contains(q));
        if (year_min.HasValue)
            query = query.Where(m => m.Year >= year_min.Value);
        if (year_max.HasValue)
            query = query.Where(m => m.Year <= year_max.Value);
        if (monitored.HasValue)
            query = query.Where(m => m.Monitored == monitored.Value);
        if (has_file.HasValue)
            query = query.Where(m => has_file.Value ? m.MovieFileId != 0 : m.MovieFileId == 0);
        if (quality_met.HasValue)
            query = query.Where(m => m.QualityMet == quality_met.Value);
        if (is_request.HasValue)
            query = query.Where(m => m.IsRequest == is_request.Value);

        var total = await baseQuery.CountAsync();
        var availableCount = await baseQuery.CountAsync(m => m.MovieFileId != 0);
        var monitoredCount = await baseQuery.CountAsync(m => m.Monitored);

        var movies = await query
            .OrderBy(m => m.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(m => new
            {
                id = m.EntryId,
                title = m.Title,
                year = m.Year,
                monitored = m.Monitored,
                hasFile = m.MovieFileId != 0,
                qualityMet = m.QualityMet,
                isRequest = m.IsRequest,
                upgrade = m.Upgrade,
                customFormatScore = m.CustomFormatScore,
                minCustomFormatScore = m.MinCustomFormatScore,
                customFormatMet = m.CustomFormatMet,
                reason = m.Reason,
                qualityProfileId = m.QualityProfileId,
                qualityProfileName = m.QualityProfileName
            })
            .ToListAsync();

        return Results.Ok(new
        {
            category,
            counts = new { available = availableCount, monitored = monitoredCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            movies
        });
    });

    app.MapGet("/api/sonarr/{category}/series", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, int? missing) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Series.Where(s => s.ArrInstance == category);
        var query = baseQuery;

        if (missing == 1)
        {
            var missingSeriesIds = await db.Episodes
                .Where(e => e.ArrInstance == category && (e.EpisodeFileId == null || e.EpisodeFileId == 0))
                .Select(e => e.SeriesId)
                .Distinct()
                .ToListAsync();
            baseQuery = baseQuery.Where(s => missingSeriesIds.Contains(s.EntryId));
            query = baseQuery;
        }

        if (!string.IsNullOrEmpty(q))
            query = query.Where(s => s.Title != null && s.Title.Contains(q));

        var total = await baseQuery.CountAsync();
        var monitoredCount = await baseQuery.CountAsync(s => s.Monitored == true);

        var seriesPage = await query
            .OrderBy(s => s.Title)
            .Skip(skip)
            .Take(currentPageSize)
            .Select(s => new { s.EntryId, s.Title, s.Monitored, s.QualityProfileId, s.QualityProfileName })
            .ToListAsync();

        var seriesIds = seriesPage.Select(s => s.EntryId).ToList();

        var seasonGroups = await db.Episodes
            .Where(e => e.ArrInstance == category && seriesIds.Contains(e.SeriesId))
            .GroupBy(e => new { e.SeriesId, e.SeasonNumber })
            .Select(g => new
            {
                g.Key.SeriesId,
                g.Key.SeasonNumber,
                TotalCount = g.Count(),
                HasFileCount = g.Count(e => e.EpisodeFileId != null && e.EpisodeFileId != 0),
                MonitoredCount = g.Count(e => e.Monitored == true)
            })
            .ToListAsync();

        var totalAvailableEpisodes2 = await db.Episodes
            .CountAsync(e => e.ArrInstance == category && e.EpisodeFileId != null && e.EpisodeFileId != 0);

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
                        episodes = Array.Empty<object>()
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
            counts = new { available = totalAvailableEpisodes2, monitored = monitoredCount },
            series = seriesList
        });
    });

    app.MapGet("/api/lidarr/{category}/albums", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q, bool? monitored, bool? has_file, bool? quality_met, bool? is_request, bool? flat_mode) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Albums.Where(a => a.ArrInstance == category);
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
            var trackBaseQuery = db.Tracks.Where(t => t.ArrInstance == category);
            var trackTotal = await trackBaseQuery.CountAsync();
            var trackAvailable = await trackBaseQuery.CountAsync(t => t.HasFile);
            var trackMonitored = await trackBaseQuery.CountAsync(t => t.Monitored);
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
        var availableCount = await baseQuery.CountAsync(a => a.AlbumFileId != 0);
        var monitoredCount = await baseQuery.CountAsync(a => a.Monitored);

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
            .Where(t => t.ArrInstance == category && albumIds.Contains(t.AlbumId))
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
            counts = new { available = availableCount, monitored = monitoredCount },
            total,
            page = currentPage,
            page_size = currentPageSize,
            albums
        });
    });

    app.MapGet("/api/lidarr/{category}/tracks", async (string category, TorrentarrDbContext db, int? page, int? page_size, string? q) =>
    {
        var currentPage = page ?? 0;
        var currentPageSize = page_size ?? 50;
        var skip = currentPage * currentPageSize;

        var baseQuery = db.Tracks.Where(t => t.ArrInstance == category);
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

    app.MapGet("/api/config", (TorrentarrConfig cfg) => Results.Ok(cfg));

    app.MapPost("/api/config", async (HttpRequest request, TorrentarrConfig cfg, ConfigurationLoader loader) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            string configJson;
            if (payload.TryGetProperty("changes", out var changesEl))
                configJson = changesEl.GetRawText();
            else
                configJson = payload.GetRawText();
            var updatedConfig = Newtonsoft.Json.JsonConvert.DeserializeObject<TorrentarrConfig>(configJson);
            if (updatedConfig == null)
                return Results.BadRequest(new { error = "Invalid config payload" });

            // Determine reload type by comparing old vs new config before applying
            var (reloadType, affectedInstancesList) = DetermineReloadType(cfg, updatedConfig);

            loader.SaveConfig(updatedConfig);
            cfg.Settings = updatedConfig.Settings;
            cfg.WebUI = updatedConfig.WebUI;
            cfg.ArrInstances = updatedConfig.ArrInstances;
            cfg.QBitInstances = updatedConfig.QBitInstances;
            TorrentPolicyHelper.InvalidateMonitoredPolicyCategoriesCache(cfg);
            return Results.Ok(new
            {
                status = "ok",
                configReloaded = reloadType != "none" && reloadType != "frontend",
                reloadType,
                affectedInstances = affectedInstancesList
            });
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

// ── Manual DB migrations (columns added after initial EnsureCreated) ──────
static void ApplyManualMigrations(TorrentarrDbContext db)
{
    // Add tvdbid to seriesfilesmodel if it doesn't exist (added in v1.1)
    AddColumnIfMissing(db, "seriesfilesmodel", "tvdbid", "INTEGER NOT NULL DEFAULT 0");

    // Add availability fields for Radarr (added in logging enhancement)
    AddColumnIfMissing(db, "moviesfilesmodel", "InCinemas", "TEXT");
    AddColumnIfMissing(db, "moviesfilesmodel", "DigitalRelease", "TEXT");
    AddColumnIfMissing(db, "moviesfilesmodel", "PhysicalRelease", "TEXT");
    AddColumnIfMissing(db, "moviesfilesmodel", "MinimumAvailability", "TEXT");

    // Add availability fields for Sonarr
    AddColumnIfMissing(db, "episodefilesmodel", "InCinemas", "TEXT");
    AddColumnIfMissing(db, "episodefilesmodel", "DigitalRelease", "TEXT");
    AddColumnIfMissing(db, "episodefilesmodel", "PhysicalRelease", "TEXT");
    AddColumnIfMissing(db, "episodefilesmodel", "MinimumAvailability", "TEXT");

    // Add availability fields for Lidarr
    AddColumnIfMissing(db, "albumfilesmodel", "InCinemas", "TEXT");
    AddColumnIfMissing(db, "albumfilesmodel", "DigitalRelease", "TEXT");
    AddColumnIfMissing(db, "albumfilesmodel", "PhysicalRelease", "TEXT");
    AddColumnIfMissing(db, "albumfilesmodel", "MinimumAvailability", "TEXT");

    // §5: Search activity table for Processes page (qBitrr parity)
    CreateTableIfMissing(db, "searchactivity", "CREATE TABLE IF NOT EXISTS searchactivity ( category TEXT NOT NULL PRIMARY KEY, summary TEXT, timestamp TEXT );");

    // qBitrr parity: one-time cleanup of legacy rows with blank ArrInstance (not every startup: avoids repeat DELETE I/O
    // and preserves operator-visible bad data if a bug reintroduces blank keys).
    CreateTableIfMissing(
        db,
        "torrentarr_manual_migrations",
        "CREATE TABLE IF NOT EXISTS torrentarr_manual_migrations ( name TEXT NOT NULL PRIMARY KEY );");
    const string emptyArrInstanceCleanup = "empty_arrinstance_row_cleanup_v1";
    if (!IsManualMigrationApplied(db, emptyArrInstanceCleanup))
    {
        DeleteRowsWithEmptyArrInstance(db, "moviesfilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "episodefilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "seriesfilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "albumfilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "artistfilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "trackfilesmodel");
        DeleteRowsWithEmptyArrInstance(db, "moviequeuemodel");
        DeleteRowsWithEmptyArrInstance(db, "episodequeuemodel");
        DeleteRowsWithEmptyArrInstance(db, "albumqueuemodel");
        DeleteRowsWithEmptyArrInstance(db, "filesqueued");
        MarkManualMigrationApplied(db, emptyArrInstanceCleanup);
    }

    // qBitrr parity: ensure ArrInstance indexes exist even on upgraded DBs.
    CreateIndexIfMissing(db, "idx_arrinstance_movies", "moviesfilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_episodes", "episodefilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_series", "seriesfilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_albums", "albumfilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_artists", "artistfilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_tracks", "trackfilesmodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_moviequeue", "moviequeuemodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_episodequeue", "episodequeuemodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_albumqueue", "albumqueuemodel", "arrinstance");
    CreateIndexIfMissing(db, "idx_arrinstance_filesqueued", "filesqueued", "arrinstance");
}

static void CreateTableIfMissing(TorrentarrDbContext db, string tableName, string createSql)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=@name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = tableName;
        cmd.Parameters.Add(p);
        var exists = cmd.ExecuteScalar() != null;
        if (!exists)
        {
            using var create = conn.CreateCommand();
            create.CommandText = createSql;
            create.ExecuteNonQuery();
        }
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

static void AddColumnIfMissing(TorrentarrDbContext db, string table, string column, string columnDef)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table});";
        using var reader = cmd.ExecuteReader();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            columns.Add(reader.GetString(1)); // column 1 = name
        reader.Close();

        if (!columns.Contains(column))
        {
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {columnDef};";
            alter.ExecuteNonQuery();
        }
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

static void DeleteRowsWithEmptyArrInstance(TorrentarrDbContext db, string table)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DELETE FROM {table} WHERE arrinstance IS NULL OR TRIM(arrinstance)='';";
        cmd.ExecuteNonQuery();
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

static bool IsManualMigrationApplied(TorrentarrDbContext db, string name)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM torrentarr_manual_migrations WHERE name = @name LIMIT 1;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = name;
        cmd.Parameters.Add(p);
        return cmd.ExecuteScalar() != null;
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

static void MarkManualMigrationApplied(TorrentarrDbContext db, string name)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO torrentarr_manual_migrations (name) VALUES (@name);";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = name;
        cmd.Parameters.Add(p);
        cmd.ExecuteNonQuery();
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

static void CreateIndexIfMissing(TorrentarrDbContext db, string indexName, string table, string column)
{
    var conn = db.Database.GetDbConnection();
    var wasOpen = conn.State == System.Data.ConnectionState.Open;
    if (!wasOpen) conn.Open();
    try
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='index' AND name=@name;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@name";
        p.Value = indexName;
        cmd.Parameters.Add(p);
        var exists = cmd.ExecuteScalar() != null;
        if (!exists)
        {
            using var create = conn.CreateCommand();
            create.CommandText = $"CREATE INDEX {indexName} ON {table}({column});";
            create.ExecuteNonQuery();
        }
    }
    finally
    {
        if (!wasOpen) conn.Close();
    }
}

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

        // When instanceKey is provided, load URI and APIKey from config (e.g. when API key is redacted in UI)
        if (!string.IsNullOrEmpty(req.InstanceKey))
        {
            if (string.IsNullOrEmpty(req.ArrType))
                return Results.BadRequest(new { success = false, message = "Missing required field: arrType" });

            if (!cfg.ArrInstances.TryGetValue(req.InstanceKey, out var arrCfg))
                return Results.Ok(new { success = false, message = "Instance not found or missing URI/APIKey in config" });

            uri = arrCfg.URI;
            apiKey = arrCfg.APIKey;
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
                var radarr = new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(uri, apiKey);
                getSystemInfo = () => radarr.GetSystemInfoAsync();
                getProfiles = () => radarr.GetQualityProfilesAsync();
                break;
            case "sonarr":
                var sonarr = new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(uri, apiKey);
                getSystemInfo = () => sonarr.GetSystemInfoAsync();
                getProfiles = () => sonarr.GetQualityProfilesAsync();
                break;
            case "lidarr":
                var lidarr = new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(uri, apiKey);
                getSystemInfo = () => lidarr.GetSystemInfoAsync();
                getProfiles = () => lidarr.GetQualityProfilesAsync();
                break;
            default:
                return Results.BadRequest(new { error = $"Invalid arrType: {req.ArrType}" });
        }

        // Get system info to verify connection
        systemInfo = await getSystemInfo();

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
            message = $"Connected to {req.ArrType} {systemInfo!.Version}",
            systemInfo = new { version = systemInfo.Version ?? "unknown", branch = (string?)null },
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
    bool hasQBitChanges = serialize(oldCfg.QBitInstances) != serialize(newCfg.QBitInstances);

    // Settings changes → webui reload (workers pick up changes at next cycle)
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
    private readonly QBittorrentConnectionManager _qbitManager;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ProcessStateManager _stateManager;
    private long _currentFreeSpace;
    private long _minFreeSpaceBytes;
    private string? _freeSpaceFolder;
    private bool _qbitConfigured;
    private bool _freeSpaceEnabled;

    public ProcessOrchestratorService(
        ILogger<ProcessOrchestratorService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager,
        IServiceScopeFactory scopeFactory,
        ProcessStateManager stateManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _scopeFactory = scopeFactory;
        _stateManager = stateManager;
        // §8: Respect Settings.FreeSpace string ("-1" = disabled, "10G"/"500M" = threshold)
        var freeSpaceBytes = ParseFreeSpaceString(_config.Settings.FreeSpace);
        if (freeSpaceBytes < 0)
        {
            _freeSpaceEnabled = false;
            _minFreeSpaceBytes = (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;
        }
        else
        {
            _freeSpaceEnabled = true;
            _minFreeSpaceBytes = freeSpaceBytes;
        }
        _qbitConfigured = config.QBitInstances.Values.Any(q =>
            !q.Disabled && q.Host != "CHANGE_ME" && q.UserName != "CHANGE_ME" && q.Password != "CHANGE_ME");
    }

    /// <summary>Parse qBitrr FreeSpace string: "-1" = disabled, "10G"/"500M"/"1024K" or raw number = threshold bytes.</summary>
    private static long ParseFreeSpaceString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "-1") return -1;
        var v = value.Trim().ToUpperInvariant();
        try
        {
            if (v.EndsWith("G")) return long.Parse(v[..^1]) * 1024L * 1024L * 1024L;
            if (v.EndsWith("M")) return long.Parse(v[..^1]) * 1024L * 1024L;
            if (v.EndsWith("K")) return long.Parse(v[..^1]) * 1024L;
            return long.Parse(v);
        }
        catch { return -1; }
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
                var anyConnected = false;
                foreach (var (name, qbit) in _config.QBitInstances)
                {
                    if (!qbit.Disabled && qbit.Host != "CHANGE_ME")
                    {
                        var ok = await _qbitManager.InitializeAsync(name, qbit, stoppingToken);
                        if (ok) anyConnected = true;
                    }
                }
                if (!anyConnected)
                {
                    _logger.LogWarning("Failed to connect to any qBittorrent instance. WebUI is still available.");
                    _qbitConfigured = false;
                }
            }

            var initialManaged = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(_config);
            if (initialManaged.Count > 0)
                _logger.LogInformation("FreeSpace categories: {Categories}", string.Join(", ", initialManaged));

            _freeSpaceFolder = GetFreeSpaceFolder();

            // Other section (Recheck, Failed, Free Space Manager): only when qBit is configured
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
                _stateManager.Initialize("FreeSpaceManager", new ArrProcessState
                {
                    Name = "FreeSpaceManager",
                    Category = "FreeSpaceManager",
                    Kind = "torrent",
                    MetricType = "free-space",
                    Alive = false,
                    CategoryCount = null
                });
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_qbitConfigured)
                    {
                        await ProcessSpecialCategoriesAsync(stoppingToken);

                        var freeSpaceGuardActive = _freeSpaceEnabled && _minFreeSpaceBytes > 0;
                        var enableTrackerSort = TorrentPolicyHelper.EnableTrackerSort(_config);
                        var enableFreeSpace = TorrentPolicyHelper.EnableFreeSpace(_config, freeSpaceGuardActive);
                        var managedCategories = TorrentPolicyHelper.GetAllMonitoredPolicyCategories(_config);
                        if (managedCategories.Count > 0 && (enableTrackerSort || enableFreeSpace))
                            await ProcessTorrentPolicyAsync(managedCategories, enableTrackerSort, enableFreeSpace, stoppingToken);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in orchestrator loop");
                }

                await Task.Delay(TimeSpan.FromSeconds(_config.Settings.LoopSleepTimer), stoppingToken);
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

    private string? GetFreeSpaceFolder()
    {
        // Try configured folder first
        if (!string.IsNullOrEmpty(_config.Settings.FreeSpaceFolder) && _config.Settings.FreeSpaceFolder != "CHANGE_ME")
        {
            // Check if path exists, if not return null
            if (Directory.Exists(_config.Settings.FreeSpaceFolder))
                return _config.Settings.FreeSpaceFolder;
        }

        // Fallback to completed download folder
        if (!string.IsNullOrEmpty(_config.Settings.CompletedDownloadFolder) && _config.Settings.CompletedDownloadFolder != "CHANGE_ME")
        {
            if (Directory.Exists(_config.Settings.CompletedDownloadFolder))
                return _config.Settings.CompletedDownloadFolder;
        }

        // Final fallback: use /config which is always available in container
        return "/config";
    }

    private async Task ProcessSpecialCategoriesAsync(CancellationToken cancellationToken)
    {
        int totalFailed = 0;
        int totalRecheck = 0;
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var failedTorrents = await client.GetTorrentsAsync(_config.Settings.FailedCategory, cancellationToken: cancellationToken);
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
                    await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, cancellationToken);
                }

                var recheckTorrents = await client.GetTorrentsAsync(_config.Settings.RecheckCategory, cancellationToken: cancellationToken);
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
                    _logger.LogInformation("[{Instance}] Re-checking torrent: {Name}", instanceName, torrent.Name);
                    await client.RecheckTorrentsAsync(new List<string> { torrent.Hash }, cancellationToken);
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
                    if (t.Tags.Contains("qBitrr-ignored", StringComparison.OrdinalIgnoreCase))
                        continue;
                    t.QBitInstanceName = instanceName;
                    await seeding.ApplyTrackerActionsForTorrentAsync(t, cancellationToken);
                }
            }
        }
    }

    private async Task ProcessFreeSpaceManagerAsync(HashSet<string> managedCategories, CancellationToken cancellationToken)
    {
        _logger.LogInformation("FreeSpace: Starting FreeSpace manager check");

        if (string.IsNullOrEmpty(_freeSpaceFolder))
        {
            _logger.LogWarning("FreeSpace: No free space folder configured or folder doesn't exist");
            _stateManager.Update("FreeSpaceManager", s => { s.Alive = false; s.CategoryCount = 0; });
            return;
        }

        _logger.LogInformation("FreeSpace: Using folder {Folder} for space monitoring", _freeSpaceFolder);

        // §1.6: tagless mode needs a DB scope to read/write FreeSpacePaused column
        IServiceScope? scope = null;
        TorrentarrDbContext? dbContext = null;
        if (_config.Settings.Tagless)
        {
            scope = _scopeFactory.CreateScope();
            dbContext = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        }

        const string freeSpacePausedTag = "qBitrr-free_space_paused";
        int pausedCount = 0;

        try
        {
            var driveInfo = new DriveInfo(_freeSpaceFolder);
            _currentFreeSpace = driveInfo.AvailableFreeSpace - _minFreeSpaceBytes;

            // Gather torrents from ALL qBit instances across all managed categories.
            var allTorrents = new List<(QBittorrentClient client, TorrentInfo torrent)>();
            foreach (var (_, client) in _qbitManager.GetAllClients())
            {
                foreach (var category in managedCategories)
                {
                    var torrents = await client.GetTorrentsAsync(category, "priority", cancellationToken);
                    allTorrents.AddRange(torrents.Select(t => (client, t)));
                }
            }

            int[]? pausedCountRef = null;
            if (!_config.Settings.Tagless)
            {
                pausedCount = allTorrents.Count(t => t.torrent.Tags?.Contains(freeSpacePausedTag) == true);
                pausedCountRef = new int[] { pausedCount };
            }

            foreach (var (client, torrent) in allTorrents
                .Select(x => (x.client, x.torrent, key: TorrentPolicyHelper.TorrentQueuePositionSortKey(x.torrent)))
                .OrderBy(x => x.key.InactiveQueueGroup)
                .ThenBy(x => x.key.Nq)
                .ThenBy(x => x.torrent.AddedOn)
                .Select(x => (x.client, x.torrent)))
                await ProcessSingleTorrentSpaceAsync(client, torrent, dbContext, pausedCountRef, cancellationToken);

            if (_config.Settings.Tagless && dbContext != null)
                pausedCount = await dbContext.TorrentLibrary.CountAsync(t => t.FreeSpacePaused, cancellationToken);
            else if (pausedCountRef != null)
                pausedCount = pausedCountRef[0];

            _stateManager.Update("FreeSpaceManager", s =>
            {
                s.CategoryCount = pausedCount;
                s.MetricType = "free-space";
                s.Alive = _freeSpaceEnabled && _minFreeSpaceBytes > 0 && !string.IsNullOrEmpty(_freeSpaceFolder);
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in free space manager");
        }
        finally
        {
            scope?.Dispose();
        }
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

    private async Task ProcessSingleTorrentSpaceAsync(
        QBittorrentClient client, TorrentInfo torrent, TorrentarrDbContext? dbContext, int[]? pausedCountRef, CancellationToken cancellationToken)
    {
        const string freeSpacePausedTag = "qBitrr-free_space_paused";
        var tagless = _config.Settings.Tagless;

        var isDownloading = torrent.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                           torrent.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase);
        var isPausedDownload = torrent.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase);

        // §1.6: tagless mode reads FreeSpacePaused from DB column; otherwise check qBit tag
        bool hasFreeSpaceTag;
        if (tagless && dbContext != null)
        {
            var dbEntry = await dbContext.TorrentLibrary.AsNoTracking()
                .FirstOrDefaultAsync(t => t.Hash == torrent.Hash, cancellationToken);
            hasFreeSpaceTag = dbEntry?.FreeSpacePaused == true;
        }
        else
        {
            hasFreeSpaceTag = torrent.Tags?.Contains(freeSpacePausedTag) == true;
        }

        if (isDownloading || (isPausedDownload && hasFreeSpaceTag))
        {
            var freeSpaceTest = _currentFreeSpace - torrent.AmountLeft;

            _logger.LogInformation(
                "FreeSpace: Evaluating torrent: {Name} | Current space: {Available} | Space after: {SpaceAfter} | Remaining: {Needed}",
                torrent.Name, FormatBytes(_currentFreeSpace), FormatBytes(freeSpaceTest), FormatBytes(torrent.AmountLeft));

            if (!isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Pausing download (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name, FormatBytes(_currentFreeSpace), FormatBytes(torrent.AmountLeft), FormatBytes(-freeSpaceTest));
                // §1.6: tagless — set DB column; else apply qBit tag
                if (tagless && dbContext != null)
                    await dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, true), cancellationToken);
                else
                    await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { freeSpacePausedTag }, cancellationToken);
                if (pausedCountRef != null) pausedCountRef[0]++;
                await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Resuming download (space available) | Torrent: {Name} | Available: {Available} | Space after: {SpaceAfter}",
                    torrent.Name, FormatBytes(_currentFreeSpace), FormatBytes(freeSpaceTest));
                _currentFreeSpace = freeSpaceTest;
                // §1.6: tagless — clear DB column; else remove qBit tag
                if (tagless && dbContext != null)
                    await dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                        .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, false), cancellationToken);
                else
                    await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { freeSpacePausedTag }, cancellationToken);
                if (pausedCountRef != null) pausedCountRef[0]--;
                await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Keeping paused (insufficient space) | Torrent: {Name} | Available: {Available} | Needed: {Needed} | Deficit: {Deficit}",
                    torrent.Name, FormatBytes(_currentFreeSpace), FormatBytes(torrent.AmountLeft), FormatBytes(-freeSpaceTest));
            }
            else if (!isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation(
                    "FreeSpace: Continuing download (sufficient space) | Torrent: {Name} | Available: {Available} | Space after: {SpaceAfter}",
                    torrent.Name, FormatBytes(_currentFreeSpace), FormatBytes(freeSpaceTest));
                _currentFreeSpace = freeSpaceTest;
            }
        }
        else if (!isDownloading && hasFreeSpaceTag)
        {
            // Torrent completed — clear the paused marker
            _logger.LogInformation(
                "FreeSpace: Torrent completed, removing free space tag | Torrent: {Name} | Available: {Available}",
                torrent.Name, FormatBytes(_currentFreeSpace + _minFreeSpaceBytes));
            // §1.6: tagless — clear DB column; else remove qBit tag
            if (tagless && dbContext != null)
                await dbContext.TorrentLibrary.Where(t => t.Hash == torrent.Hash)
                    .ExecuteUpdateAsync(s => s.SetProperty(t => t.FreeSpacePaused, false), cancellationToken);
            else
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { freeSpacePausedTag }, cancellationToken);
            if (pausedCountRef != null) pausedCountRef[0]--;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

// Request models for API endpoints
public record TestConnectionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("arrType")] string ArrType,
    [property: System.Text.Json.Serialization.JsonPropertyName("uri")] string? Uri,
    [property: System.Text.Json.Serialization.JsonPropertyName("apiKey")] string? ApiKey,
    [property: System.Text.Json.Serialization.JsonPropertyName("instanceKey")] string? InstanceKey = null);
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
