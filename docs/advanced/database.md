# Database Schema

Complete reference for Torrentarr's SQLite database structure and operations.

## Overview

Torrentarr uses **SQLite** with **Entity Framework Core** for persistent state management.

**Database Location:**
- Native install: `~/config/torrentarr.db` or `./config/torrentarr.db`
- Docker: `/config/torrentarr.db`

!!! success "Single Consolidated Database (v5.8.0+)"
    As of version 5.8.0, Torrentarr uses a **single consolidated database** file (`torrentarr.db`) for all Arr instances, replacing the previous per-instance database approach.

**Why SQLite?**
- Zero configuration required
- Single-file storage (easy backups)
- ACID compliant
- Sufficient for Torrentarr's write patterns
- Cross-platform compatibility
- WAL mode for concurrent access

## Database Architecture

### Consolidated Database (v5.8.0+)

All Arr instances now share a single database file with **ArrInstance field** for data isolation:

```
torrentarr.db
├── MoviesFilesModel      (ArrInstance: "Radarr-4K", "Radarr-1080", etc.)
├── EpisodeFilesModel     (ArrInstance: "Sonarr-TV", "Sonarr-4K", etc.)
├── AlbumFilesModel       (ArrInstance: "Lidarr", etc.)
├── SeriesFilesModel      (ArrInstance: "Sonarr-TV", etc.)
├── ArtistFilesModel      (ArrInstance: "Lidarr", etc.)
├── TrackFilesModel       (ArrInstance: "Lidarr", etc.)
├── MovieQueueModel       (ArrInstance: per-Radarr instance)
├── EpisodeQueueModel     (ArrInstance: per-Sonarr instance)
├── AlbumQueueModel       (ArrInstance: per-Lidarr instance)
├── FilesQueued           (ArrInstance: cross-instance)
├── TorrentLibrary        (ArrInstance: "TAGLESS" when enabled)
└── SearchActivity        (WebUI activity tracking)
```

**Benefits:**
- Single file to backup instead of 9+ separate databases
- Simplified database management and maintenance
- Better performance with shared connection pool
- Reduced disk I/O overhead

### Schema Definition

All models include an **ArrInstance field** to isolate data by Arr instance (EF Core entity property).

Torrentarr maintains tables via **Torrentarr.Infrastructure** (EF Core entities in `TorrentarrDbContext`). Key tables:

```sql
CREATE TABLE downloads (
    hash TEXT PRIMARY KEY,           -- qBittorrent torrent hash
    name TEXT NOT NULL,              -- Torrent name
    arr_type TEXT NOT NULL,          -- "radarr", "sonarr", or "lidarr"
    arr_name TEXT NOT NULL,          -- Arr instance name from config
    media_id INTEGER NOT NULL,       -- Movie/Series/Album ID in Arr
    state TEXT NOT NULL,             -- Current state (see below)
    added_at DATETIME NOT NULL,      -- When added to qBittorrent
    updated_at DATETIME NOT NULL,    -- Last state update
    completed_at DATETIME,           -- When torrent completed
    imported_at DATETIME,            -- When imported to Arr
    ratio REAL,                      -- Current seed ratio
    seed_time INTEGER,               -- Seconds seeded
    tracker TEXT,                    -- Primary tracker domain
    category TEXT,                   -- qBittorrent category
    save_path TEXT,                  -- Download location
    content_path TEXT,               -- Path to downloaded files
    size INTEGER,                    -- Total size in bytes
    downloaded INTEGER,              -- Bytes downloaded
    uploaded INTEGER,                -- Bytes uploaded
    eta INTEGER,                     -- Estimated time remaining (seconds)
    progress REAL,                   -- Download progress (0.0-1.0)
    error_message TEXT,              -- Last error encountered
    retry_count INTEGER DEFAULT 0,   -- Number of retry attempts
    blacklisted BOOLEAN DEFAULT 0,   -- Whether torrent is blacklisted
    deleted BOOLEAN DEFAULT 0        -- Soft delete flag
);
```

**State Values:**

| State | Description |
|-------|-------------|
| `downloading` | Actively downloading |
| `stalled` | Download stalled (no progress) |
| `completed` | Download finished, not yet imported |
| `importing` | Import to Arr in progress |
| `imported` | Successfully imported to Arr |
| `seeding` | Actively seeding after import |
| `failed` | Download or import failed |
| `blacklisted` | Added to Arr blacklist |
| `deleted` | Removed from qBittorrent |

**Indexes:**

```sql
CREATE INDEX idx_downloads_arr ON downloads(arr_type, arr_name);
CREATE INDEX idx_downloads_state ON downloads(state);
CREATE INDEX idx_downloads_media_id ON downloads(media_id);
CREATE INDEX idx_downloads_updated ON downloads(updated_at);
```

#### searches

Records search activity history for auditing and rate limiting.

```sql
CREATE TABLE searches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    arr_type TEXT NOT NULL,
    arr_name TEXT NOT NULL,
    media_id INTEGER NOT NULL,
    media_title TEXT,                -- Movie/Series/Album title
    query TEXT NOT NULL,             -- Search query sent to Arr
    searched_at DATETIME NOT NULL,   -- When search was triggered
    result_count INTEGER DEFAULT 0,  -- Number of results returned
    best_result_hash TEXT,           -- Hash of best result (if grabbed)
    success BOOLEAN DEFAULT 0,       -- Whether search found results
    error_message TEXT               -- Error if search failed
);
```

**Indexes:**

```sql
CREATE INDEX idx_searches_arr ON searches(arr_type, arr_name);
CREATE INDEX idx_searches_media ON searches(media_id);
CREATE INDEX idx_searches_date ON searches(searched_at);
```

**Use Cases:**
- Prevent duplicate searches within cooldown period
- Track search success rate
- Audit trail for troubleshooting
- Rate limit Arr API calls

#### expiry

Manages automatic cleanup of old database entries.

```sql
CREATE TABLE expiry (
    entry_id TEXT PRIMARY KEY,       -- Foreign key to downloads.hash
    entry_type TEXT NOT NULL,        -- "download" or "search"
    expires_at DATETIME NOT NULL,    -- When to delete entry
    created_at DATETIME NOT NULL     -- When expiry was set
);
```

**Indexes:**

```sql
CREATE INDEX idx_expiry_time ON expiry(expires_at);
CREATE INDEX idx_expiry_type ON expiry(entry_type);
```

**Cleanup Schedule:**

```toml
[Settings]
RetentionDays = 30  # Keep entries for 30 days
```

Cleanup runs automatically during each event loop iteration.

## EF Core and DbContext

**Location:** `Torrentarr.Infrastructure/Database/` — EF Core entities and `TorrentarrDbContext`

Entities map to the tables above (e.g. `TorrentLibrary`, `MoviesFilesModel`, `EpisodeFilesModel`, queue models, `SearchActivity`). Use dependency injection to get `TorrentarrDbContext`; run queries and updates through the DbContext. All Arr workers share the same database file; data is isolated by the `ArrInstance` (or equivalent) field per entity.

## Database Operations

### Initialization

**File:** `Torrentarr.Infrastructure` (EF Core DbContext and services)

Database is initialized on first run using the centralized `get_database()` function:

Database path is set at startup (e.g. `~/config/torrentarr.db` or `./config/torrentarr.db`). Torrentarr.Host passes the path to Torrentarr.Infrastructure; EF Core creates the database and tables if they don't exist. All Arr workers share the same database file, with isolation by the `ArrInstance` field.

### Concurrency Control

**Why coordination is needed:**
- Multiple Arr worker processes may write concurrently
- SQLite WAL mode and EF Core handle coordination
- For offline recovery options, see [Database Troubleshooting](../troubleshooting/database.md).

### Transactions

EF Core wraps SaveChanges in transactions. Use the same DbContext scope for multi-entity updates to keep them atomic.

### Migrations

#### Consolidated Database Migration (v5.7.x → v5.8.0)

**Location:** Torrentarr.Host / Torrentarr.Infrastructure — on startup, old per-instance DBs are removed and the consolidated `torrentarr.db` is used.

When upgrading to v5.8.0+:
1. **Deletes old per-instance databases** (Radarr-*.db, Sonarr-*.db, etc.) if present
2. **Uses consolidated database** (`torrentarr.db`) in config directory
3. **Re-syncs data** from Arr APIs automatically

This approach ensures:

**Best Practices:**
- Always provide `null=True` and `default` values for new columns
- Test migrations on backup database first
- Increment config version in `Torrentarr.Core` (ConfigurationLoader.ExpectedConfigVersion)
- Document migration in CHANGELOG.md

## Maintenance

### Backup

**Recommended Backup Strategy:**

```bash
# Manual backup
cp ~/config/torrentarr.db ~/config/torrentarr.db.backup

# Automated backup (cron)
0 2 * * * cp ~/config/torrentarr.db ~/config/torrentarr.db.$(date +\%Y\%m\%d)

# Docker backup
docker exec torrentarr sqlite3 /config/torrentarr.db ".backup /config/torrentarr.db.backup"
```

**What to backup:**
- `torrentarr.db` - Primary database
- `config.toml` - Configuration file
- `logs/` - Optional, for troubleshooting

### VACUUM

**Optimize database size:** Torrentarr does not provide a `--vacuum-db` CLI. Use sqlite3 directly (stop Torrentarr first):

```bash
# Stop Torrentarr, then:
sqlite3 ~/config/torrentarr.db "VACUUM;"
# Or Docker: docker exec torrentarr sqlite3 /config/torrentarr.db "VACUUM;"
```

**When to vacuum:**
- After deleting large number of entries
- Database file larger than expected
- Performance degradation

**Automatic vacuum:**

```toml
[Settings]
AutoVacuum = true  # VACUUM during startup if DB > threshold
```

### Integrity Check

**Validate database integrity:**

```bash
# Check for corruption
sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"

# Expected output: ok
```

**Auto-recovery:**

Torrentarr includes automatic recovery in `Torrentarr.Infrastructure` (e.g. `DatabaseHealthService` and WAL checkpoint). For manual recovery, use sqlite3 to dump and reimport (see [Database Troubleshooting](../troubleshooting/database.md)).

### Reset Operations

**Clear all data:**

```bash
# Reset: remove database file and restart to recreate
# Stop Torrentarr, then:
rm ~/config/torrentarr.db
# Restart Torrentarr — database recreated on next start
```

There is no `--reset-torrents` or `--reset-searches` CLI; use sqlite3 or the API if such operations are exposed.

## Performance Optimization

### Indexing Strategy

Indexes are automatically created for:
- Primary keys (hash, id)
- Foreign keys (entry_id)
- Frequently queried columns (state, arr_name, updated_at)

**Impact:**
- SELECT queries: 10-100x faster with indexes
- INSERT/UPDATE: Minimal overhead (< 5%)
- Disk space: Indexes add ~20% to DB size

**Slow query example:** Avoid N+1 queries by using EF Core `.Include()` or explicit loading. Prefer single queries with joins over per-entity lookups.

**Batch operations:** Use EF Core `AddRange` and `SaveChanges` for bulk inserts; run in a single transaction scope.

## Troubleshooting

### "Database is locked"

**Cause:** Concurrent write from multiple processes without lock

**Solution:** Ensure only one process writes at a time, or use WAL mode (default). SQLite and EF Core handle coordination; if you see lock errors, check that multiple Torrentarr instances are not sharing the same DB file.

### Corruption Detection

**Symptoms:**
- "database disk image is malformed" error
- Queries returning wrong data
- Random crashes

**Recovery:**

```bash
# Try Torrentarr CLI if available (see troubleshooting docs)
# Or manual recovery:
sqlite3 ~/config/torrentarr.db ".dump" > dump.sql
mv ~/config/torrentarr.db ~/config/torrentarr.db.corrupt
sqlite3 ~/config/torrentarr.db < dump.sql
```

### High Disk Usage

**Cause:** Large number of old entries not cleaned up

**Solution:**

```toml
[Settings]
RetentionDays = 7  # Reduce from default 30
AutoVacuum = true
```

```bash
# Immediate cleanup
Use sqlite3 to run VACUUM (see above); Torrentarr has no `--vacuum-db` CLI.
```

## Security Considerations

### File Permissions

**Recommended:**

```bash
# Restrict access to database
chmod 600 ~/config/torrentarr.db
chown torrentarr:torrentarr ~/config/torrentarr.db

# Docker automatically sets via PUID/PGID
```

### Sensitive Data

**What's stored:**
- Torrent hashes (public data)
- Media IDs (internal Arr IDs)
- File paths (may contain personal info)

**What's NOT stored:**
- API keys (only in config.toml)
- Passwords
- User credentials

**Data Retention:**

```toml
[Settings]
RetentionDays = 7  # Minimize data retention for privacy
```

## Future Enhancements

**Planned for v6.0:**

- **PostgreSQL Support** - Better concurrent write performance
- **Time-series Tables** - Optimized for metrics/stats
- **Full-text Search** - Search logs and torrents
- **Schema Versioning** - Alembic-style migrations
- **Sharding** - Split data by Arr instance for scale

## Related Documentation

- [Architecture](architecture.md) - System design overview
- [Performance Troubleshooting](../troubleshooting/performance.md) - Optimization strategies
- [Troubleshooting: Database Issues](../troubleshooting/database.md) - Common problems
