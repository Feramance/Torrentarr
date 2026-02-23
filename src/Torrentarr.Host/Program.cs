using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
// Mutable level switch — lets /web/loglevel and /api/loglevel change the level at runtime
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .WriteTo.Console()
    .WriteTo.File("logs/torrentarr.log", rollingInterval: RollingInterval.Day)
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

        Log.Information("Configuration loaded from {Path}", ConfigurationLoader.GetDefaultConfigPath());
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to load configuration");
        return 1;
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
    // ArrWorkerManager registered as both singleton and IHostedService so it's injectable in endpoints
    builder.Services.AddSingleton<ArrWorkerManager>();
    builder.Services.AddHostedService(sp => sp.GetRequiredService<ArrWorkerManager>());
    builder.Services.AddHostedService<ProcessOrchestratorService>();
    // Scoped services (one per request / scope)
    builder.Services.AddScoped<ArrSyncService>();
    builder.Services.AddScoped<IArrImportService, ArrImportService>();
    builder.Services.AddScoped<ISeedingService, SeedingService>();
    builder.Services.AddScoped<IFreeSpaceService, FreeSpaceService>();
    builder.Services.AddScoped<ITorrentProcessor, TorrentProcessor>();
    builder.Services.AddScoped<IArrMediaService, ArrMediaService>();

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
    });

    builder.Services.AddCors(options =>
    {
        options.AddPolicy("AllowAll", policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    });

    // Database
    var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var dbPath = Path.Combine(homePath, ".config", "torrentarr", "qbitrr.db");
    var logsPath = Path.Combine(homePath, ".config", "torrentarr", "logs");
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

    builder.Services.AddDbContext<TorrentarrDbContext>(options =>
        options.UseSqlite($"Data Source={dbPath}"));

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

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
        db.Database.EnsureCreated();
        db.ConfigureWalMode();
        // Manual migrations for columns added after initial release
        ApplyManualMigrations(db);
    }

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors("AllowAll");

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

    // Bearer token auth for all /api/* routes
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            var configuredToken = config.WebUI.Token;
            if (!string.IsNullOrEmpty(configuredToken))
            {
                string? providedToken = null;
                var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
                if (authHeader?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
                    providedToken = authHeader["Bearer ".Length..];
                else if (context.Request.Query.ContainsKey("token"))
                    providedToken = context.Request.Query["token"];

                if (providedToken != configuredToken)
                {
                    context.Response.StatusCode = 401;
                    await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
                    return;
                }
            }
        }
        await next(context);
    });

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
    app.MapGet("/web/meta", async (TorrentarrConfig cfg) =>
        Results.Ok(await FetchMetaAsync(cfg)));

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
        var monitoredForDefault = new HashSet<string>(qbitManagedSet.Union(arrCategorySet), StringComparer.OrdinalIgnoreCase);

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
    app.MapGet("/web/processes", (ProcessStateManager stateMgr, TorrentarrConfig cfg, QBittorrentConnectionManager qbitMgr) =>
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
        }).ToList<object>();

        // Add a process card for each configured qBit instance
        foreach (var (instanceName, qbit) in cfg.QBitInstances.Where(q => q.Value.Host != "CHANGE_ME"))
        {
            processes.Add(new
            {
                category = instanceName,
                name = instanceName,
                kind = "torrent",
                pid = (int?)null,
                alive = qbitMgr.IsConnected(instanceName == "qBit" ? "default" : instanceName),
                rebuilding = false,
                searchSummary = (string?)null,
                searchTimestamp = (string?)null,
                queueCount = (int?)null,
                categoryCount = (int?)null,
                metricType = (string?)null
            });
        }

        return Results.Ok(new { processes });
    });

    // Web Restart Process — stops and restarts the named instance worker
    app.MapPost("/web/processes/{category}/{kind}/restart", async (string category, string kind, TorrentarrConfig cfg, ArrWorkerManager workerMgr) =>
    {
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
                    id = t.EntryId, trackNumber = t.TrackNumber, title = t.Title,
                    hasFile = t.HasFile, duration = t.Duration, monitored = t.Monitored,
                    trackFileId = t.TrackFileId, albumId = t.AlbumId,
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
                a.EntryId, a.Title, a.ArtistId, a.ArtistTitle,
                a.ReleaseDate, a.Monitored, a.AlbumFileId,
                a.Reason, a.QualityProfileId, a.QualityProfileName
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
    app.MapPost("/web/arr/{category}/restart", (string category) =>
        Results.Ok(new { success = true, message = $"Restart requested for {category}" }));

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
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(flat, jsonSettings);
        return Results.Content(json, "application/json");
    });

    // Web Config Update — frontend sends { changes: { "Section.Key": value, ... } } (dotted keys).
    // ConfigView.tsx flatten()s the hierarchical config into dotted paths before sending only the
    // changed keys.  We apply those changes onto the current in-memory config and save.
    app.MapPost("/web/config", async (HttpRequest request, TorrentarrConfig cfg, ConfigurationLoader loader) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            System.Text.Json.JsonElement changesEl;
            if (!payload.TryGetProperty("changes", out changesEl))
                changesEl = payload;

            var newtonsoftSettings = new Newtonsoft.Json.JsonSerializerSettings
            {
                ContractResolver = new Newtonsoft.Json.Serialization.DefaultContractResolver(),
                NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore,
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
                var parts = change.Name.Split('.');
                var sectionKey = parts[0];
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

    // Web Update Trigger
    app.MapPost("/web/update", () =>
        Results.Ok(new { success = true, message = "Update triggered" }));

    // Web Download Update
    app.MapGet("/web/download-update", () =>
        Results.Ok(new
        {
            download_url = (string?)null,
            download_name = (string?)null,
            download_size = (long?)null,
            error = (string?)null
        }));

    // Web Test Arr Connection
    app.MapPost("/web/arr/test-connection", async (TestConnectionRequest req) =>
    {
        try
        {
            if (req.ArrType == "radarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Radarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            else if (req.ArrType == "sonarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Sonarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            else if (req.ArrType == "lidarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Lidarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            return Results.BadRequest(new { error = "Unknown arr type" });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { success = false, message = ex.Message });
        }
    });

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

    // Web Token
    app.MapGet("/web/token", (TorrentarrConfig cfg) =>
        Results.Ok(new { token = cfg.WebUI.Token }));

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

    app.MapGet("/api/meta", async (TorrentarrConfig cfg) =>
        Results.Ok(await FetchMetaAsync(cfg)));

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
        var logFile = Path.Combine(logsPath, name);
        if (!File.Exists(logFile))
            return Results.NotFound(new { error = "Log file not found" });
        var lines = await File.ReadAllLinesAsync(logFile);
        return Results.Text(string.Join("\n", lines.TakeLast(500)), "text/plain");
    });

    app.MapGet("/api/logs/{name}/download", (string name) =>
    {
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

    app.MapPost("/api/arr/{section}/restart", (string section) =>
        Results.Ok(new { success = true, message = $"Restart requested for {section}" }));

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
                    id = t.EntryId, trackNumber = t.TrackNumber, title = t.Title,
                    hasFile = t.HasFile, duration = t.Duration, monitored = t.Monitored,
                    trackFileId = t.TrackFileId, albumId = t.AlbumId,
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
                a.EntryId, a.Title, a.ArtistId, a.ArtistTitle,
                a.ReleaseDate, a.Monitored, a.AlbumFileId,
                a.Reason, a.QualityProfileId, a.QualityProfileName
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

    app.MapPost("/api/update", () =>
        Results.Ok(new { success = true, message = "Update triggered" }));

    app.MapGet("/api/download-update", () =>
        Results.Ok(new
        {
            download_url = (string?)null,
            download_name = (string?)null,
            download_size = (long?)null,
            error = (string?)null
        }));

    app.MapPost("/api/arr/test-connection", async (TestConnectionRequest req) =>
    {
        try
        {
            if (req.ArrType == "radarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.RadarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Radarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            else if (req.ArrType == "sonarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.SonarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Sonarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            else if (req.ArrType == "lidarr")
            {
                var client = new Torrentarr.Infrastructure.ApiClients.Arr.LidarrClient(req.Uri, req.ApiKey);
                var systemInfo = await client.GetSystemInfoAsync();
                var profiles = await client.GetQualityProfilesAsync();
                return Results.Ok(new
                {
                    success = true,
                    message = $"Connected to Lidarr {systemInfo.Version}",
                    systemInfo = new { version = systemInfo.Version ?? "unknown" },
                    qualityProfiles = profiles.Select(p => new { id = p.Id, name = p.Name })
                });
            }
            return Results.BadRequest(new { error = "Unknown arr type" });
        }
        catch (Exception ex)
        {
            return Results.Ok(new { success = false, message = ex.Message });
        }
    });

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

// ── Version / meta helper ─────────────────────────────────────────────────
static Task<object> FetchMetaAsync(Torrentarr.Core.Configuration.TorrentarrConfig cfg)
    => MetaHelper.FetchAsync(cfg);

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

    // Global changes (Settings or QBit instances) → full reload
    bool hasGlobalChanges = serialize(oldCfg.Settings) != serialize(newCfg.Settings)
                         || serialize(oldCfg.QBitInstances) != serialize(newCfg.QBitInstances);

    // WebUI connection fields → webui restart
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

    if (hasGlobalChanges)
        return ("full", newCfg.ArrInstances.Keys.OrderBy(k => k).ToList());
    if (affectedArr.Count > 0)
        return (affectedArr.Count > 1 ? "multi_arr" : "single_arr", affectedArr);
    if (hasWebuiKeyChanges)
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
    private readonly HashSet<string> _managedCategories;
    private long _currentFreeSpace;
    private long _minFreeSpaceBytes;
    private string? _freeSpaceFolder;
    private bool _qbitConfigured;

    public ProcessOrchestratorService(
        ILogger<ProcessOrchestratorService> logger,
        TorrentarrConfig config,
        QBittorrentConnectionManager qbitManager)
    {
        _logger = logger;
        _config = config;
        _qbitManager = qbitManager;
        _managedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _minFreeSpaceBytes = (long)(_config.Settings.FreeSpaceThresholdGB ?? 10) * 1024L * 1024L * 1024L;
        _qbitConfigured = config.QBitInstances.Values.Any(q =>
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

            foreach (var arrInstance in _config.ArrInstances.Where(x => x.Value.Managed && x.Value.URI != "CHANGE_ME" && !string.IsNullOrEmpty(x.Value.Category)))
                _managedCategories.Add(arrInstance.Value.Category!);

            if (_managedCategories.Count > 0)
                _logger.LogInformation("Managing {Count} categories: {Categories}", _managedCategories.Count, string.Join(", ", _managedCategories));

            _freeSpaceFolder = GetFreeSpaceFolder();

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (_qbitConfigured)
                    {
                        await ProcessSpecialCategoriesAsync(stoppingToken);

                        if (_config.Settings.AutoPauseResume && _config.Settings.FreeSpaceThresholdGB > 0)
                            await ProcessFreeSpaceManagerAsync(stoppingToken);
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
        if (!string.IsNullOrEmpty(_config.Settings.FreeSpaceFolder) && _config.Settings.FreeSpaceFolder != "CHANGE_ME")
            return _config.Settings.FreeSpaceFolder;

        if (!string.IsNullOrEmpty(_config.Settings.CompletedDownloadFolder) && _config.Settings.CompletedDownloadFolder != "CHANGE_ME")
            return _config.Settings.CompletedDownloadFolder;

        return null;
    }

    private async Task ProcessSpecialCategoriesAsync(CancellationToken cancellationToken)
    {
        foreach (var (instanceName, client) in _qbitManager.GetAllClients())
        {
            try
            {
                var failedTorrents = await client.GetTorrentsAsync(_config.Settings.FailedCategory, cancellationToken);
                foreach (var torrent in failedTorrents)
                {
                    _logger.LogWarning("[{Instance}] Deleting failed torrent: {Name}", instanceName, torrent.Name);
                    await client.DeleteTorrentsAsync(new List<string> { torrent.Hash }, deleteFiles: true, cancellationToken);
                }

                var recheckTorrents = await client.GetTorrentsAsync(_config.Settings.RecheckCategory, cancellationToken);
                foreach (var torrent in recheckTorrents)
                {
                    _logger.LogInformation("[{Instance}] Re-checking torrent: {Name}", instanceName, torrent.Name);
                    await client.RecheckTorrentsAsync(new List<string> { torrent.Hash }, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Instance}] Error processing special categories", instanceName);
            }
        }
    }

    private async Task ProcessFreeSpaceManagerAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(_freeSpaceFolder)) return;

        try
        {
            var driveInfo = new DriveInfo(_freeSpaceFolder);
            _currentFreeSpace = driveInfo.AvailableFreeSpace - _minFreeSpaceBytes;

            // Gather torrents from ALL qBit instances across all managed categories, sorted by added date
            var allTorrents = new List<(QBittorrentClient client, TorrentInfo torrent)>();
            foreach (var (_, client) in _qbitManager.GetAllClients())
            {
                foreach (var category in _managedCategories)
                {
                    var torrents = await client.GetTorrentsAsync(category, cancellationToken);
                    allTorrents.AddRange(torrents.Select(t => (client, t)));
                }
            }

            foreach (var (client, torrent) in allTorrents.OrderBy(x => x.torrent.AddedOn))
                await ProcessSingleTorrentSpaceAsync(client, torrent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in free space manager");
        }
    }

    private async Task ProcessSingleTorrentSpaceAsync(QBittorrentClient client, TorrentInfo torrent, CancellationToken cancellationToken)
    {
        const string freeSpacePausedTag = "qBitrr-free_space_paused";

        var isDownloading = torrent.State.Contains("downloading", StringComparison.OrdinalIgnoreCase) ||
                           torrent.State.Contains("stalledDL", StringComparison.OrdinalIgnoreCase);
        var isPausedDownload = torrent.State.Contains("pausedDL", StringComparison.OrdinalIgnoreCase);
        var hasFreeSpaceTag = torrent.Tags?.Contains(freeSpacePausedTag) == true;

        if (isDownloading || (isPausedDownload && hasFreeSpaceTag))
        {
            var freeSpaceTest = _currentFreeSpace - torrent.AmountLeft;

            if (!isPausedDownload && freeSpaceTest < 0)
            {
                _logger.LogInformation("Pausing download (insufficient space): {Name}", torrent.Name);
                await client.AddTagsAsync(new List<string> { torrent.Hash }, new List<string> { freeSpacePausedTag }, cancellationToken);
                await client.PauseTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (isPausedDownload && freeSpaceTest >= 0)
            {
                _logger.LogInformation("Resuming download (space available): {Name}", torrent.Name);
                _currentFreeSpace = freeSpaceTest;
                await client.RemoveTagsAsync(new List<string> { torrent.Hash }, new List<string> { freeSpacePausedTag }, cancellationToken);
                await client.ResumeTorrentAsync(torrent.Hash, cancellationToken);
            }
            else if (!isPausedDownload && freeSpaceTest >= 0)
            {
                _currentFreeSpace = freeSpaceTest;
            }
        }
    }
}

// Request models for API endpoints
public record TestConnectionRequest(
    [property: System.Text.Json.Serialization.JsonPropertyName("arrType")] string ArrType,
    [property: System.Text.Json.Serialization.JsonPropertyName("uri")] string Uri,
    [property: System.Text.Json.Serialization.JsonPropertyName("apiKey")] string ApiKey);
public record LoggerConfigurationRequest(string Level);

/// <summary>
/// GitHub release check helper — mirrors qBitrr's versioning.py logic.
/// Caches the result for 1 hour to avoid hammering the API.
/// </summary>
static class MetaHelper
{
    private static object? _cache;
    private static DateTime _cacheAt = DateTime.MinValue;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    private const string RepoOwner = "Feramance";
    private const string RepoName = "Torrentarr";
    private const string GithubApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    public static async Task<object> FetchAsync(Torrentarr.Core.Configuration.TorrentarrConfig cfg)
    {
        if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalHours < 1)
            return _cache;

        await _lock.WaitAsync();
        try
        {
            // Double-check inside lock
            if (_cache != null && (DateTime.UtcNow - _cacheAt).TotalHours < 1)
                return _cache;

            string currentVersion = GetCurrentVersion();

            try
            {
                using var http = new System.Net.Http.HttpClient();
                http.DefaultRequestHeaders.Add("User-Agent", $"{RepoName}/{currentVersion}");
                http.Timeout = TimeSpan.FromSeconds(10);

                var response = await http.GetAsync(GithubApiUrl);
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var release = Newtonsoft.Json.Linq.JObject.Parse(json);

                    var latestTag = release["tag_name"]?.ToObject<string>() ?? currentVersion;
                    // Strip leading 'v' if present
                    var latestVersion = latestTag.TrimStart('v');
                    var updateAvailable = IsNewerVersion(latestVersion, currentVersion);
                    var body = release["body"]?.ToObject<string>() ?? "";
                    var htmlUrl = release["html_url"]?.ToObject<string>() ?? "";

                    _cache = new
                    {
                        version = currentVersion,
                        latestVersion = latestVersion,
                        updateAvailable = updateAvailable,
                        releaseNotes = body,
                        releaseUrl = htmlUrl
                    };
                }
                else
                {
                    _cache = new { version = currentVersion, latestVersion = currentVersion, updateAvailable = false };
                }
            }
            catch
            {
                _cache = new { version = currentVersion, latestVersion = currentVersion, updateAvailable = false };
            }

            _cacheAt = DateTime.UtcNow;
            return _cache!;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string GetCurrentVersion()
    {
        var asm = System.Reflection.Assembly.GetEntryAssembly();
        var ver = asm?.GetName().Version;
        if (ver == null) return "0.0.0";
        return $"{ver.Major}.{ver.Minor}.{ver.Build}";
    }

    /// <summary>Returns true if <paramref name="latest"/> is strictly newer than <paramref name="current"/>.</summary>
    private static bool IsNewerVersion(string latest, string current)
    {
        if (System.Version.TryParse(latest, out var l) && System.Version.TryParse(current, out var c))
            return l > c;
        return false;
    }
}

// Make Program accessible to test projects (WebApplicationFactory<Program>)
public partial class Program { }
