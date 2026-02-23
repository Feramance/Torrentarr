using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File("logs/webui.log", rollingInterval: RollingInterval.Day)
    .CreateLogger();

builder.Host.UseSerilog();

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

// Configure Database
var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
var dbPath = Path.Combine(homePath, ".config", "torrentarr", "qbitrr.db");

// Ensure directory exists
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

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

// Status endpoint
app.MapGet("/api/status", async (TorrentarrDbContext db, TorrentarrConfig config) =>
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
app.MapGet("/api/movies", async (TorrentarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/episodes", async (TorrentarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/torrents", async (TorrentarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/stats", async (TorrentarrDbContext db) =>
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
app.MapGet("/api/config", (TorrentarrConfig config) =>
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

// Processes endpoint - list all processes with status
app.MapGet("/api/processes", (TorrentarrConfig config) =>
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
app.MapPost("/api/processes/{category}/{kind}/restart", (string category, string kind) =>
{
    return Results.Ok(new
    {
        success = true,
        message = $"Restart requested for {kind} in {category}"
    });
});

// Restart all processes
app.MapPost("/api/processes/restart_all", () =>
{
    return Results.Ok(new
    {
        success = true,
        message = "Restart requested for all processes"
    });
});

// Logs endpoint - list available log files
app.MapGet("/api/logs", () =>
{
    var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var logsPath = Path.Combine(homePath, ".config", "torrentarr", "logs");

    var logs = new List<object>();

    if (Directory.Exists(logsPath))
    {
        foreach (var file in Directory.GetFiles(logsPath, "*.log").OrderByDescending(f => f))
        {
            var fileInfo = new FileInfo(file);
            logs.Add(new
            {
                name = Path.GetFileName(file),
                size = fileInfo.Length,
                modified = fileInfo.LastWriteTimeUtc
            });
        }
    }

    return Results.Ok(new { logs });
});

// Log file contents
app.MapGet("/api/logs/{name}", async (string name, int? lines) =>
{
    var homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    var logsPath = Path.Combine(homePath, ".config", "torrentarr", "logs");
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

// Radarr movies for specific category
app.MapGet("/api/radarr/{category}/movies", async (string category, TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalMovies = await db.Movies.CountAsync(m => m.ArrInstance == category);
    var movies = await db.Movies
        .Where(m => m.ArrInstance == category)
        .OrderByDescending(m => m.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(m => new
        {
            m.EntryId,
            m.Title,
            m.Monitored,
            m.TmdbId,
            m.Year,
            m.ArrInstance
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalMovies,
        totalPages = (int)Math.Ceiling((double)totalMovies / currentPageSize),
        category,
        items = movies
    });
});

// Sonarr series for specific category
app.MapGet("/api/sonarr/{category}/series", async (string category, TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalSeries = await db.Series.CountAsync(s => s.ArrInstance == category);
    var series = await db.Series
        .Where(s => s.ArrInstance == category)
        .OrderByDescending(s => s.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(s => new
        {
            s.EntryId,
            s.Title,
            s.Monitored,
            s.ArrInstance
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalSeries,
        totalPages = (int)Math.Ceiling((double)totalSeries / currentPageSize),
        category,
        items = series
    });
});

// Lidarr albums for specific category
app.MapGet("/api/lidarr/{category}/albums", async (string category, TorrentarrDbContext db, int? page, int? pageSize) =>
{
    var currentPage = page ?? 1;
    var currentPageSize = pageSize ?? 50;
    var skip = (currentPage - 1) * currentPageSize;

    var totalAlbums = await db.Albums.CountAsync(a => a.ArrInstance == category);
    var albums = await db.Albums
        .Where(a => a.ArrInstance == category)
        .OrderByDescending(a => a.EntryId)
        .Skip(skip)
        .Take(currentPageSize)
        .Select(a => new
        {
            a.EntryId,
            a.Title,
            a.Monitored,
            a.ArrInstance
        })
        .ToListAsync();

    return Results.Ok(new
    {
        page = currentPage,
        pageSize = currentPageSize,
        totalCount = totalAlbums,
        totalPages = (int)Math.Ceiling((double)totalAlbums / currentPageSize),
        category,
        items = albums
    });
});

// Arr instances info
app.MapGet("/api/arr", (TorrentarrConfig config) =>
{
    var arrs = new Dictionary<string, object>();

    foreach (var arr in config.ArrInstances)
    {
        arrs[arr.Key] = new
        {
            name = arr.Key,
            type = arr.Value.Type,
            category = arr.Value.Category,
            uri = arr.Value.URI,
            managed = arr.Value.Managed,
            searchOnly = arr.Value.SearchOnly,
            processingOnly = arr.Value.ProcessingOnly
        };
    }

    return Results.Ok(new
    {
        arrs,
        total = arrs.Count
    });
});

app.MapGet("/api/config/full", (TorrentarrConfig config, IConfigReloader reloader) =>
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

app.MapPost("/api/config/reload", (IConfigReloader reloader) =>
{
    var success = reloader.ReloadConfig();
    return success
        ? Results.Ok(new { success = true, message = "Configuration reloaded" })
        : Results.BadRequest(new { success = false, message = "Failed to reload configuration" });
});

app.MapPost("/api/config/save", async (TorrentarrConfig updatedConfig, ConfigurationLoader loader, IConfigReloader reloader) =>
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

app.MapGet("/api/config/path", (IConfigReloader reloader) =>
{
    return Results.Ok(new { path = reloader.ConfigPath });
});

// Meta info
app.MapGet("/api/meta", () =>
{
    return Results.Ok(new
    {
        version = "1.0.0",
        pythonEquivalent = "5.8.8",
        platform = Environment.OSVersion.ToString(),
        runtime = $".NET {Environment.Version}",
        machineName = Environment.MachineName
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
