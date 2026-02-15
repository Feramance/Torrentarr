using Commandarr.Core.Configuration;
using Commandarr.Infrastructure.Database;
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
        Title = "Commandarr API",
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
var dbPath = Path.Combine(homePath, ".config", "commandarr", "qbitrr.db");

// Ensure directory exists
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

builder.Services.AddDbContext<CommandarrDbContext>(options =>
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
        return new CommandarrConfig();
    }
});

var app = builder.Build();

// Ensure database is created and configure WAL mode
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<CommandarrDbContext>();
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

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// Health check endpoint
app.MapGet("/health", () => Results.Ok(new
{
    status = "healthy",
    service = "commandarr-webui",
    timestamp = DateTime.UtcNow
}));

// Status endpoint
app.MapGet("/api/status", async (CommandarrDbContext db, CommandarrConfig config) =>
{
    var movieCount = await db.Movies.CountAsync();
    var episodeCount = await db.Episodes.CountAsync();
    var torrentCount = await db.TorrentLibrary.CountAsync();

    return Results.Ok(new
    {
        qbit = new
        {
            alive = !config.QBit.Disabled,
            host = config.QBit.Host,
            port = config.QBit.Port,
            version = (string?)null
        },
        qbitInstances = new Dictionary<string, object>
        {
            ["default"] = new
            {
                alive = !config.QBit.Disabled,
                host = config.QBit.Host,
                port = config.QBit.Port,
                version = (string?)null
            }
        },
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
app.MapGet("/api/movies", async (CommandarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/episodes", async (CommandarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/torrents", async (CommandarrDbContext db, int? page, int? pageSize) =>
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
app.MapGet("/api/stats", async (CommandarrDbContext db) =>
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
app.MapGet("/api/config", (CommandarrConfig config) =>
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
            config.QBit.Host,
            config.QBit.Port,
            config.QBit.Disabled,
            managedCategories = config.QBit.ManagedCategories
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

// Fallback for SPA routing
app.MapFallbackToFile("index.html");

Log.Information("Commandarr WebUI starting on {Host}:{Port}",
    builder.Configuration["urls"] ?? "http://localhost:5000",
    "");

app.Run();
