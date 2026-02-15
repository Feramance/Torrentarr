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

// Fallback for SPA routing
app.MapFallbackToFile("index.html");

Log.Information("Commandarr WebUI starting on {Host}:{Port}",
    builder.Configuration["urls"] ?? "http://localhost:5000",
    "");

app.Run();
