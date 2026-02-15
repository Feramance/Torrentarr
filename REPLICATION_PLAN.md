# Plan: Replicate qBitrr as a C# Project

## 1. Project Setup & Architecture
- Create .NET 8/9 solution with clean architecture pattern
- **Structure**:
  - `QBitrr.Core` (domain/business logic)
  - `QBitrr.Infrastructure` (external APIs, DB)
  - `QBitrr.WebUI` (ASP.NET Core - standalone always-online web server)
  - `QBitrr.Workers` (background worker processes)
  - `QBitrr.Host` (entry point & process orchestrator)
- **Process Isolation**: Each Arr worker runs as separate `System.Diagnostics.Process` independent from WebUI
- **WebUI Independence**: ASP.NET Core app runs continuously regardless of worker process state

## 2. Configuration System (100% Backwards Compatible)
- **TOML File Format**: Use Tomlyn library to parse existing `config.toml` files WITHOUT modification
- **Config Location**: Read from same location as Python version: `~/config/config.toml` or `~/.config/qbitrr/config.toml`
- **Schema Compatibility**: Support all existing config sections:
  - `[Settings]` - All current options (ConsoleLevel, CompletedDownloadFolder, FreeSpace, LoopSleepTimer, etc.)
  - `[qBit]` - Host, Port, UserName, Password, Disabled, ManagedCategories, Trackers
  - `[qBit.CategorySeeding]` - MaxUploadRatio, MaxSeedingTime, etc.
  - `[Radarr-*]`, `[Sonarr-*]`, `[Lidarr-*]` - Arr instance configs
  - `[WebUI]` - Host, Port, Token, LiveArr, GroupSonarr, Theme, etc.
- **Environment Variables**: Support same `QBITRR_*` env var overrides as Python version
- **Config Versioning**:
  - Read `ConfigVersion = "5.8.8"` from existing files
  - Auto-migrate to newer schemas if needed
  - Write migrations for future versions
  - Preserve unknown keys for forward compatibility
- **Validation**: Same defaults and validation rules as Python version
- **No Breaking Changes**: Users can switch between Python and C# versions using same config file

## 3. Database Layer (Backwards Compatible)
- Use Entity Framework Core with SQLite provider
- **Database Files**: Use same file paths as Python version (`~/qbitrr.db`, `~/Torrents.db`)
- **Schema Compatibility**: Match exact Peewee schema (table names, column names, types)
- Enable WAL mode for multi-process/multi-threaded access
- Implement 11 entity models matching Python tables EXACTLY:
  - `MoviesFilesModel`, `EpisodeFilesModel`, `SeriesFilesModel`, `AlbumFilesModel`, `TrackFilesModel`, `ArtistFilesModel`
  - `MovieQueueModel`, `EpisodeQueueModel`, `AlbumQueueModel`
  - `FilesQueued`, `TorrentLibrary`
- Create database context with proper indexing matching Peewee
- Implement checkpoint background service for WAL optimization (runs in separate process)
- Add retry/recovery mechanisms with Polly for concurrent access
- **No Schema Changes**: C# version can read/write same database as Python version

## 4. External API Clients (via RestSharp + Newtonsoft.Json)
- **qBittorrent Client**: Implement async JSON-RPC wrapper using RestSharp for WebUI API (add/remove torrents, categories, transfer info, multi-instance support)
- **Arr API Clients**: Create base `ArrApiClient` using RestSharp abstracted for Radarr/Sonarr/Lidarr with quality profiles, custom formats, search triggers, file management
- **Request Systems**: Overseerr and Ombi API clients using RestSharp for pulling user requests
- **GitHub Releases**: Version checker using RestSharp for auto-updates
- **Media Processing**: FFmpeg/FFprobe wrapper (consider FFMpegCore library)
- Use Newtonsoft.Json for all JSON serialization/deserialization across API clients

## 5. Process Architecture (Critical Design)
### Main Orchestrator Process (QBitrr.Host)
- Spawns and monitors all child processes
- Reads config and determines which workers to start
- Uses `System.Diagnostics.Process` to launch:
  - WebUI process (always-on)
  - One Arr worker process per managed Arr instance
  - Database checkpoint process
  - Auto-update process (optional)
- Monitors child process health via PID tracking
- Handles graceful shutdown (SIGTERM propagation)
- Auto-restarts crashed workers (5 attempts in 300s window)

### WebUI Process (QBitrr.WebUI.exe)
- **Standalone ASP.NET Core application**
- Runs independently - NEVER crashes due to worker failures
- Reads process state from:
  - Database (for data queries)
  - Shared memory/file system (for real-time process health)
  - PID files or named pipes for IPC
- Serves React frontend + REST API
- Provides process control endpoints (start/stop/restart workers)
- Always accessible even if all workers are down

### Arr Worker Process (QBitrr.Workers.exe --arr-instance=<name>)
- **One process per Arr instance** (e.g., Radarr-Movies, Sonarr-TV)
- Command-line args: `--arr-instance=Radarr-Movies`
- Runs continuous processing loop:
  - Fetch torrents from qBit
  - Process imports, searches, quality upgrades
  - Update database
  - Sleep for configured interval (default: 5s)
- Isolated crash domain - one Arr failure doesn't affect others
- Writes health status to shared location (file, named pipe, or Redis)

### Database Checkpoint Process (QBitrr.DbCheckpoint.exe)
- **Separate process** for WAL checkpoint operations
- Runs every 5+ seconds
- Ensures data durability without blocking workers
- Can run independently of all other processes

### Auto-Update Process (QBitrr.AutoUpdate.exe) [Optional]
- **Separate process** triggered by cron schedule
- Checks GitHub for new releases
- Downloads and applies updates
- Signals orchestrator to restart all processes

## 6. Inter-Process Communication (IPC)
### Health Status Sharing
- **Option 1 (File-based)**: Each worker writes JSON health file to shared directory
  - `~/qbitrr/health/webui.json`
  - `~/qbitrr/health/radarr-movies.json`
  - WebUI reads all health files periodically
- **Option 2 (Named Pipes)**: Workers send health updates via named pipes to orchestrator
- **Option 3 (Redis/SQLite)**: Use lightweight Redis or SQLite table for health tracking

### Process Control
- WebUI sends commands to orchestrator via:
  - HTTP requests to orchestrator's control endpoint (internal API on localhost:6970)
  - File-based signals (e.g., `~/qbitrr/commands/restart-radarr-movies.signal`)
  - Named pipes/sockets

### Shared Data
- Database (SQLite with WAL - safe for multi-process)
- Config file (read-only for workers, orchestrator handles reloads)

## 7. Core Business Logic
- **qBitManager**: Main orchestration service managing qBit connections, Arr instances, worker coordination
- **ArrManager**: Multi-instance Arr coordinator (runs in each Arr worker process)
- **Arr Worker**: Per-instance processing loop for torrents (import, search, pause, delete, quality upgrade)
- **FreeSpaceManager**: Disk space monitoring with configurable pause/resume
- **TorrentProcessor**: State-based handlers (downloading, stalled, completed, failed)
- **SearchCoordinator**: Rate-limited media search with activity tracking
- **QualityUpgradeService**: Custom format scoring and profile switching

## 8. Category & Seeding Management
- **qBitCategoryManager**: Per-category seeding rules (ratio, time, tracker-based HnR protection)
- Tracker configuration with inheritance (global → qBit instance → Arr instance)
- Implement remove modes: Never, IfMetUploadLimit, Always

## 9. Web UI & API (ASP.NET Core - Always Online with React Frontend)

### Frontend (React Application - REUSE EXISTING)
- **USE EXISTING REACT APP**: Located in `webui/` directory
- **Technology Stack**:
  - React 19 + TypeScript
  - Vite (build tool)
  - Tailwind CSS + Mantine UI components
  - React Router for navigation
- **Key Features to Preserve**:
  - TypeScript type definitions from `webui/src/api/types.ts`
  - API client from `webui/src/api/client.ts`
  - All existing components, contexts, and hooks
  - Theme support (Light/Dark from config)
  - View density settings (Comfortable/Compact)
  - Real-time updates with polling (or upgrade to SignalR)
- **Build Process**:
  - Run `npm ci && npm run build` in `webui/` directory
  - Outputs to `webui/dist/` directory
  - Copy built assets to ASP.NET Core `wwwroot/` folder
- **NO CHANGES TO REACT CODE**: The existing React app should work as-is with the C# backend

### Backend (ASP.NET Core)
- **Minimal APIs or Controllers** for REST endpoints matching Python Flask routes EXACTLY:
  - `/api/status` - qBit & Arr health (reads from IPC health files)
  - `/api/processes` - Worker process info (PID, alive status, uptime)
  - `/api/processes/{name}/restart` - Restart specific worker
  - `/api/processes/restart-all` - Restart all workers
  - `/api/categories` - Category stats & config
  - `/api/radarr/{category}/movies` - Paginated movie list
  - `/api/sonarr/{category}/series` - Series/episodes list (grouped if config enabled)
  - `/api/lidarr/{category}/albums` - Albums/tracks list (grouped if config enabled)
  - `/api/logs` - Log file listing
  - `/api/logs/{filename}` - Log file contents
  - `/api/config` - Current config (with secrets masked)
  - `/api/qbit/instances` - Multi-instance qBit info
  - `/api/qbit/categories` - Category management
- **Static Files Middleware**: Serve React app from `wwwroot/`
  - Default route `/` → `index.html`
  - SPA fallback for client-side routing
- **Authentication**: Bearer token via custom middleware (same as Python version)
  - Read `WebUI.Token` from config
  - Validate `Authorization: Bearer <token>` header
- **API Compatibility**:
  - Match exact JSON response structures from Python version
  - Use same field names and data types
  - Support same query parameters (page, page_size, sort, filter)
- **Live Updates**:
  - Initially: Support polling (existing React app mechanism)
  - Optional: Add SignalR hub for real-time push updates
- **CORS**: Configure CORS if needed for development
- **Resilience**: WebUI NEVER crashes due to worker failures - always serves UI

### Integration Points
- **React API Client** (`webui/src/api/client.ts`) makes requests to ASP.NET Core backend
- **TypeScript Types** (`webui/src/api/types.ts`) must match C# DTO models
- **Response Format Compatibility**: Ensure C# serialization matches Python JSON output
  - Use Newtonsoft.Json with same settings (camelCase, null handling, etc.)
  - Match date/time formatting
  - Match numeric precision

## 10. Logging & Observability
- Use Serilog with structured logging
- **Log Format**: Match Python coloredlogs format for consistency
- Colored console output (Serilog.Sinks.Console with themes)
- Per-process log files (rolling file sink) in same location as Python:
  - `~/qbitrr/logs/orchestrator.log`
  - `~/qbitrr/logs/webui.log`
  - `~/qbitrr/logs/radarr-movies.log`
  - `~/qbitrr/logs/sonarr-tv.log`
- Custom log levels matching Python: TRACE, DEBUG, INFO, NOTICE, WARNING, ERROR, CRITICAL
- Process ID in all log entries for correlation
- **Same Verbosity Levels**: Respect `ConsoleLevel` config option

## 11. Error Handling & Resilience
- Custom exception hierarchy matching Python: `RestartLoopException`, `DelayLoopException`, `SkipException`, `UnhandledError`, `NoConnectionException`
- Polly for retry policies with exponential backoff
- Database deadlock/locked handling with automatic retry
- **Worker process crashes**: Orchestrator detects via PID monitoring, auto-restarts
- **WebUI resilience**: Catches all exceptions, never crashes, shows degraded state in UI
- Graceful shutdown via `SIGTERM` propagation to all child processes

## 12. Process Management (Orchestrator)
- Use `System.Diagnostics.Process` to spawn all child processes
- Command-line patterns:
  - WebUI: `dotnet QBitrr.WebUI.dll --host 0.0.0.0 --port 6969`
  - Worker: `dotnet QBitrr.Workers.dll --type arr --instance Radarr-Movies`
  - Checkpoint: `dotnet QBitrr.Workers.dll --type db-checkpoint`
- PID tracking in memory + PID files for recovery
- Signal handling (SIGINT/SIGTERM) for graceful shutdown
- Process restart tracking with time windows (5 restarts in 300s limit per config)
- Health monitoring:
  - Periodic PID checks (`Process.HasExited`)
  - Heartbeat file updates (workers write timestamp every 30s)
  - Automatic recovery on stale heartbeats

## 13. Docker Support
- Multi-stage Dockerfile:
  - **Stage 1 (Node)**: Build React frontend
    ```dockerfile
    FROM node:25-bookworm AS frontend-build
    WORKDIR /app/webui
    COPY webui/package*.json ./
    RUN npm ci
    COPY webui/ ./
    RUN npm run build
    ```
  - **Stage 2 (.NET SDK)**: Build C# backend
    ```dockerfile
    FROM mcr.microsoft.com/dotnet/sdk:8.0 AS backend-build
    WORKDIR /app
    COPY *.sln ./
    COPY QBitrr.*/*.csproj ./
    RUN dotnet restore
    COPY . ./
    COPY --from=frontend-build /app/webui/dist ./QBitrr.WebUI/wwwroot
    RUN dotnet publish -c Release -o out
    ```
  - **Stage 3 (Runtime)**: Final image
    ```dockerfile
    FROM mcr.microsoft.com/dotnet/aspnet:8.0
    WORKDIR /app
    COPY --from=backend-build /app/out .
    ENTRYPOINT ["dotnet", "QBitrr.Host.dll"]
    ```
- Single ENTRYPOINT: orchestrator process
- Orchestrator spawns all child processes within container
- Volume mounts for config, logs, database, downloads
- Environment variable configuration override
- **Same Docker Image Behavior**: Match Python version's Docker functionality

## 14. Testing Strategy
- Unit tests with xUnit for business logic
- Integration tests for API clients using RestSharp (mocked HTTP with WireMock.Net)
- E2E tests with Testcontainers (qBittorrent + Arr instances)
- Database tests with in-memory SQLite
- Process orchestration tests (spawn, monitor, restart)
- **Frontend Tests**: Keep existing React tests (if any)
- **API Contract Tests**: Verify C# backend produces same JSON as Python version
- **Compatibility Tests**: Verify C# version can read Python config/database

## 15. Build & Deployment
- GitHub Actions CI/CD pipeline:
  - Build React frontend
  - Build C# backend
  - Run tests
  - Build Docker image
  - Publish to Docker Hub and NuGet
- NuGet package publishing
- Docker Hub automated builds
- systemd service file for Linux (runs orchestrator)
- Cross-platform support (Linux, Windows, macOS)
- Single-file publish for each executable

## 16. Backwards Compatibility Checklist
- ✅ Read existing `config.toml` without modification
- ✅ Support all environment variable overrides (`QBITRR_*`)
- ✅ Use same database schema and file paths
- ✅ Match Flask API endpoints and JSON response structures
- ✅ Use same log file paths and formats
- ✅ Support same config options with same defaults
- ✅ Parse config version and apply migrations
- ✅ Preserve unknown config keys for future compatibility
- ✅ Match qBittorrent minimum version requirements (4.3.9+)
- ✅ Support same category naming and management
- ✅ Implement same Hit & Run protection logic
- ✅ Match free space handling behavior
- ✅ Support same WebUI token authentication
- ✅ **Reuse existing React frontend without modification**
- ✅ **Match TypeScript API type definitions**

## 17. Critical Implementation Areas
1. **React Frontend Reuse**: Copy existing React app and ensure API compatibility
2. **Config parser**: Exact TOML parsing with same defaults and validation
3. **Database schema**: Match Peewee table definitions EXACTLY
4. **API endpoint compatibility**: Flask routes → ASP.NET Core with same JSON
5. **Process isolation architecture**: Ensure WebUI is completely independent
6. **IPC design**: Efficient health status sharing without tight coupling
7. **arss.py complexity**: 7,978 lines of torrent processing logic - translate to C# with async/await
8. **Multi-instance support**: Dictionary-based qBit/Arr instance management
9. **WAL database optimization**: Multi-process SQLite with checkpointing
10. **Concurrent processing**: Task-based async/await in each worker process
11. **Config migrations**: Schema versioning with backward compatibility
12. **Hit & Run protection**: Tracker-based seeding rule injection per torrent
13. **Process recovery**: Robust restart logic with exponential backoff

## 18. Estimated Development Phases
**Phase 1**: Core infrastructure (config parser, DB schema match, logging) - 2 weeks
**Phase 2**: API clients with RestSharp (qBit, Arr, requests) - 2 weeks
**Phase 3**: Process orchestration & IPC - 2 weeks
**Phase 4**: Business logic (managers, workers, processors) - 4 weeks
**Phase 5**: WebUI backend & API endpoint compatibility - 1 week
**Phase 6**: React frontend integration & testing - 1 week
**Phase 7**: Testing & backwards compatibility validation - 1 week
**Phase 8**: Docker & deployment - 1 week
**Total**: ~14 weeks for feature parity with full backwards compatibility

## 19. Key C# Libraries
- **RestSharp** (API client HTTP requests)
- **Newtonsoft.Json** (JSON serialization/deserialization)
- **Tomlyn** (TOML parsing - exact Python config compatibility)
- **Entity Framework Core** (ORM matching Peewee schema)
- **Polly** (resilience/retry)
- **Serilog** (logging with same format as coloredlogs)
- **Hangfire/CronNET** (scheduling in auto-update process)
- **FFMpegCore** (media processing)
- **SignalR** (optional: WebUI real-time updates)
- **xUnit + Testcontainers** (testing)

## 20. React Frontend Technical Details

### Existing React App Structure
```
webui/
├── src/
│   ├── api/
│   │   ├── client.ts          # HTTP client for API calls
│   │   └── types.ts           # TypeScript API type definitions
│   ├── components/            # Reusable React components
│   ├── context/               # React contexts (SearchContext, WebUIContext, ToastContext)
│   ├── hooks/                 # Custom React hooks
│   ├── pages/                 # Page components
│   ├── config/                # UI configuration
│   ├── App.tsx                # Root component
│   └── main.tsx               # Entry point
├── package.json               # Frontend dependencies
├── vite.config.ts             # Vite bundler config
├── tailwind.config.js         # Tailwind CSS config
└── tsconfig.json              # TypeScript config
```

### C# Backend DTO Models Must Match TypeScript Types
- Create C# record types matching `webui/src/api/types.ts`:
  - `ProcessInfo`, `ProcessesResponse`
  - `ArrInfo`, `ArrListResponse`
  - `QbitStatus`, `QbitInstance`, `QbitCategory`
  - `RadarrMovie`, `RadarrMoviesResponse`
  - `SonarrEpisode`, `SonarrSeason`, `SonarrSeriesEntry`
  - `LidarrAlbum`, `LidarrTrack`, `LidarrArtistEntry`
  - `StatusResponse`, `LogsListResponse`, etc.

### ASP.NET Core Configuration for React SPA
```csharp
// Program.cs
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseSpa(spa => {
    spa.Options.SourcePath = "wwwroot";
});
app.MapFallbackToFile("index.html");
```

### API Response Serialization Settings
```csharp
// Match Python/JavaScript camelCase conventions
builder.Services.AddControllers()
    .AddNewtonsoftJson(options => {
        options.SerializerSettings.ContractResolver =
            new CamelCasePropertyNamesContractResolver();
        options.SerializerSettings.NullValueHandling = NullValueHandling.Include;
        options.SerializerSettings.DateTimeZoneHandling = DateTimeZoneHandling.Utc;
    });
```

## 21. ASP.NET Core WebUI Improvements (Beyond Python Flask)

While maintaining backwards compatibility, ASP.NET Core enables significant improvements over the Python Flask implementation:

### Performance Improvements

#### 1. **Response Compression & Caching**
```csharp
// Enable response compression for API responses
builder.Services.AddResponseCompression(options => {
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
        new[] { "application/json", "text/plain" });
});

// Add response caching for static data
builder.Services.AddResponseCaching();
builder.Services.AddMemoryCache();

app.UseResponseCompression();
app.UseResponseCaching();
```
**Benefit**: Reduces bandwidth usage by 70-80% for JSON responses, faster page loads

#### 2. **HTTP/2 and HTTP/3 Support**
```csharp
builder.WebHost.ConfigureKestrel(serverOptions => {
    serverOptions.ConfigureEndpointDefaults(listenOptions => {
        listenOptions.Protocols = HttpProtocols.Http1AndHttp2AndHttp3;
    });
});
```
**Benefit**: Multiplexed connections, reduced latency, better mobile performance

#### 3. **Output Caching for Expensive Queries**
```csharp
builder.Services.AddOutputCache(options => {
    options.AddPolicy("StatusCache", builder =>
        builder.Expire(TimeSpan.FromSeconds(5)));
    options.AddPolicy("MoviesCache", builder =>
        builder.Expire(TimeSpan.FromSeconds(30)).Tag("movies"));
});

// In endpoints
app.MapGet("/api/status", GetStatus).CacheOutput("StatusCache");
app.MapGet("/api/radarr/{category}/movies", GetMovies)
   .CacheOutput("MoviesCache");
```
**Benefit**: Reduces database queries, faster API responses (5-10x improvement)

### Real-Time Improvements

#### 4. **SignalR for Live Updates (Replace Polling)**
```csharp
builder.Services.AddSignalR()
    .AddNewtonsoftJsonProtocol(); // Match REST API serialization

// Create hub for real-time updates
public class QBitrrHub : Hub {
    public async Task SubscribeToProcessUpdates() {
        await Groups.AddToGroupAsync(Context.ConnectionId, "processes");
    }

    public async Task SubscribeToTorrents(string category) {
        await Groups.AddToGroupAsync(Context.ConnectionId, $"torrents:{category}");
    }
}

// Background service pushes updates
public class RealtimeUpdateService : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            var status = await GetProcessStatus();
            await _hubContext.Clients.Group("processes")
                .SendAsync("ProcessStatusUpdate", status);
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }
}
```
**Benefit**:
- Eliminates constant polling, reduces server load by 80%
- Sub-second latency for updates vs 5-30 second polling intervals
- Automatic reconnection handling
- React can use `@microsoft/signalr` client library

#### 5. **Server-Sent Events (SSE) Alternative**
```csharp
// For log streaming
app.MapGet("/api/logs/{filename}/stream", async (
    string filename,
    HttpContext context,
    CancellationToken ct) => {

    context.Response.Headers.Append("Content-Type", "text/event-stream");
    context.Response.Headers.Append("Cache-Control", "no-cache");

    await using var fileStream = new FileStream(logPath,
        FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(fileStream);

    string? line;
    while ((line = await reader.ReadLineAsync(ct)) != null) {
        await context.Response.WriteAsync($"data: {line}\n\n", ct);
        await context.Response.Body.FlushAsync(ct);
    }
});
```
**Benefit**: Real-time log streaming without page refresh, better than polling

### Security Improvements

#### 6. **Enhanced Authentication & Authorization**
```csharp
// JWT token support (in addition to bearer token)
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options => {
        options.TokenValidationParameters = new TokenValidationParameters {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["WebUI:JwtIssuer"],
            ValidAudience = config["WebUI:JwtAudience"],
            IssuerSigningKey = new SymmetricSecurityKey(
                Encoding.UTF8.GetBytes(config["WebUI:Token"]))
        };

        // Support SignalR authentication
        options.Events = new JwtBearerEvents {
            OnMessageReceived = context => {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs")) {
                    context.Token = accessToken;
                }
                return Task.CompletedTask;
            }
        };
    });

// Rate limiting per client
builder.Services.AddRateLimiter(options => {
    options.AddFixedWindowLimiter("api", opt => {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 100;
        opt.QueueLimit = 0;
    });
});

app.UseRateLimiter();
```
**Benefit**:
- Prevents brute force attacks on API
- JWT tokens with expiration vs static bearer token
- Better multi-user support potential

#### 7. **HTTPS Enforcement & Security Headers**
```csharp
app.UseHttpsRedirection();
app.UseHsts(); // HTTP Strict Transport Security

app.Use(async (context, next) => {
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Append("X-Frame-Options", "DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    context.Response.Headers.Append("Referrer-Policy", "no-referrer");
    context.Response.Headers.Append("Content-Security-Policy",
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'");
    await next();
});
```
**Benefit**: Protection against XSS, clickjacking, MIME sniffing attacks

### API Improvements

#### 8. **OpenAPI/Swagger Documentation**
```csharp
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c => {
    c.SwaggerDoc("v1", new OpenApiInfo {
        Title = "qBitrr API",
        Version = "v1",
        Description = "API for qBittorrent + Arr automation"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme {
        In = ParameterLocation.Header,
        Description = "WebUI Token from config.toml",
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        BearerFormat = "JWT",
        Scheme = "bearer"
    });
});

if (app.Environment.IsDevelopment()) {
    app.UseSwagger();
    app.UseSwaggerUI();
}
```
**Benefit**: Auto-generated API documentation, easier integration, testing UI

#### 9. **Typed API Endpoints with Minimal APIs**
```csharp
// Type-safe endpoint definitions
app.MapGet("/api/radarr/{category}/movies",
    async (
        string category,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? sort = null,
        [FromQuery] string? filter = null,
        [FromServices] IRadarrService radarrService,
        CancellationToken ct
    ) => {
        var result = await radarrService.GetMoviesAsync(
            category, page, pageSize, sort, filter, ct);
        return Results.Ok(result);
    })
    .Produces<RadarrMoviesResponse>(200)
    .ProducesProblem(404)
    .RequireAuthorization()
    .WithName("GetRadarrMovies")
    .WithTags("Radarr");
```
**Benefit**:
- Auto parameter validation
- Compile-time type safety
- Built-in OpenAPI metadata
- Better error handling with ProblemDetails

#### 10. **Pagination & Filtering Helpers**
```csharp
public static class QueryableExtensions {
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken ct = default) {

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<T> {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize,
            TotalPages = (int)Math.Ceiling(total / (double)pageSize)
        };
    }
}

// Usage
app.MapGet("/api/radarr/{category}/movies", async (
    string category, int page, int pageSize, QBitrrDbContext db) => {

    return await db.Movies
        .Where(m => m.ArrInstance == category)
        .OrderByDescending(m => m.Year)
        .ToPagedResultAsync(page, pageSize);
});
```
**Benefit**: Consistent pagination across all endpoints, reduced boilerplate

### Monitoring & Diagnostics

#### 11. **Health Checks**
```csharp
builder.Services.AddHealthChecks()
    .AddDbContextCheck<QBitrrDbContext>("database")
    .AddCheck<QBitConnectionHealthCheck>("qbittorrent")
    .AddCheck<ArrHealthCheck>("arr-instances")
    .AddCheck<WorkerProcessHealthCheck>("worker-processes");

app.MapHealthChecks("/health", new HealthCheckOptions {
    ResponseWriter = async (context, report) => {
        context.Response.ContentType = "application/json";
        var result = JsonConvert.SerializeObject(new {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                duration = e.Value.Duration.TotalMilliseconds
            })
        });
        await context.Response.WriteAsync(result);
    }
});

// Liveness and readiness for Kubernetes
app.MapHealthChecks("/health/live", new HealthCheckOptions {
    Predicate = _ => false // Always healthy if app is running
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions {
    Predicate = check => check.Tags.Contains("ready")
});
```
**Benefit**:
- Built-in health monitoring
- Kubernetes/Docker health probes
- Detailed component status

#### 12. **Application Insights / OpenTelemetry**
```csharp
// OpenTelemetry for distributed tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddSource("QBitrr.*")
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddRuntimeInstrumentation()
        .AddMeter("QBitrr.*")
        .AddPrometheusExporter());

// Custom metrics
app.MapPrometheusScrapingEndpoint("/metrics");
```
**Benefit**:
- Performance monitoring
- Distributed tracing across worker processes
- Prometheus metrics export
- Grafana dashboard support

### Development Experience

#### 13. **Hot Reload for Development**
```csharp
// appsettings.Development.json
{
  "DetailedErrors": true,
  "Logging": {
    "LogLevel": {
      "Default": "Debug",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}

// During development, Kestrel can watch for changes
// dotnet watch run
```
**Benefit**: Faster development cycle, no need to restart on code changes

#### 14. **Development Proxy for React**
```csharp
// For development, proxy to Vite dev server
if (app.Environment.IsDevelopment()) {
    app.UseSpa(spa => {
        spa.Options.SourcePath = "webui";
        spa.UseProxyToSpaDevelopmentServer("http://localhost:5173");
    });
} else {
    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapFallbackToFile("index.html");
}
```
**Benefit**: Hot module replacement for React during development

### Deployment & Production

#### 15. **Static File Optimization**
```csharp
// Aggressive caching for static assets
app.UseStaticFiles(new StaticFileOptions {
    OnPrepareResponse = ctx => {
        if (ctx.File.Name.Contains(".")) {
            var extension = Path.GetExtension(ctx.File.Name);
            if (extension == ".js" || extension == ".css" ||
                extension == ".woff" || extension == ".woff2") {
                ctx.Context.Response.Headers.Append(
                    "Cache-Control", "public,max-age=31536000,immutable");
            }
        }
    }
});
```
**Benefit**: Reduced bandwidth, faster repeat visits

#### 16. **Graceful Shutdown Handling**
```csharp
builder.Services.AddHostedService<GracefulShutdownService>();

public class GracefulShutdownService : IHostedService {
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<GracefulShutdownService> _logger;

    public Task StartAsync(CancellationToken ct) {
        _lifetime.ApplicationStopping.Register(() => {
            _logger.LogInformation("WebUI shutdown requested, completing requests...");
        });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct) {
        // Allow 30 seconds for in-flight requests to complete
        await Task.Delay(TimeSpan.FromSeconds(30), ct);
        _logger.LogInformation("WebUI shut down gracefully");
    }
}
```
**Benefit**: No dropped connections during updates/restarts

### Configuration Management

#### 17. **Options Pattern with Validation**
```csharp
public class WebUIOptions {
    public string Host { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 6969;
    public string Token { get; set; } = "";
    public bool LiveArr { get; set; } = true;
    public string Theme { get; set; } = "Dark";
}

builder.Services.AddOptions<WebUIOptions>()
    .Bind(configuration.GetSection("WebUI"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<WebUIOptions>,
    WebUIOptionsValidator>();

public class WebUIOptionsValidator : IValidateOptions<WebUIOptions> {
    public ValidateOptionsResult Validate(string name, WebUIOptions options) {
        if (options.Port < 1 || options.Port > 65535)
            return ValidateOptionsResult.Fail("Port must be between 1 and 65535");
        if (string.IsNullOrEmpty(options.Token))
            return ValidateOptionsResult.Fail("Token cannot be empty");
        return ValidateOptionsResult.Success;
    }
}
```
**Benefit**: Type-safe config, validation on startup, better error messages

### Summary of ASP.NET Core Improvements

| Feature | Python Flask | ASP.NET Core | Benefit |
|---------|--------------|--------------|---------|
| **Response Compression** | Manual | Built-in Brotli/Gzip | 70-80% bandwidth reduction |
| **HTTP/2 & HTTP/3** | ❌ | ✅ | 50% faster page loads |
| **Real-time Updates** | Polling every 5-30s | SignalR push | 80% less server load |
| **API Documentation** | Manual | Auto Swagger/OpenAPI | Self-documenting |
| **Type Safety** | Runtime checks | Compile-time | Fewer bugs |
| **Health Checks** | Custom | Built-in | K8s-ready |
| **Rate Limiting** | Manual | Built-in | DDoS protection |
| **Observability** | Basic logs | OpenTelemetry | Full tracing |
| **Caching** | Manual | Output caching | 5-10x faster responses |
| **Security Headers** | Manual | Built-in middleware | Better security |

**Migration Path**: All improvements can be implemented gradually while maintaining Flask API compatibility. Start with compression and caching, add SignalR, then observability.

---

## Summary

This plan ensures a faithful C# replication of qBitrr with:
- **100% backwards compatibility** with existing configs and databases
- **React frontend reuse** without any modifications
- **Process isolation** for maximum reliability (WebUI always online)
- **Exact API compatibility** for seamless frontend integration
- Modern .NET architecture with async/await and dependency injection
- Cross-platform support via .NET and Docker

The React app will continue to work exactly as it does now, making API calls to the new C# backend instead of the Python Flask backend.
