# Architecture

Detailed overview of Torrentarr's system architecture and design patterns.

## System Design

Torrentarr uses ASP.NET Core Generic Host with hosted background services, designed for reliability, scalability, and isolation:

```mermaid
graph TB
    Host["🎛️ Generic Host<br/>(Torrentarr.Host)"]

    Host -->|registers| WebAPI["🌐 ASP.NET Core API<br/>(minimal API endpoints)"]
    Host -->|registers| Radarr["📽️ Arr Manager Service<br/>(Radarr-4K)"]
    Host -->|registers| Sonarr["📺 Arr Manager Service<br/>(Sonarr-TV)"]
    Host -->|registers| Lidarr["🎵 Arr Manager Service<br/>(Lidarr-Music)"]

    WebAPI -->|API calls| QBT["⚙️ qBittorrent<br/>(Torrent Client)"]
    Radarr -->|API calls| QBT
    Sonarr -->|API calls| QBT
    Lidarr -->|API calls| QBT

    Radarr -->|API calls| RadarrAPI["📡 Radarr API"]
    Sonarr -->|API calls| SonarrAPI["📡 Sonarr API"]
    Lidarr -->|API calls| LidarrAPI["📡 Lidarr API"]

    WebAPI -.->|reads| DB[(🗄️ SQLite<br/>Database)]
    Radarr -.->|writes| DB
    Sonarr -.->|writes| DB
    Lidarr -.->|writes| DB

    subgraph "Generic Host Responsibilities"
        H1["✅ Lifetime management (start/stop)"]
        H2["✅ Dependency injection container"]
        H3["✅ Configuration pipeline"]
        H4["✅ Graceful shutdown on SIGTERM/SIGINT"]
    end

    subgraph "ASP.NET Core API Responsibilities"
        W1["✅ REST API (/api/*, /web/*)"]
        W2["✅ React SPA (static files)"]
        W3["✅ Token authentication"]
        W4["✅ Real-time log streaming"]
    end

    subgraph "Arr Manager Responsibilities"
        A1["✅ Independent background loop"]
        A2["✅ Health monitoring"]
        A3["✅ Import triggering"]
        A4["✅ Blacklist management"]
    end

    style Host fill:#4dabf7,stroke:#1971c2,color:#000
    style WebAPI fill:#51cf66,stroke:#2f9e44,color:#000
    style Radarr fill:#ffa94d,stroke:#fd7e14,color:#000
    style Sonarr fill:#ffa94d,stroke:#fd7e14,color:#000
    style Lidarr fill:#ffa94d,stroke:#fd7e14,color:#000
    style QBT fill:#e599f7,stroke:#ae3ec9,color:#000
    style DB fill:#74c0fc,stroke:#1c7ed6,color:#000
```

**Key Architecture Principles:**

- **Service Isolation**: Each Arr instance runs as an independent `BackgroundService` — one failure doesn't affect others
- **Fault Tolerance**: The host monitors and restarts failed services via `CancellationToken` and retry policies
- **Simplicity**: No complex IPC — coordination via SQLite and external APIs
- **Dependency Injection**: All components are registered in the DI container for testability

### Core Components

#### Generic Host
**Project:** `Torrentarr.Host`

The entry point — `Program.cs` — configures and starts the ASP.NET Core Generic Host:

- Reads and validates configuration (TOML)
- Registers all services in the DI container
- Starts the ASP.NET Core HTTP server
- Starts all `IHostedService` instances
- Handles SIGTERM/SIGINT for graceful shutdown

#### ASP.NET Core API
**Project:** `Torrentarr.Host` — minimal API endpoints in `Program.cs`

Responsibilities:

- Serves REST API on `/api/*` (token-protected) and `/web/*` (public) routes
- Hosts React SPA from `Torrentarr.Host/static/` via static file middleware
- Provides token-based authentication middleware
- Serves log files and process status to the WebUI

#### Arr Manager Services
**Project:** `Torrentarr.Core` — `ArrManagerBase` and subclasses

Each configured Arr instance (Radarr/Sonarr/Lidarr) runs as an `IHostedService`:

- Independent background loop checking qBittorrent every N seconds
- Queries Arr API for media information
- Performs health checks on torrents
- Triggers imports when torrents complete
- Manages blacklisting and re-searching
- Tracks state in SQLite database

### Background Services

#### Auto-Update Service

- Checks GitHub releases for new versions on a schedule
- Downloads and validates release packages
- Triggers application restart when an update is available

#### Configuration Watcher

- Monitors `config.toml` for file-system changes
- Signals running services to reload configuration
- Triggers a `RestartLoopException`-equivalent via `CancellationToken`

## Data Flow

### Torrent Processing Pipeline

```mermaid
sequenceDiagram
    participant QBT as ⚙️ qBittorrent
    participant AM as 📡 Arr Manager
    participant DB as 🗄️ Database
    participant ARR as 🎬 Arr API

    Note over AM: Every N seconds (LoopSleepTimer)

    rect rgb(230, 245, 255)
        Note right of AM: 1. Detection Phase
        AM->>QBT: GET /api/v2/torrents/info?category=radarr-4k
        QBT-->>AM: List of torrents with tags
        AM->>AM: Filter by configured categories
    end

    rect rgb(211, 249, 216)
        Note right of AM: 2. Classification Phase
        AM->>DB: SELECT * FROM Downloads WHERE Hash IN (...)
        DB-->>AM: Tracked torrents
        AM->>AM: Determine state:<br/>downloading, stalled,<br/>completed, seeding
    end

    rect rgb(255, 243, 191)
        Note right of AM: 3. Health Check Phase
        AM->>QBT: GET torrent details (ETA, stall time, trackers)
        QBT-->>AM: Torrent health data
        AM->>AM: Check ETA vs MaxETA<br/>Check stall time vs StallTimeout<br/>Verify tracker status
    end

    rect rgb(255, 230, 230)
        Note right of AM: 4. Action Decision Phase
        alt Completed + Valid
            AM->>ARR: POST /api/v3/command (DownloadedMoviesScan)
            ARR-->>AM: Import queued
            Note over AM: ✅ Import triggered
        else Failed Health Check
            AM->>ARR: POST /api/v3/queue/blacklist (hash)
            ARR-->>AM: Blacklisted
            AM->>QBT: DELETE /api/v2/torrents/delete
            Note over AM: ❌ Blacklisted & deleted
        else Blacklisted Item
            AM->>ARR: POST /api/v3/command (MoviesSearch)
            ARR-->>AM: Search queued
            Note over AM: 🔍 Re-search triggered
        else Seeded Enough
            AM->>QBT: DELETE /api/v2/torrents/delete
            Note over AM: 🗑️ Cleaned up
        end
    end

    rect rgb(243, 232, 255)
        Note right of AM: 5. State Update Phase
        AM->>DB: UPDATE Downloads SET State=?, UpdatedAt=?
        AM->>DB: INSERT INTO EntryExpiry (EntryId, ExpiresAt)
        DB-->>AM: State persisted
        Note over AM: 💾 Audit trail updated
    end
```

**Pipeline Stages:**

1. **Detection** — Poll qBittorrent for torrents matching configured categories/tags
2. **Classification** — Query database to determine tracking state and history
3. **Health Check** — Evaluate torrent health against configured thresholds
4. **Action Decision** — Choose appropriate action (import/blacklist/re-search/cleanup)
5. **State Update** — Persist state changes and actions to database for audit trail

### Configuration Flow

```mermaid
flowchart TD
    Start([🚀 Application Start])

    Start --> LoadTOML["📄 Load TOML File<br/>(config.toml)"]

    LoadTOML --> ParseTOML["🔍 Parse & Validate<br/>(ConfigurationLoader.cs)"]

    ParseTOML --> CheckVersion{Config version<br/>matches?}

    CheckVersion -->|No| Migrate["⚙️ Apply Migrations<br/>(MigrateConfig)"]
    CheckVersion -->|Yes| EnvVars

    Migrate --> EnvVars["🌍 Environment Override<br/>(QBITRR_* env vars)"]

    EnvVars --> CheckEnv{QBITRR_*<br/>env vars?}

    CheckEnv -->|Yes| Override["✏️ Override TOML values<br/>(useful for Docker)"]
    CheckEnv -->|No| Validate

    Override --> Validate["✅ Validation<br/>(ValidateConfig)"]

    Validate --> CheckRequired{Required<br/>fields present?}

    CheckRequired -->|No| Error["❌ Error: Missing Config"]
    CheckRequired -->|Yes| DI["📦 Register in DI Container<br/>(IOptions&lt;TorrentarrConfig&gt;)"]

    DI --> StartHost["🎛️ Start Generic Host"]

    StartHost --> StartWebAPI["Start → 🌐 ASP.NET Core API"]
    StartHost --> SpawnArr1["Start → 📡 Arr Manager 1"]
    StartHost --> SpawnArr2["Start → 📡 Arr Manager 2"]

    StartWebAPI --> Runtime["⚡ Runtime"]
    SpawnArr1 --> Runtime
    SpawnArr2 --> Runtime

    Error --> End([💥 Exit])
    Runtime --> End2([✅ Running])

    style Start fill:#dee2e6,stroke:#495057,color:#000
    style LoadTOML fill:#e7f5ff,stroke:#1971c2,color:#000
    style Migrate fill:#fff3bf,stroke:#fab005,color:#000
    style Override fill:#d3f9d8,stroke:#2f9e44,color:#000
    style Validate fill:#e7f5ff,stroke:#1971c2,color:#000
    style Error fill:#ffe3e3,stroke:#c92a2a,color:#000
    style DI fill:#f3f0ff,stroke:#7950f2,color:#000
    style Runtime fill:#d3f9d8,stroke:#2f9e44,color:#000
```

**Configuration Precedence (highest to lowest):**

1. **Environment Variables** (`QBITRR_*`) — Highest priority
2. **TOML File** (`config.toml`) — Standard configuration
3. **Defaults** (in `ConfigurationLoader.cs`) — Fallback values

**Key Files:**

- `Torrentarr.Core/Configuration/ConfigurationLoader.cs` — TOML parsing, validation, migrations
- `Torrentarr.Host/Program.cs` — DI registration, host startup

### API Request Flow

```mermaid
sequenceDiagram
    participant Client as 💻 Client<br/>(React App/API)
    participant Auth as 🔐 Auth Middleware
    participant API as 🌐 ASP.NET Core API
    participant Logic as ⚙️ Handler Logic
    participant DB as 🗄️ Database
    participant ARR as 📡 Arr APIs

    Client->>API: HTTP Request<br/>GET /api/processes

    rect rgb(255, 243, 191)
        Note right of API: Authentication Phase
        API->>Auth: Check Authorization header

        alt Token Valid
            Auth-->>API: ✅ Authenticated
        else Token Missing/Invalid
            Auth-->>Client: ❌ 401 Unauthorized
            Note over Client: Request rejected
        end
    end

    rect rgb(230, 245, 255)
        Note right of API: Request Processing Phase
        API->>Logic: Route to handler

        alt Read Operation
            Logic->>DB: SELECT * FROM Downloads
            DB-->>Logic: Query results
        else Write Operation
            Logic->>DB: INSERT/UPDATE/DELETE
            DB-->>Logic: Rows affected
        else External Query
            Logic->>ARR: GET /api/v3/movie/123
            ARR-->>Logic: Movie metadata
        end
    end

    rect rgb(211, 249, 216)
        Note right of API: Response Phase
        Logic-->>API: Processed data
        API->>API: Serialize to JSON
        API-->>Client: 200 OK<br/>{ "data": [...] }
    end
```

**API Endpoints:**

- `/api/processes` — List all Arr manager services and their states
- `/api/logs` — Read log files
- `/api/config` — Read/update configuration
- `/web/status` — Public status endpoint
- `/web/qbit/categories` — qBittorrent category information

**Authentication:**

All `/api/*` endpoints require `Authorization: Bearer` header matching `WebUI.Token` from config.toml.

## Component Architecture

### Hosted Services Model

Torrentarr uses .NET's `IHostedService` / `BackgroundService` pattern:

```
┌─────────────────────────────────────────────────────────┐
│              Generic Host (Program.cs)                   │
│  - Configuration management                              │
│  - Dependency injection container                        │
│  - Service lifecycle orchestration                       │
│  - Signal handling (SIGTERM, SIGINT)                     │
└──────────────────┬──────────────────────────────────────┘
                   │ hosts
         ┌─────────┼─────────┬─────────────────┐
         │         │         │                 │
    ┌────▼───┐ ┌──▼───┐ ┌───▼────┐     ┌─────▼──────┐
    │ASP.NET │ │Radarr│ │ Sonarr │ ... │   Lidarr   │
    │  Core  │ │  Mgr │ │   Mgr  │     │    Mgr     │
    │        │ │      │ │        │     │            │
    │Minimal │ │Event │ │ Event  │     │   Event    │
    │  API   │ │Loop  │ │  Loop  │     │   Loop     │
    └────────┘ └──────┘ └────────┘     └────────────┘
         │         │         │                 │
         └─────────┴─────────┴─────────────────┘
                           │
                  ┌────────▼─────────┐
                  │  Shared Resources │
                  │  - SQLite DB      │
                  │  - Config file    │
                  │  - Log files      │
                  └───────────────────┘
```

**Service Registration** (`Program.cs`):

```csharp
var builder = WebApplication.CreateBuilder(args);

// Load Torrentarr configuration
builder.Services.Configure<TorrentarrConfig>(
    builder.Configuration.GetSection("Torrentarr"));

// Register Arr manager services
foreach (var arrConfig in config.ArrInstances.Values)
{
    builder.Services.AddHostedService(sp =>
        ArrManagerFactory.Create(arrConfig, sp));
}

// Register auto-update service
builder.Services.AddHostedService<AutoUpdateService>();

var app = builder.Build();

// Map API endpoints
app.MapGet("/web/status", GetStatus);
app.MapGet("/api/processes", GetProcesses).RequireAuthorization();
// ...

await app.RunAsync();
```

### Database Architecture

Torrentarr uses **SQLite** for state persistence:

#### Schema

```mermaid
erDiagram
    Downloads ||--o| EntryExpiry : "has expiry"

    Downloads {
        string Hash PK "Torrent hash (SHA1)"
        string Name "Torrent name"
        string ArrType "radarr | sonarr | lidarr"
        string ArrName "Arr instance name"
        int MediaId "Movie/Series/Album ID in Arr"
        string State "downloading | stalled | completed | seeding"
        datetime AddedAt "When torrent was added to qBittorrent"
        datetime UpdatedAt "Last state update"
    }

    Searches {
        int Id PK "Auto-increment primary key"
        string ArrType "radarr | sonarr | lidarr"
        string ArrName "Arr instance name"
        int MediaId "Movie/Series/Album ID in Arr"
        string Query "Search query sent to Arr"
        datetime SearchedAt "When search was executed"
        int ResultCount "Number of results returned"
    }

    EntryExpiry {
        string EntryId FK "Foreign key to Downloads.Hash"
        datetime ExpiresAt "When to delete entry"
    }
```

**Table Descriptions:**

- **Downloads** — Tracks all torrents Torrentarr is managing. Primary key is the torrent hash. Lifecycle: created on detection → updated during health checks → deleted after expiry.
- **Searches** — Records all automated searches for audit and deduplication. Auto-cleaned after 30 days.
- **EntryExpiry** — Schedules delayed cleanup after seeding goals are met.

### Event Loop Architecture

Each Arr manager's background service loop:

```mermaid
flowchart TD
    Start([⚡ BackgroundService.ExecuteAsync])

    Start --> Init["🔧 Initialize<br/>(load config, connect APIs)"]

    Init --> LoopStart{Cancellation<br/>requested?}

    LoopStart -->|Yes| Shutdown([🛑 Graceful Shutdown])
    LoopStart -->|No| FetchTorrents["📥 Fetch Torrents<br/>qbitClient.GetTorrentsAsync(category)"]

    FetchTorrents --> QueryDB["🗄️ Query Database<br/>SELECT * FROM Downloads"]

    QueryDB --> ProcessLoop["🔄 Process Each Torrent"]

    ProcessLoop --> CheckTorrent{Torrent<br/>healthy?}

    CheckTorrent -->|Yes| Import["✅ Trigger Import<br/>POST /api/v3/command"]
    CheckTorrent -->|No| Blacklist["❌ Blacklist & Delete<br/>POST /api/v3/queue/blacklist"]
    CheckTorrent -->|Stalled| Retry["⚠️ Retry or Re-search"]

    Import --> UpdateDB
    Blacklist --> UpdateDB
    Retry --> UpdateDB

    UpdateDB["💾 Update State<br/>UPDATE Downloads SET State=?"]

    UpdateDB --> Cleanup["🗑️ Cleanup Expired<br/>DELETE FROM Downloads WHERE ExpiresAt < NOW()"]

    Cleanup --> Sleep["💤 Sleep<br/>await Task.Delay(LoopSleepTimer, ct)"]

    Sleep --> LoopStart

    style Start fill:#dee2e6,stroke:#495057,color:#000
    style Shutdown fill:#ffe3e3,stroke:#c92a2a,color:#000
    style FetchTorrents fill:#e7f5ff,stroke:#1971c2,color:#000
    style Import fill:#d3f9d8,stroke:#2f9e44,color:#000
    style Blacklist fill:#ffe3e3,stroke:#c92a2a,color:#000
    style Retry fill:#fff3bf,stroke:#fab005,color:#000
    style Sleep fill:#f3f0ff,stroke:#7950f2,color:#000
```

**BackgroundService implementation:**

```csharp
public abstract class ArrManagerBase : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InitializeAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var torrents = await FetchTorrentsAsync(stoppingToken);
                var tracked = await GetTrackedTorrentsAsync(stoppingToken);

                foreach (var torrent in torrents)
                {
                    try
                    {
                        var health = await CheckHealthAsync(torrent, stoppingToken);

                        await (health switch
                        {
                            TorrentHealth.Completed => ImportAsync(torrent, stoppingToken),
                            TorrentHealth.Failed    => BlacklistAsync(torrent, stoppingToken),
                            TorrentHealth.Stalled   => HandleStalledAsync(torrent, stoppingToken),
                            _                       => Task.CompletedTask
                        });
                    }
                    catch (SkipTorrentException)
                    {
                        continue;
                    }
                }

                await UpdateStatesAsync(torrents, stoppingToken);
                await CleanupExpiredAsync(stoppingToken);

                await Task.Delay(_config.LoopSleepTimer, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;  // Graceful shutdown
            }
            catch (ApiUnavailableException ex)
            {
                _logger.LogWarning("API unavailable: {Reason}. Retrying in {Delay}s",
                    ex.Reason, ex.RetryAfter.TotalSeconds);
                await Task.Delay(ex.RetryAfter, stoppingToken);
            }
        }
    }
}
```

### Torrent State Machine

```
        ┌─────────┐
        │ Detected│ (New torrent found in qBittorrent)
        └────┬────┘
             │
        ┌────▼─────────┐
        │ Downloading  │
        └────┬─────────┘
             │
    ┌────────┴────────┐
    │                 │
┌───▼────┐      ┌────▼─────┐
│Stalled │      │Completed │
└───┬────┘      └────┬─────┘
    │                │
┌───▼────┐      ┌────▼─────┐
│Failed  │      │Importing │
└───┬────┘      └────┬─────┘
    │                │
┌───▼────────┐  ┌────▼─────┐
│Blacklisted │  │Imported  │
└───┬────────┘  └────┬─────┘
    │                │
┌───▼────────┐  ┌────▼─────┐
│Re-searching│  │ Seeding  │
└────────────┘  └────┬─────┘
                     │
                ┌────▼─────┐
                │ Deleted  │ (After seed goals met)
                └──────────┘
```

## Security Architecture

### Authentication

**WebUI Token:**

```toml
[WebUI]
Token = "your-secure-token"
```

- All `/api/*` endpoints check `Authorization: Bearer` header
- Token stored in config.toml (not in database)
- React app reads token from localStorage
- Stateless — no session management needed

**Middleware registration:**

```csharp
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var token = context.Request.Headers.Authorization
            .ToString().Replace("Bearer ", "");

        if (token != cfg.WebUI.Token)
        {
            context.Response.StatusCode = 401;
            return;
        }
    }
    await next();
});
```

### Network Binding

```toml
[WebUI]
Host = "127.0.0.1"  # Localhost only
Port = 6969
```

- Default: `0.0.0.0` for Docker
- Recommended: `127.0.0.1` behind a reverse proxy for native installs
- No TLS built-in — use nginx/Caddy for HTTPS

## Performance Characteristics

### Resource Usage

**Typical Load (4 Arr instances, 50 torrents):**

- CPU: 1-2% average, 5-10% during health checks
- RAM: 150-300 MB (.NET runtime + application)
- Disk I/O: Minimal (SQLite writes are infrequent)
- Network: 1-5 KB/s (API polling)

**Scaling:**

- Each Arr instance adds ~20-30 MB RAM (background service overhead)
- Check interval trades CPU for responsiveness
- Database size grows with torrent history

### Bottlenecks

1. **SQLite Write Contention** — Mitigated by short-lived transactions; future: PostgreSQL support
2. **Arr API Rate Limits** — Batched requests, retry with backoff
3. **qBittorrent API Overhead** — Fetch only needed fields, cache responses

## Extensibility

### Adding New Arr Types

1. Subclass `ArrManagerBase` in `Torrentarr.Core`
2. Implement `CheckHealthAsync()` and `HandleFailedAsync()`
3. Register as a hosted service in `Program.cs`
4. Add config section to `TorrentarrConfig`

### Adding New API Endpoints

```csharp
// In Program.cs — minimal API style
app.MapGet("/api/myfeature", async (IMyService svc) =>
{
    var result = await svc.GetDataAsync();
    return Results.Ok(result);
}).RequireAuthorization();
```

## Further Reading

- [Database Schema](database.md) - Complete schema documentation
- [Performance Troubleshooting](../troubleshooting/performance.md) - Optimization strategies
