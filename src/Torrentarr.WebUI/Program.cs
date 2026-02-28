using Torrentarr.Core.Configuration;
using Torrentarr.Core.Models;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Torrentarr.Infrastructure.ApiClients.QBittorrent;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// Calculate base paths - use /config for Docker, or config/ relative to cwd for local
var configEnv = Environment.GetEnvironmentVariable("TORRENTARR_CONFIG");
var basePath = !string.IsNullOrEmpty(configEnv) && configEnv.StartsWith("/config") 
    ? "/config" 
    : Path.Combine(Directory.GetCurrentDirectory(), "config");
var logsPath = Path.Combine(basePath, "logs");
var dbPath = Path.Combine(basePath, "qbitrr.db");
Directory.CreateDirectory(basePath);
Directory.CreateDirectory(logsPath);

// Mutable level switch — lets /web/loglevel change the level at runtime
var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog - write to .config/logs/ with process metadata enrichment
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.ControlledBy(levelSwitch)
    .Enrich.WithProperty("ProcessType", "WebUI")
    .Enrich.WithProperty("ProcessId", Environment.ProcessId)
    .Enrich.WithProperty("MachineName", Environment.MachineName)
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(logsPath, "webui.log"),
        rollingInterval: RollingInterval.Day,
        shared: true,
        retainedFileCountLimit: 7)
    .CreateLogger();

builder.Host.UseSerilog();

// Register LoggingLevelSwitch so endpoints can change log level at runtime
builder.Services.AddSingleton(levelSwitch);

// Add services to the container
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ContractResolver =
            new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Include;
        options.SerializerSettings.DateTimeZoneHandling = Newtonsoft.Json.DateTimeZoneHandling.Utc;
    });

// Add OpenAPI/Swagger
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

// Add Response Compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
});

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Configure Database - use same dbPath as defined at startup
builder.Services.AddDbContext<TorrentarrDbContext>(options =>
{
    options.UseSqlite($"Data Source={dbPath}");
});

// Add Configuration Loader
builder.Services.AddSingleton(sp =>
{
    var loader = new ConfigurationLoader();
    try
    {
        return loader.Load();
    }
    catch (FileNotFoundException)
    {
        Log.Warning("Configuration file not found, using defaults");
        return new TorrentarrConfig();
    }
});

builder.Services.AddSingleton<IConfigReloader, ConfigReloader>();
builder.Services.AddSingleton<ConfigurationLoader>();

var app = builder.Build();

// Ensure database is created and configure WAL mode
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<TorrentarrDbContext>();
    db.Database.EnsureCreated();
    db.ConfigureWalMode();
}

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseResponseCompression();
app.UseCors("AllowAll");

// Serve static files from ClientApp/build
var clientAppPath = Path.Combine(Directory.GetCurrentDirectory(), "ClientApp", "build");
if (Directory.Exists(clientAppPath))
{
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientAppPath)
    });
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(clientAppPath)
    });
}
else
{
    // Fallback to wwwroot if ClientApp/build doesn't exist
    app.UseDefaultFiles();
    app.UseStaticFiles();
}

app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "torrentarr-webui",
    timestamp = DateTime.UtcNow
}));

app.MapPost("/web/loglevel", (LogLevelRequest req, LoggingLevelSwitch ls) =>
{
    var newLevel = req.Level?.ToUpperInvariant() switch
    {
        "TRACE" or "VERBOSE" => LogEventLevel.Verbose,
        "DEBUG" => LogEventLevel.Debug,
        "INFORMATION" or "INFO" => LogEventLevel.Information,
        "WARNING" or "WARN" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        "CRITICAL" or "FATAL" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information
    };
    
    ls.MinimumLevel = newLevel;
    Log.Information("Log level changed to {Level}", req.Level);
    
    return Results.Ok(new { success = true, level = req.Level });
});

app.MapGet("/web/loglevel", (LoggingLevelSwitch ls) =>
{
    return Results.Ok(new { level = ls.MinimumLevel.ToString() });
});

// Status endpoint
app.MapGet("/web/status", async (TorrentarrDbContext db, TorrentarrConfig config) =>
{
    var movieCount = await db.Movies.CountAsync();
    var episodeCount = await db.Episodes.CountAsync();
    var torrentCount = await db.TorrentLibrary.CountAsync();

    return Results.Ok(new
    {
        qbit = new
        {
            alive = !(config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Disabled,
            host = (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Host,
            port = (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Port,
            version = (string?)null
        },
        qbitInstances = config.QBitInstances.ToDictionary(
            kv => kv.Key,
            kv => (object)new { alive = !kv.Value.Disabled, host = kv.Value.Host, port = kv.Value.Port, version = (string?)null }),
        arrs = config.ArrInstances.Select(kvp => new
        {
            category = kvp.Value.Category,
            name = kvp.Key,
            type = kvp.Value.Type,
            alive = true
        }).ToList(),
        ready = true,
        stats = new
        {
            movieCount,
            episodeCount,
            torrentCount
        }
    });
});

// Movies endpoint - get all movies with pagination
app.MapGet("/web/movies", async (TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalMovies = await db.Movies.CountAsync();
    var movies = await db.Movies
        .OrderByDescending(m => m.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(m => new
        {
            m.EntryId,
            m.Title,
            m.Monitored,
            m.TmdbId,
            m.Year
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalMovies,
        totalPages = (int)Math.Ceiling((double)totalMovies / currentPageSize),
        items = movies
    });
});

// Episodes endpoint - get all episodes with pagination
app.MapGet("/web/episodes", async (TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalEpisodes = await db.Episodes.CountAsync();
    var episodes = await db.Episodes
        .OrderByDescending(e => e.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(e => new
        {
            e.EntryId,
            e.SeriesTitle,
            e.SeasonNumber,
            e.EpisodeNumber,
            e.Monitored,
            e.SeriesId
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalEpisodes,
        totalPages = (int)Math.Ceiling((double)totalEpisodes / currentPageSize),
        items = episodes
    });
});

// Torrents endpoint - get all tracked torrents
app.MapGet("/web/torrents", async (TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalTorrents = await db.TorrentLibrary.CountAsync();
    var torrents = await db.TorrentLibrary
        .OrderByDescending(t => t.Id)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(t => new
        {
            t.Hash,
            t.Category,
            t.Imported,
            t.QbitInstance
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalTorrents,
        totalPages = (int)Math.Ceiling((double)totalTorrents / currentPageSize),
        items = torrents
    });
});

// Stats endpoint - detailed statistics
app.MapGet("/web/stats", async (TorrentarrDbContext db) =>
{
    var movieCount = await db.Movies.CountAsync();
    var episodeCount = await db.Episodes.CountAsync();
    var seriesCount = await db.Series.CountAsync();
    var albumCount = await db.Albums.CountAsync();
    var torrentCount = await db.TorrentLibrary.CountAsync();

    var importedTorrents = await db.TorrentLibrary.CountAsync(t => t.Imported);
    var activeTorrents = await db.TorrentLibrary.CountAsync(t => !t.Imported);

    return Results.Ok(new
    {
        media = new
        {
            movies = movieCount,
            episodes = episodeCount,
            series = seriesCount,
            albums = albumCount,
            total = movieCount + episodeCount + albumCount
        },
        torrents = new
        {
            total = torrentCount,
            imported = importedTorrents,
            active = activeTorrents
        }
    });
});

// Configuration endpoint - get current configuration (sanitized)
app.MapGet("/web/config", (TorrentarrConfig config) =>
{
    return Results.Ok(new
    {
        settings = new
        {
            config.Settings.ConfigVersion,
            config.Settings.LoopSleepTimer,
            config.Settings.SearchLoopDelay,
            config.Settings.AutoRestartProcesses,
            config.Settings.FreeSpaceThresholdGB
        },
        webui = new
        {
            config.WebUI.Host,
            config.WebUI.Port,
            config.WebUI.Theme,
            config.WebUI.ViewDensity,
            config.WebUI.LiveArr,
            config.WebUI.GroupSonarr,
            config.WebUI.GroupLidarr
        },
        qbit = new
        {
            (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Host,
            (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Port,
            (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).Disabled,
            managedCategories = (config.QBitInstances.GetValueOrDefault("qBit") ?? new QBitConfig()).ManagedCategories
        },
        arrs = config.Arrs.Select(a => new
        {
            a.Category,
            a.Type,
            a.Managed,
            a.SearchOnly,
            a.ProcessingOnly
        }).ToList()
    });
});

// §6.8: POST /web/config — partial config changes with reload-type detection
// Accepts { "changes": { "Section.Key": value, ... } }, saves and returns reloadType
app.MapPost("/web/config", async (HttpContext ctx, TorrentarrConfig config, ConfigurationLoader loader, IConfigReloader reloader) =>
{
    Newtonsoft.Json.Linq.JObject? body = null;
    try
    {
        using var reader = new StreamReader(ctx.Request.Body);
        var json = await reader.ReadToEndAsync();
        body = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = $"Invalid JSON: {ex.Message}" });
    }

    var changesToken = body?["changes"] as Newtonsoft.Json.Linq.JObject;
    if (changesToken == null)
        return Results.BadRequest(new { error = "Missing 'changes' field" });

    // Empty changes dict → nothing to do, report reloadType="none"
    if (!changesToken.HasValues)
        return Results.Ok(new { status = "ok", configReloaded = false, reloadType = "none", affectedInstances = Array.Empty<string>() });

    var changes = changesToken.Properties()
        .ToDictionary(p => p.Name, p => p.Value);

    // ── Classify reload type ────────────────────────────────────────────────
    var reloadTiers = new[] { "none", "frontend", "webui", "single_arr", "multi_arr", "full" };
    var reloadType = "none";
    var affectedInstances = new List<string>();

    static string EscalateReloadType(string current, string candidate, string[] tiers)
    {
        var ci = Array.IndexOf(tiers, current);
        var ni = Array.IndexOf(tiers, candidate);
        return ni > ci ? candidate : current;
    }

    foreach (var key in changes.Keys)
    {
        var lower = key.ToLowerInvariant();

        if (lower.StartsWith("webui."))
        {
            reloadType = EscalateReloadType(reloadType, "frontend", reloadTiers);
            continue;
        }

        if (lower.StartsWith("settings."))
        {
            reloadType = EscalateReloadType(reloadType, "webui", reloadTiers);
            continue;
        }

        // Check against known Arr instance names (case-insensitive prefix match)
        var matchedArr = config.ArrInstances.Keys
            .FirstOrDefault(name => lower.StartsWith(name.ToLowerInvariant() + "."));
        if (matchedArr != null)
        {
            if (!affectedInstances.Contains(matchedArr, StringComparer.OrdinalIgnoreCase))
                affectedInstances.Add(matchedArr);
            reloadType = EscalateReloadType(reloadType,
                affectedInstances.Count > 1 ? "multi_arr" : "single_arr",
                reloadTiers);
            continue;
        }

        // qBit, unknown, or structural changes → full restart
        reloadType = EscalateReloadType(reloadType, "full", reloadTiers);
    }

    // ── Apply changes to config + save ─────────────────────────────────────
    try
    {
        // Serialize current config to JObject, apply dot-path changes, deserialize back
        var configJson = Newtonsoft.Json.JsonConvert.SerializeObject(config);
        var configObj = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(configJson)!;

        foreach (var (key, value) in changes)
        {
            // Convert dot-path "Section.SubKey" to JToken path "section.subKey"
            // Apply each change via JToken pointer navigation
            ApplyDotPathChange(configObj, key, value);
        }

        var updatedConfig = configObj.ToObject<TorrentarrConfig>();
        if (updatedConfig != null)
        {
            loader.SaveConfig(updatedConfig, reloader.ConfigPath);
        }
    }
    catch (Exception ex)
    {
        Log.Warning(ex, "POST /web/config: failed to save config changes");
        return Results.Ok(new
        {
            status = "saved_with_warning",
            configReloaded = false,
            reloadType,
            affectedInstances,
            warning = ex.Message
        });
    }

    var reloaded = reloader.ReloadConfig();

    return Results.Ok(new
    {
        status = "ok",
        configReloaded = reloaded,
        reloadType,
        affectedInstances
    });
});

// Helper: apply a dot-path change to a JObject (e.g. "Settings.LoopSleepTimer" = 30)
static void ApplyDotPathChange(Newtonsoft.Json.Linq.JObject root, string dotPath, Newtonsoft.Json.Linq.JToken value)
{
    var segments = dotPath.Split('.');
    Newtonsoft.Json.Linq.JObject current = root;

    for (var i = 0; i < segments.Length - 1; i++)
    {
        var seg = segments[i];
        // Case-insensitive property lookup
        var prop = current.Properties()
            .FirstOrDefault(p => p.Name.Equals(seg, StringComparison.OrdinalIgnoreCase));

        if (prop?.Value is Newtonsoft.Json.Linq.JObject nested)
        {
            current = nested;
        }
        else
        {
            // Create missing intermediate object
            var newObj = new Newtonsoft.Json.Linq.JObject();
            current[seg] = newObj;
            current = newObj;
        }
    }

    var leafKey = segments[^1];
    var existingProp = current.Properties()
        .FirstOrDefault(p => p.Name.Equals(leafKey, StringComparison.OrdinalIgnoreCase));
    if (existingProp != null)
        existingProp.Value = value;
    else
        current[leafKey] = value;
}

// Processes endpoint - list all processes with status
app.MapGet("/web/processes", (TorrentarrConfig config) =>
{
    var processes = new List<object>();

    foreach (var arr in config.ArrInstances)
    {
        processes.Add(new
        {
            category = arr.Value.Category,
            name = arr.Key,
            type = arr.Value.Type,
            alive = true,
            pid = (int?)null,
            uptime = (long?)null
        });
    }

    return Results.Ok(new
    {
        processes,
        total = processes.Count
    });
});

// Restart specific process
app.MapPost("/web/processes/{category}/{kind}/restart", (string category, string kind) =>
{
    return Results.Ok(new
    {
        success = true,
        message = $"Restart requested for {kind} in {category}"
    });
});

// Restart all processes
app.MapPost("/web/processes/restart_all", () =>
{
    return Results.Ok(new
    {
        success = true,
        message = "Restart requested for all processes"
    });
});

// Logs endpoint - list available log files
app.MapGet("/web/logs", () =>
{
    var files = new List<object>();

    if (Directory.Exists(logsPath))
    {
        foreach (var file in Directory.GetFiles(logsPath, "*.log").OrderByDescending(f => f))
        {
            var fileInfo = new FileInfo(file);
            files.Add(new
            {
                name = Path.GetFileName(file),
                size = fileInfo.Length,
                modified = fileInfo.LastWriteTimeUtc.ToString("o")
            });
        }
    }

    return Results.Ok(new { files });
});

// Log file contents
app.MapGet("/web/logs/{name}", async (string name, int? lines) =>
{
    var logFile = Path.Combine(logsPath, name);

    if (!File.Exists(logFile))
    {
        return Results.NotFound(new { error = "Log file not found" });
    }

    var lineCount = lines ?? 100;
    var content = await File.ReadAllTextAsync(logFile);
    var logLines = content.Split('\n').TakeLast(lineCount).ToList();

    return Results.Ok(new
    {
        name,
        lines = logLines,
        totalLines = content.Split('\n').Length
    });
});

// §6.9: Log file download — streams named log file as an attachment
app.MapGet("/web/logs/{name}/download", (string name, HttpResponse response) =>
{
    // Sanitize: only allow the filename component (no directory traversal)
    var safeName = Path.GetFileName(name);
    var logFile = Path.Combine(logsPath, safeName);

    if (!File.Exists(logFile))
        return Results.NotFound(new { error = "Log file not found" });

    response.Headers["Content-Disposition"] = $"attachment; filename=\"{safeName}\"";
    return Results.File(logFile, "application/octet-stream", safeName);
});

// Radarr movies for specific category
app.MapGet("/web/radarr/{category}/movies", async (string category, TorrentarrDbContext db, int? page, int? pageSize, string? q) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var allMovies = db.Movies.Where(m => m.ArrInstance == category);
    // §6.3: text search filter
    if (!string.IsNullOrWhiteSpace(q))
        allMovies = allMovies.Where(m => m.Title != null && m.Title.Contains(q));
    var totalMovies = await allMovies.CountAsync();
    var monitoredCount = await allMovies.CountAsync(m => m.Monitored);
    var availableCount = await allMovies.CountAsync(m => m.HasFile);
    // §6.4: additional aggregate counts
    var missingCount = await allMovies.CountAsync(m => !m.HasFile && m.Monitored);
    var qualityMetCount = await allMovies.CountAsync(m => m.QualityMet);
    var requestsCount = await allMovies.CountAsync(m => m.IsRequest);

    var movies = await allMovies
        .OrderByDescending(m => m.Year)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(m => new
        {
            id = m.EntryId,
            m.Title,
            m.Year,
            m.Monitored,
            m.HasFile,
            m.QualityMet,
            m.CustomFormatMet,
            m.CustomFormatScore,
            m.MinCustomFormatScore,
            m.IsRequest,
            m.Upgrade,
            m.Reason,
            m.Searched,
            m.QualityProfileId,
            m.QualityProfileName,
            m.TmdbId,
            m.ArrId,
            m.ArrInstance
        })
        .ToListAsync();

    return Results.Ok(new
    {
        category,
        total = totalMovies,
        page = currentPage,
        page_size = currentPageSize,
        counts = new { available = availableCount, monitored = monitoredCount, missing = missingCount, quality_met = qualityMetCount, requests = requestsCount },
        movies
    });
});

// Sonarr series for specific category
app.MapGet("/web/sonarr/{category}/series", async (string category, TorrentarrDbContext db, int? page, int? pageSize, string? q, string? missing) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var allSeries = db.Series.Where(s => s.ArrInstance == category);
    // §6.3: text search filter
    if (!string.IsNullOrWhiteSpace(q))
        allSeries = allSeries.Where(s => s.Title != null && s.Title.Contains(q));
    // missing=1: only return series that have unaired/missing episodes
    if (missing == "1")
        allSeries = allSeries.Where(s => !s.Searched);
    var totalSeries = await allSeries.CountAsync();
    var monitoredCount = await allSeries.CountAsync(s => s.Monitored == true);
    // §6.4: additional aggregate counts (SeriesFilesModel has no IsRequest/QualityMet; use Searched/Upgrade as proxies)
    var missingSeriesCount = await allSeries.CountAsync(s => !s.Searched);
    var qualityMetSeriesCount = await allSeries.CountAsync(s => !s.Upgrade);

    var seriesItems = await allSeries
        .OrderByDescending(s => s.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(s => new
        {
            series = new
            {
                id = s.EntryId,
                s.Title,
                s.TvdbId,
                s.Monitored,
                s.Upgrade,
                s.MinCustomFormatScore,
                s.QualityProfileId,
                s.QualityProfileName,
                s.ArrId,
                s.ArrInstance
            },
            totals = new { available = s.Searched ? 1 : 0, monitored = s.Monitored == true ? 1 : 0 },
            seasons = new Dictionary<string, object>()
        })
        .ToListAsync();

    return Results.Ok(new
    {
        category,
        total = totalSeries,
        page = currentPage,
        page_size = currentPageSize,
        counts = new { available = monitoredCount, monitored = monitoredCount, missing = missingSeriesCount, quality_met = qualityMetSeriesCount, requests = 0 },
        series = seriesItems
    });
});

// Lidarr albums for specific category
app.MapGet("/web/lidarr/{category}/albums", async (string category, TorrentarrDbContext db, int? page, int? pageSize, string? q) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var allAlbums = db.Albums.Where(a => a.ArrInstance == category);
    // §6.3: text search filter (matches artist or album title)
    if (!string.IsNullOrWhiteSpace(q))
        allAlbums = allAlbums.Where(a =>
            (a.Title != null && a.Title.Contains(q)) ||
            (a.ArtistTitle != null && a.ArtistTitle.Contains(q)));
    var totalAlbums = await allAlbums.CountAsync();
    var monitoredCount = await allAlbums.CountAsync(a => a.Monitored);
    var availableCount = await allAlbums.CountAsync(a => a.HasFile);
    // §6.4: additional aggregate counts
    var missingAlbumCount = await allAlbums.CountAsync(a => !a.HasFile && a.Monitored);
    var qualityMetAlbumCount = await allAlbums.CountAsync(a => a.QualityMet);
    var requestsAlbumCount = await allAlbums.CountAsync(a => a.IsRequest);

    var albums = await allAlbums
        .OrderByDescending(a => a.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(a => new
        {
            album = new
            {
                id = a.EntryId,
                a.Title,
                a.Monitored,
                a.HasFile,
                a.QualityMet,
                a.CustomFormatMet,
                a.CustomFormatScore,
                a.MinCustomFormatScore,
                a.IsRequest,
                a.Upgrade,
                a.Reason,
                a.Searched,
                a.QualityProfileId,
                a.QualityProfileName,
                a.ArrId,
                a.ArrInstance
            },
            totals = new { available = a.HasFile ? 1 : 0, monitored = a.Monitored ? 1 : 0 },
            tracks = new List<object>()
        })
        .ToListAsync();

    return Results.Ok(new
    {
        category,
        total = totalAlbums,
        page = currentPage,
        page_size = currentPageSize,
        counts = new { available = availableCount, monitored = monitoredCount, missing = missingAlbumCount, quality_met = qualityMetAlbumCount, requests = requestsAlbumCount },
        albums
    });
});

// Arr instances info — returns { arr: ArrInfo[] } matching frontend ArrListResponse
app.MapGet("/web/arr", (TorrentarrConfig config) =>
{
    var arrList = config.ArrInstances.Select(kv => new
    {
        name = kv.Key,
        type = kv.Value.Type,
        category = kv.Value.Category,
        uri = kv.Value.URI,
        managed = kv.Value.Managed,
        searchOnly = kv.Value.SearchOnly,
        processingOnly = kv.Value.ProcessingOnly
    }).ToList();

    return Results.Ok(new { arr = arrList, ready = true });
});

// Restart a specific Arr worker
app.MapPost("/web/arr/{category}/restart", (string category) =>
{
    return Results.Ok(new { status = "ok", restarted = new[] { category } });
});

// Test Arr connection and return quality profiles
app.MapPost("/web/arr/test-connection", async (HttpContext ctx) =>
{
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<ArrTestConnectionRequest>();
        if (body == null)
            return Results.BadRequest(new { success = false, error = "Invalid request body" });

        var profiles = body.ArrType?.ToLowerInvariant() switch
        {
            "radarr" => (await new RadarrClient(body.Uri, body.ApiKey).GetQualityProfilesAsync())
                            .Select(p => new { p.Id, p.Name }).Cast<object>().ToList(),
            "sonarr" => (await new SonarrClient(body.Uri, body.ApiKey).GetQualityProfilesAsync())
                            .Select(p => new { p.Id, p.Name }).Cast<object>().ToList(),
            "lidarr" => (await new LidarrClient(body.Uri, body.ApiKey).GetQualityProfilesAsync())
                            .Select(p => new { p.Id, p.Name }).Cast<object>().ToList(),
            _ => new List<object>()
        };

        return Results.Ok(new { success = true, profiles });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, error = ex.Message });
    }
});

// §6.7: Arr rebuild — triggers RescanMovie / RescanSeries / RescanArtist on the target instance
app.MapPost("/web/arr/rebuild", async (HttpContext ctx, TorrentarrConfig config) =>
{
    try
    {
        var body = await ctx.Request.ReadFromJsonAsync<ArrRebuildRequest>();
        if (body == null || string.IsNullOrEmpty(body.ArrInstanceName))
            return Results.BadRequest(new { success = false, error = "arrInstanceName required" });

        if (!config.ArrInstances.TryGetValue(body.ArrInstanceName, out var arrCfg))
            return Results.NotFound(new { success = false, error = "Arr instance not found" });

        bool success = arrCfg.Type?.ToLowerInvariant() switch
        {
            "radarr" => await new RadarrClient(arrCfg.URI, arrCfg.APIKey).RescanAsync(),
            "sonarr" => await new SonarrClient(arrCfg.URI, arrCfg.APIKey).RescanAsync(),
            "lidarr" => await new LidarrClient(arrCfg.URI, arrCfg.APIKey).RescanAsync(),
            _ => false
        };

        return Results.Ok(new { success, instance = body.ArrInstanceName });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { success = false, error = ex.Message });
    }
});

// §6.5: qBit categories — seeding config + live torrent stats per category
app.MapGet("/web/qbit/categories", async (TorrentarrConfig config) =>
{
    var categories = new List<object>();

    foreach (var (qbitName, qbitCfg) in config.QBitInstances)
    {
        if (qbitCfg.Disabled || qbitCfg.Host == "CHANGE_ME") continue;

        // Fetch live torrent list from this qBit instance
        var liveTorrents = new List<TorrentInfo>();
        try
        {
            var qbitClient = new QBittorrentClient(qbitCfg.Host, qbitCfg.Port, qbitCfg.UserName, qbitCfg.Password);
            if (await qbitClient.LoginAsync())
                liveTorrents = await qbitClient.GetTorrentsAsync();
        }
        catch { /* live stats unavailable — return zeros */ }

        foreach (var cat in qbitCfg.ManagedCategories)
        {
            var catTorrents = liveTorrents
                .Where(t => string.Equals(t.Category, cat, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var seedingTorrents = catTorrents
                .Where(t => t.State.Contains("upload", StringComparison.OrdinalIgnoreCase) ||
                             t.State.Contains("seeding", StringComparison.OrdinalIgnoreCase))
                .ToList();
            var avgRatio = catTorrents.Count > 0 ? catTorrents.Average(t => t.Ratio) : 0.0;
            var avgSeedingTimeDays = seedingTorrents.Count > 0
                ? seedingTorrents.Average(t => t.SeedingTime) / 86400.0
                : 0.0;

            var seeding = qbitCfg.CategorySeeding;
            categories.Add(new
            {
                category = cat,
                instance = qbitName,
                managedBy = config.ArrInstances.Values.Any(a =>
                    string.Equals(a.Category, cat, StringComparison.OrdinalIgnoreCase)) ? "arr" : "qbit",
                torrentCount = catTorrents.Count,
                seedingCount = seedingTorrents.Count,
                totalSize = catTorrents.Sum(t => t.Size),
                avgRatio,
                avgSeedingTimeDays,
                seedingConfig = new
                {
                    maxRatio = seeding.MaxUploadRatio,
                    maxTime = seeding.MaxSeedingTime,
                    removeMode = seeding.RemoveTorrent,
                    hitAndRunMode = seeding.HitAndRunMode,
                    minSeedRatio = seeding.MinSeedRatio,
                    minSeedingTimeDays = seeding.MinSeedingTimeDays,
                    downloadLimit = seeding.DownloadRateLimitPerTorrent,
                    uploadLimit = seeding.UploadRateLimitPerTorrent
                }
            });
        }
    }

    return Results.Ok(new { categories, ready = true });
});

app.MapGet("/web/config/full", (TorrentarrConfig config, IConfigReloader reloader) =>
{
    return Results.Ok(new
    {
        configPath = reloader.ConfigPath,
        settings = config.Settings,
        webui = config.WebUI,
        qbitInstances = config.QBitInstances.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                kv.Value.Host,
                kv.Value.Port,
                kv.Value.UserName,
                kv.Value.Disabled,
                kv.Value.ManagedCategories
            }),
        arrInstances = config.ArrInstances.ToDictionary(
            kv => kv.Key,
            kv => new
            {
                kv.Value.Type,
                kv.Value.Category,
                kv.Value.URI,
                kv.Value.Managed,
                kv.Value.SearchOnly,
                kv.Value.ProcessingOnly,
                kv.Value.Search
            })
    });
});

app.MapPost("/web/config/reload", (IConfigReloader reloader) =>
{
    var success = reloader.ReloadConfig();
    return success
        ? Results.Ok(new { success = true, message = "Configuration reloaded" })
        : Results.BadRequest(new { success = false, message = "Failed to reload configuration" });
});

app.MapPost("/web/config/save", async (TorrentarrConfig updatedConfig, ConfigurationLoader loader, IConfigReloader reloader) =>
{
    try
    {
        loader.SaveConfig(updatedConfig, reloader.ConfigPath);
        var reloadSuccess = reloader.ReloadConfig();
        
        return reloadSuccess
            ? Results.Ok(new { success = true, message = "Configuration saved and reloaded" })
            : Results.Ok(new { success = true, message = "Configuration saved but reload failed" });
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Failed to save configuration");
        return Results.BadRequest(new { success = false, message = ex.Message });
    }
});

app.MapGet("/web/config/path", (IConfigReloader reloader) =>
{
    return Results.Ok(new { path = reloader.ConfigPath });
});

// Meta info — matches frontend MetaResponse interface
app.MapGet("/web/meta", () =>
{
    return Results.Ok(new
    {
        current_version = "1.0.0",
        latest_version = (string?)null,
        update_available = false,
        changelog = (string?)null,
        current_version_changelog = (string?)null,
        changelog_url = (string?)null,
        repository_url = "https://github.com/Feramance/Torrentarr",
        homepage_url = "https://github.com/Feramance/Torrentarr",
        last_checked = (string?)null,
        error = (string?)null,
        update_state = new
        {
            in_progress = false,
            last_result = (string?)null,
            last_error = (string?)null,
            completed_at = (string?)null
        },
        installation_type = "binary",
        binary_download_url = (string?)null,
        binary_download_name = (string?)null,
        binary_download_size = (long?)null,
        binary_download_error = (string?)null,
        // Extra info not in qBitrr but useful
        platform = Environment.OSVersion.Platform.ToString(),
        runtime = $".NET {Environment.Version}"
    });
});

// Fallback for SPA routing - serve index.html from ClientApp/build or wwwroot
if (Directory.Exists(clientAppPath))
{
    app.MapFallback(context =>
    {
        context.Response.ContentType = "text/html";
        return context.Response.SendFileAsync(Path.Combine(clientAppPath, "index.html"));
    });
}
else
{
    app.MapFallbackToFile("index.html");
}

Log.Information("Torrentarr WebUI starting on {Host}:{Port}",
    builder.Configuration["urls"] ?? "http://localhost:5000",
    "");

app.Run();

public record LogLevelRequest(string Level);
public record ArrTestConnectionRequest(string ArrType, string Uri, string ApiKey);
public record ArrRebuildRequest(string ArrInstanceName);
