# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What This Project Is

**Torrentarr** is a C# port of [qBitrr](https://github.com/Feramance/qBitrr) (Python). It automates qBittorrent torrent management with integration to Radarr, Sonarr, and Lidarr — handling Hit & Run protection, free space management, quality upgrades, and import triggering. The goal is 100% configuration and database compatibility with qBitrr while offering better performance and process isolation.

## Build & Run Commands

### .NET Backend
```bash
dotnet restore
dotnet build
dotnet build -c Release
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj    # Full orchestrator
dotnet run --project src/Torrentarr.WebUI/Torrentarr.WebUI.csproj  # WebUI only
dotnet watch --project src/Torrentarr.WebUI/Torrentarr.WebUI.csproj # Hot reload
```

### .NET Tests
```bash
# All non-live tests (suitable for CI)
dotnet test --filter "Category!=Live"

# Run a single test project
dotnet test tests/Torrentarr.Core.Tests/

# Run a single test by name
dotnet test --filter "FullyQualifiedName~ConfigurationLoaderTests"

# Live integration tests (requires real qBit/Arr services configured in config.toml)
dotnet test --filter "Category=Live"

# With coverage
dotnet test --collect:"XPlat Code Coverage" --filter "Category!=Live"
```

### React Frontend (webui/)
```bash
cd webui
npm install
npm run dev          # Vite dev server
npm run build        # Production bundle (tsc + vite build)
npm test             # Vitest (watch mode)
npx vitest run       # Vitest single run (CI)
npm run test:coverage # Coverage report
```

### Full Build Scripts
```bash
./build.sh    # Linux/macOS: builds React then .NET
build.bat     # Windows equivalent
```

### Docker
```bash
docker build -t torrentarr:latest . --no-cache
docker-compose up -d
docker-compose logs -f torrentarr
```

## Architecture

Torrentarr uses a **process-orchestrated multi-tier architecture** with process isolation between the WebUI and workers:

```
Torrentarr.Host (orchestrator)
├── Hosts Torrentarr.WebUI (always online, port 6969)
├── Manages free space globally (across ALL qBit instances)
├── Handles special categories (failed, recheck)
└── Spawns per-Arr Worker processes
    ├── Radarr Worker (Torrentarr.Workers)
    ├── Sonarr Worker (Torrentarr.Workers)
    └── Lidarr Worker (Torrentarr.Workers)

All processes share:
    Torrentarr.Infrastructure (API clients, EF Core, services)
    Torrentarr.Core (interfaces, config models)
```

**Key design decision:** Workers are separate processes so the WebUI stays online even if a worker crashes. This differs from monolithic qBitrr.

### Five Projects

| Project | Purpose |
|---|---|
| `Torrentarr.Core` | Domain interfaces, configuration models (TOML via Tomlyn) |
| `Torrentarr.Infrastructure` | API clients (QBittorrent, Radarr, Sonarr, Lidarr), EF Core SQLite DB, 13 services |
| `Torrentarr.WebUI` | ASP.NET Core REST API + React frontend (SPA proxy in dev, static files in prod) |
| `Torrentarr.Workers` | Background worker entry point, per-Arr job orchestration |
| `Torrentarr.Host` | Main orchestrator — launches WebUI + workers, manages lifecycle |

### Infrastructure Services

- `TorrentProcessor` — State machine for torrent lifecycle (core processing loop)
- `SeedingService` — Hit & Run rules (category + tracker-based limits)
- `FreeSpaceService` — Auto pause/resume based on disk space per torrent
- `ArrMediaService` — Triggers missing media search and quality upgrades
- `ArrImportService` — Triggers manual imports in Radarr/Sonarr/Lidarr
- `ArrSyncService` — Syncs Arr queue/media data into local SQLite DB
- `TorrentCacheService` — In-memory cache to reduce qBittorrent API calls
- `MediaValidationService` — ffprobe integration for file integrity checks
- `DatabaseHealthService` — SQLite WAL checkpoint, VACUUM, integrity checks
- `ConnectivityService` — Internet connectivity detection before processing
- `QBittorrentConnectionManager` — Connection pooling/management
- `ArrWorkerManager` — Per-instance worker process lifecycle

### Database

SQLite (`qbitrr.db`, same schema as qBitrr for compatibility). Uses EF Core with WAL mode and memory-mapped I/O pragmas. Entities: `TorrentLibrary`, `MoviesFilesModel`, `EpisodeFilesModel`, `AlbumFilesModel`, `SeriesFilesModel`, `ArtistFilesModel`, `TrackFilesModel`, queue models.

### Configuration

`config.toml` is 100% compatible with qBitrr's format. Search order:
1. `TORRENTARR_CONFIG` environment variable (takes priority — used for tests and Docker)
2. `~/config/config.toml`
3. `~/.config/qbitrr/config.toml`
4. `~/.config/torrentarr/config.toml`
5. `./config.toml`

Key config sections: `[Settings]`, `[WebUI]`, `[qBit]`, `[qBit.CategorySeeding]`, `[Radarr-*]`, `[Sonarr-*]`, `[Lidarr-*]`. Both Arr instances and qBittorrent instances are uniform dictionaries — `[Radarr-4K]` and `[qBit-seedbox]` follow the same pattern. `[qBit]` is the conventional name for the primary qBit instance but carries no special status in code; all qBit instances are equal.

**qBit instance model:** `TorrentarrConfig.QBitInstances` is a `Dictionary<string, QBitConfig>` keyed by section name (e.g., `"qBit"`, `"qBit-seedbox"`). There is no `PrimaryQBit` or "default" concept — services receive the full config and look up per-instance settings via `torrent.QBitInstanceName`. `TorrentInfo.QBitInstanceName` (`[JsonIgnore]`) is set when fetching torrents from a client, recording which qBit instance a torrent came from. `QBittorrentConnectionManager` stores clients by instance name; use `GetAllClients()` — there is no `GetDefaultClient()`.

**Per-instance seeding config:** `[qBit.CategorySeeding]` and `[qBit.Trackers]` are per-qBit-instance. `SeedingService` uses `torrent.QBitInstanceName` to look up the correct `QBitConfig.CategorySeeding` and `Trackers` for each torrent. Seeding rules from one instance never apply to torrents from another.

**Cross-instance free space:** `FreeSpaceService` iterates ALL configured qBit instances, gathers torrents from all clients, sorts them globally by `AddedOn` date, and processes the oldest first. `DownloadPath` is checked per-instance for space.

**Config version:** Current format is `5.9.0`. Notable fields: `v5 = true` in `[qBit]` for qBittorrent v5 auth. Seeding configuration (`HitAndRunMode`, `MinSeedRatio`, `MinSeedingTimeDays`, etc.) lives in `[qBit.CategorySeeding]` per qBit instance — not in `[WebUI]`.

**TOML serialization rule:** Arrays that may contain regex or file extension patterns (e.g., `FileExtensionAllowlist`) must use single-quoted TOML literal strings (`'\.mkv'`) to avoid invalid escape sequences — Tomlyn enforces strict TOML and rejects `\.` in double-quoted strings.

## Tests

Three test projects under `tests/`, plus frontend tests in `webui/src/__tests__/`. ~128 total tests (85 .NET, 43 frontend).

| Project | Coverage |
|---|---|
| `tests/Torrentarr.Core.Tests` | Config parsing, model defaults — pure unit, no mocks |
| `tests/Torrentarr.Infrastructure.Tests` | Services (unit + mocked), API clients (live, gated) |
| `tests/Torrentarr.Host.Tests` | API endpoint integration tests via `WebApplicationFactory<Program>` |
| `webui/src/__tests__/` | API client deserialization, page rendering, components (Vitest + MSW) |

**Host test isolation:** `TorrentarrWebApplicationFactory` writes a minimal `TestConfigToml` to a temp file and sets `TORRENTARR_CONFIG` in its constructor (before `Program.cs` runs) so tests never touch the user's real config. Background workers are removed from DI; the in-memory SQLite connection is kept alive for the factory lifetime.

**Live tests** (`[Trait("Category", "Live")]`): Load settings from the user's config file at runtime. If the config doesn't exist or the service isn't reachable, the test stores `_skipReason` in `InitializeAsync` and calls `Skip.If(_skipReason != null, ...)` at the start of each `[SkippableFact]`. Requires `Xunit.SkippableFact` package.

**Frontend test stack:** Vitest + `@testing-library/react` + MSW (intercepts `fetch()` with hardcoded fixtures). Setup file at `webui/src/__tests__/setup.ts`. Tests that render components using `useWebUI()` must wrap in `<WebUIProvider>` inside the test wrapper.

## Tech Stack

- **.NET 10.0** / **C# 12+** — async throughout, nullable reference types enabled
- **ASP.NET Core** — REST API, Kestrel, static file serving for React SPA
- **Entity Framework Core 9** + **SQLite** — WAL mode
- **RestSharp 112** — HTTP client for qBittorrent and Arr API calls
- **Tomlyn** — TOML config parsing (strict — no invalid escapes in double-quoted strings)
- **Serilog** — structured logging (console + rolling file sinks)
- **Polly** — retry/circuit-breaker policies on external API calls
- **FFMpegCore** — ffprobe wrapper for media validation
- **React 18** + **React Router 6** + **TypeScript** — frontend SPA in `webui/` (Vite, not CRA)
- **Vitest** + **MSW** — frontend test runner and fetch mocking
- **xUnit** + **Moq** + **FluentAssertions** — .NET test stack

## CI/CD

GitHub Actions runs a matrix build across Ubuntu, Windows, and macOS with .NET 10 + Node 20. Pipeline: restore → build → test (non-live) → frontend build → Docker build (on `master` push). Artifacts retained 7 days.
