# Database Troubleshooting

This guide covers SQLite database structure, common issues, recovery procedures, and optimization techniques for Torrentarr's persistent storage.

## Overview

Torrentarr uses a **single consolidated SQLite database** to maintain persistent state across restarts:

| Database File | Purpose | Location |
|--------------|---------|----------|
| `torrentarr.db` | **Single consolidated database** for all Arr instances and WebUI data | `~/config/qBitManager/` or `/config/qBitManager/` |
| `torrentarr.db-wal` | Write-Ahead Log (uncommitted changes) | Same directory |
| `torrentarr.db-shm` | Shared memory index for WAL mode | Same directory |

!!! success "Database Consolidation (v5.8.0+)"
    As of version 5.8.0, Torrentarr uses a **single `torrentarr.db` file** instead of separate per-instance databases. All data is isolated using the `ArrInstance` field in each table.

!!! info "Database Modes"
    Torrentarr uses SQLite's **WAL (Write-Ahead Logging)** mode for better concurrency and crash resilience. WAL mode creates `-wal` and `-shm` temporary files alongside the main database.

### Migration from v5.7.x

When upgrading from v5.7.x or earlier:

1. **Old databases are automatically deleted** on first startup (Radarr-*.db, Sonarr-*.db, Lidarr.db, webui_activity.db)
2. **New consolidated database is created** (`torrentarr.db`)
3. **Data is automatically re-synced** from your Arr instances (takes 5-30 minutes depending on library size)
4. **No manual intervention required** - this happens automatically

---

## Database Schema

### Consolidated Database Tables

!!! info "ArrInstance Field"
    All tables include an **ArrInstance** field (added in v5.8.0) to isolate data by Arr instance within the single consolidated database:

    ```sql
    ArrInstance TEXT DEFAULT ""  -- Instance name (e.g., "Radarr-4K", "Sonarr-TV")
    ```

#### TorrentLibrary
Tracks all managed torrents across qBittorrent.

```sql
CREATE TABLE torrentlibrary (
    ArrInstance TEXT DEFAULT "",     -- Arr instance name
    Hash TEXT NOT NULL,              -- qBittorrent torrent hash
    Category TEXT NOT NULL,          -- qBittorrent category
    AllowedSeeding BOOLEAN,          -- Can seed (passed health checks)
    Imported BOOLEAN,                -- Successfully imported to Arr
    AllowedStalled BOOLEAN,          -- Exempt from stalled checks
    FreeSpacePaused BOOLEAN          -- Paused by free space manager
);
```

**Fields**:

- **Hash**: Unique torrent identifier from qBittorrent
- **Category**: Maps to Radarr/Sonarr/Lidarr category in config
- **AllowedSeeding**: `True` if torrent passed health checks and can seed
- **Imported**: `True` if files successfully imported to Arr instance
- **AllowedStalled**: `True` if torrent is exempt from stalled detection
- **FreeSpacePaused**: `True` if paused by disk space manager

#### MoviesFilesModel (Radarr)
Tracks movie library state and search history.

```sql
CREATE TABLE moviesfilesmodel (
    ArrInstance TEXT DEFAULT "",     -- Arr instance name (e.g., "Radarr-4K")
    Title TEXT,
    Monitored BOOLEAN,
    TmdbId INTEGER,
    Year INTEGER,
    EntryId INTEGER UNIQUE,          -- Radarr movie ID
    Searched BOOLEAN,                -- Searched for this movie
    MovieFileId INTEGER,             -- Current file ID (0 = missing)
    IsRequest BOOLEAN,               -- From Overseerr/Ombi
    QualityMet BOOLEAN,              -- Quality cutoff met
    Upgrade BOOLEAN,                 -- Upgrade in progress
    CustomFormatScore INTEGER,       -- Current CF score
    MinCustomFormatScore INTEGER,    -- Target CF score
    CustomFormatMet BOOLEAN,         -- CF score met
    Reason TEXT,                     -- Why searched/upgraded
    QualityProfileId INTEGER,        -- Current profile ID
    QualityProfileName TEXT,         -- Current profile name
    LastProfileSwitchTime DATETIME,  -- Last profile change
    CurrentProfileId INTEGER,        -- Active profile ID
    OriginalProfileId INTEGER        -- Original profile ID
);
```

**Key Fields**:

- **EntryId**: Primary key, matches Radarr's movie ID
- **Searched**: Tracks if automated search was triggered
- **QualityMet**: `True` when quality cutoff reached
- **CustomFormatScore**: Current score vs. MinCustomFormatScore
- **Profile Tracking**: Supports profile switching for searches

#### SeriesFilesModel (Sonarr)
Tracks TV series library state.

```sql
CREATE TABLE seriesfilesmodel (
    EntryId INTEGER PRIMARY KEY,     -- Sonarr series ID
    Title TEXT,
    Monitored BOOLEAN,
    Searched BOOLEAN,
    Upgrade BOOLEAN,
    MinCustomFormatScore INTEGER,
    QualityProfileId INTEGER,
    QualityProfileName TEXT
);
```

#### EpisodeFilesModel (Sonarr)
Tracks individual episodes (granular tracking).

```sql
CREATE TABLE episodefilesmodel (
    EntryId INTEGER PRIMARY KEY,     -- Sonarr episode ID
    SeriesTitle TEXT,
    Title TEXT,
    SeriesId INTEGER NOT NULL,
    EpisodeFileId INTEGER,
    EpisodeNumber INTEGER NOT NULL,
    SeasonNumber INTEGER NOT NULL,
    AbsoluteEpisodeNumber INTEGER,
    SceneAbsoluteEpisodeNumber INTEGER,
    AirDateUtc DATETIME,
    Monitored BOOLEAN,
    Searched BOOLEAN,
    IsRequest BOOLEAN,
    QualityMet BOOLEAN,
    Upgrade BOOLEAN,
    CustomFormatScore INTEGER,
    MinCustomFormatScore INTEGER,
    CustomFormatMet BOOLEAN,
    Reason TEXT,
    QualityProfileId INTEGER,
    QualityProfileName TEXT,
    LastProfileSwitchTime DATETIME,
    CurrentProfileId INTEGER,
    OriginalProfileId INTEGER
);
```

#### AlbumFilesModel (Lidarr)
Tracks music albums.

```sql
CREATE TABLE albumfilesmodel (
    Title TEXT,
    Monitored BOOLEAN,
    ForeignAlbumId TEXT,             -- MusicBrainz ID
    ReleaseDate DATETIME,
    EntryId INTEGER UNIQUE,          -- Lidarr album ID
    Searched BOOLEAN,
    AlbumFileId INTEGER,
    IsRequest BOOLEAN,
    QualityMet BOOLEAN,
    Upgrade BOOLEAN,
    CustomFormatScore INTEGER,
    MinCustomFormatScore INTEGER,
    CustomFormatMet BOOLEAN,
    Reason TEXT,
    ArtistId INTEGER NOT NULL,
    ArtistTitle TEXT,
    QualityProfileId INTEGER,
    QualityProfileName TEXT,
    LastProfileSwitchTime DATETIME,
    CurrentProfileId INTEGER,
    OriginalProfileId INTEGER
);
```

#### TrackFilesModel (Lidarr)
Tracks individual music tracks.

```sql
CREATE TABLE trackfilesmodel (
    EntryId INTEGER PRIMARY KEY,     -- Lidarr track ID
    AlbumId INTEGER NOT NULL,
    TrackNumber INTEGER,
    Title TEXT,
    Duration INTEGER,                -- Duration in seconds
    HasFile BOOLEAN,
    TrackFileId INTEGER,
    Monitored BOOLEAN
);
```

#### ArtistFilesModel (Lidarr)
Tracks music artists.

```sql
CREATE TABLE artistfilesmodel (
    EntryId INTEGER PRIMARY KEY,     -- Lidarr artist ID
    Title TEXT,
    Monitored BOOLEAN,
    Searched BOOLEAN,
    Upgrade BOOLEAN,
    MinCustomFormatScore INTEGER,
    QualityProfileId INTEGER,
    QualityProfileName TEXT
);
```

#### Queue Models
Track import queue state.

```sql
CREATE TABLE moviequeuemodel (
    EntryId INTEGER UNIQUE,
    Completed BOOLEAN DEFAULT FALSE
);

CREATE TABLE episodequeuemodel (
    EntryId INTEGER UNIQUE,
    Completed BOOLEAN DEFAULT FALSE
);

CREATE TABLE albumqueuemodel (
    EntryId INTEGER UNIQUE,
    Completed BOOLEAN DEFAULT FALSE
);
```

### SearchActivity (WebUI)
Tracks search activity for the WebUI dashboard.

```sql
CREATE TABLE searchactivity (
    category TEXT PRIMARY KEY,       -- Arr instance category
    summary TEXT,                    -- Search summary/status
    timestamp TEXT                   -- Last search timestamp
);
```

!!! note "Consolidated in v5.8.0"
    Prior to v5.8.0, each Arr instance had separate database files. Now all data is in the single `torrentarr.db` file with the `ArrInstance` field providing isolation.

---

## Common Database Issues

### 1. Database Locked Errors

**Symptoms**:
```
sqlite3.OperationalError: database is locked
```

**Causes**:

- Multiple Torrentarr instances accessing the same database
- Long-running transactions blocking writes
- File system latency (NFS, cloud storage)

**Solutions**:

=== "Check Running Instances"
    ```bash
    # Docker
    docker ps --filter name=torrentarr

    # Systemd
    systemctl status torrentarr

    # Manual check
    ps aux | grep torrentarr
    ```

    Stop duplicate instances before proceeding.

=== "Wait for Lock Release"
    Torrentarr automatically retries locked operations with exponential backoff:

    **Internal retry logic (automatic):**

    - Attempt 1: Wait 0.5s
    - Attempt 2: Wait 1.0s
    - Attempt 3: Wait 2.0s
    - Attempt 4: Wait 4.0s
    - Attempt 5: Wait 8.0s (max 10s)

    If locks persist beyond 5 retries:

    ```bash
    # Check for stale lock files
    ls -lh ~/config/*.db.lock

    # Remove stale locks (ONLY if Torrentarr is stopped)
    rm ~/config/*.db.lock
    ```

=== "Restart Service"
    ```bash
    # Docker
    docker restart torrentarr

    # Systemd
    sudo systemctl restart torrentarr
    ```

---

### 2. Database Corruption

**Symptoms**:
```
sqlite3.DatabaseError: database disk image is malformed
sqlite3.OperationalError: disk I/O error
```

**Causes**:

- Unexpected shutdown (power loss, container crash)
- Disk full during write operation
- Hardware failures (bad sectors, memory errors)
- File system corruption (ext4, btrfs, zfs)

**Automatic Recovery**:

Torrentarr automatically attempts recovery when corruption is detected:

1. **WAL Checkpoint**: Flushes WAL to main database
2. **Full Repair**: Dumps recoverable data to new database
3. **Backup**: Original database saved as `torrentarr.db.backup`

**Manual Recovery**:

=== "Method 1: WAL Checkpoint"
    ```bash
    # Stop Torrentarr first
    docker stop torrentarr  # or: systemctl stop torrentarr

    # Checkpoint WAL (least invasive)
    sqlite3 ~/config/torrentarr.db "PRAGMA wal_checkpoint(TRUNCATE);"

    # Verify integrity
    sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"

    # Restart
    docker start torrentarr
    ```

=== "Method 2: Dump/Restore"
    ```bash
    # Stop Torrentarr
    docker stop torrentarr

    # Backup corrupted database
    cp ~/config/torrentarr.db ~/config/torrentarr.db.corrupt

    # Dump recoverable data
    sqlite3 ~/config/torrentarr.db ".dump" > ~/config/dump.sql

    # Create new database from dump
    rm ~/config/torrentarr.db
    sqlite3 ~/config/torrentarr.db < ~/config/dump.sql

    # Verify
    sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"

    # Restart
    docker start torrentarr
    ```

=== "Method 3: Fresh Start"
    ```bash
    # CAUTION: Loses all history (searches, imports, etc.)

    # Stop Torrentarr
    docker stop torrentarr

    # Backup old database
    mv ~/config/torrentarr.db ~/config/torrentarr.db.old

    # Restart (Torrentarr creates new database)
    docker start torrentarr

    # Verify
    docker logs -f torrentarr
    ```

!!! warning "Data Loss"
    - **WAL Checkpoint**: No data loss if successful
    - **Dump/Restore**: May lose corrupted rows/tables
    - **Fresh Start**: Loses all history (searches, import state, torrent tracking)

#### Corruption Prevention

Follow these practices to minimize corruption risk:

1. **Use local storage** — SQLite on NFS/CIFS is unreliable due to file locking issues
2. **Graceful shutdown** — Always use `docker stop` (not `docker kill`); set `stop_grace_period: 30s` in Docker Compose
3. **Ensure adequate disk space** — WAL operations need temporary space; configure `FreeSpace` in config
4. **Regular backups** — Set up daily backups of `torrentarr.db` (see [Backup & Restore](#database-backup-restore))
5. **Proper permissions** — Ensure the Torrentarr user owns the database files (`chown 1000:1000 /config/qBitManager/torrentarr.db`)

!!! note "Historical Fix: synchronous Setting"
    Prior to the fix in commit `465c306d`, Torrentarr used `PRAGMA synchronous=0` (OFF), which traded data integrity for write speed. This was changed to `synchronous=1` (NORMAL), which prevents corruption from power loss/crashes with minimal performance impact (~5-10% write latency increase, mitigated by WAL mode).

---

### 3. Disk I/O Errors

**Symptoms**:
```
sqlite3.OperationalError: disk I/O error
OSError: [Errno 5] Input/output error
```

**Causes**:

- Disk full (no space for WAL or temp files)
- Failing storage device (HDD, SSD wear)
- Network storage issues (NFS, SMB timeouts)
- File system errors (unmounted, read-only)

**Diagnosis**:

=== "Check Disk Space"
    ```bash
    # Docker
    docker exec torrentarr df -h /config

    # Host
    df -h ~/config
    ```

    Torrentarr requires:
    - Minimum 100MB free for WAL operations
    - ~2x database size for VACUUM operations

=== "Check File Permissions"
    ```bash
    # Docker
    docker exec torrentarr ls -lh /config/torrentarr.db

    # Host
    ls -lh ~/config/torrentarr.db
    ```

    Ensure Torrentarr user owns the database:
    ```bash
    # Docker (PUID/PGID)
    chown 1000:1000 ~/config/torrentarr.db

    # Systemd
    chown torrentarr:torrentarr ~/config/torrentarr.db
    ```

=== "Test Disk Health"
    ```bash
    # Check for bad sectors
    sudo badblocks -sv /dev/sdX

    # SMART status
    sudo smartctl -a /dev/sdX
    ```

**Solutions**:

1. **Free Disk Space**: Delete old logs, torrents, or unused files
2. **Fix Permissions**: Ensure Torrentarr can read/write database
3. **Move Database**: Relocate to healthier storage
   ```bash
   # Stop Torrentarr
   docker stop torrentarr

   # Move database
   mv ~/config/torrentarr.db /new/path/torrentarr.db

   # Update config.toml
   # (No explicit DB path in config; just move entire config folder)

   # Restart
   docker start torrentarr
   ```

---

### 4. Duplicate Entry Errors

**Symptoms**:
```
sqlite3.IntegrityError: UNIQUE constraint failed: moviesfilesmodel.EntryId
```

**Causes**:

- Race condition during multi-threaded writes (rare)
- Manual database modifications
- Database restored from backup while Torrentarr was running

**Solutions**:

=== "Remove Duplicate"
    ```bash
    # Stop Torrentarr
    docker stop torrentarr

    # Identify duplicate
    sqlite3 ~/config/torrentarr.db << EOF
    SELECT EntryId, COUNT(*) as count
    FROM moviesfilesmodel
    GROUP BY EntryId
    HAVING count > 1;
    EOF

    # Delete duplicate (keep first entry)
    sqlite3 ~/config/torrentarr.db << EOF
    DELETE FROM moviesfilesmodel
    WHERE rowid NOT IN (
        SELECT MIN(rowid)
        FROM moviesfilesmodel
        GROUP BY EntryId
    );
    EOF

    # Restart
    docker start torrentarr
    ```

=== "Rebuild Database"
    ```bash
    # Stop Torrentarr
    docker stop torrentarr

    # Backup
    mv ~/config/torrentarr.db ~/config/torrentarr.db.backup

    # Restart (rebuilds from Arr APIs)
    docker start torrentarr
    ```

---

### 5. Missing Tables

**Symptoms**:
```
sqlite3.OperationalError: no such table: moviesfilesmodel
```

**Causes**:

- Database created by older Torrentarr version
- Incomplete migration from previous version
- Corrupted schema

**Solutions**:

=== "Verify Schema"
    ```bash
    sqlite3 ~/config/torrentarr.db << EOF
    .tables
    .schema moviesfilesmodel
    EOF
    ```

    Expected tables:
    - `torrentlibrary`
    - `moviesfilesmodel`, `moviequeuemodel`
    - `seriesfilesmodel`, `episodefilesmodel`, `episodequeuemodel`
    - `albumfilesmodel`, `trackfilesmodel`, `artistfilesmodel`, `albumqueuemodel`

=== "Recreate Database"
    ```bash
    # Stop Torrentarr
    docker stop torrentarr

    # Backup old database
    mv ~/config/torrentarr.db ~/config/torrentarr.db.old

    # Restart (creates new database with current schema)
    docker start torrentarr
    ```

---

## Manual Database Operations

!!! danger "Stop Torrentarr First"
    Always stop Torrentarr before direct database manipulation to avoid corruption.

### Inspecting Database

```bash
# Open interactive shell
sqlite3 ~/config/torrentarr.db

# List all tables
.tables

# Show table schema
.schema moviesfilesmodel

# Query movies
SELECT Title, Monitored, QualityMet, CustomFormatMet
FROM moviesfilesmodel
WHERE Monitored = 1 AND QualityMet = 0
LIMIT 10;

# Count torrents by category
SELECT Category, COUNT(*) as count
FROM torrentlibrary
GROUP BY Category;

# Show movies searched recently
SELECT Title, Searched, Reason
FROM moviesfilesmodel
WHERE Searched = 1
ORDER BY EntryId DESC
LIMIT 20;
```

### Resetting Search State

```bash
# Reset all movie searches (triggers new automated search)
sqlite3 ~/config/torrentarr.db << EOF
UPDATE moviesfilesmodel SET Searched = 0 WHERE Searched = 1;
EOF

# Reset specific movie
sqlite3 ~/config/torrentarr.db << EOF
UPDATE moviesfilesmodel SET Searched = 0 WHERE EntryId = 123;
EOF

# Reset TV series searches
sqlite3 ~/config/torrentarr.db << EOF
UPDATE seriesfilesmodel SET Searched = 0 WHERE Searched = 1;
EOF

# Reset episode searches
sqlite3 ~/config/torrentarr.db << EOF
UPDATE episodefilesmodel SET Searched = 0 WHERE Searched = 1;
EOF
```

### Clearing Torrent Tracking

```bash
# Remove specific torrent
sqlite3 ~/config/torrentarr.db << EOF
DELETE FROM torrentlibrary WHERE Hash = 'abc123...';
EOF

# Clear all torrents for category
sqlite3 ~/config/torrentarr.db << EOF
DELETE FROM torrentlibrary WHERE Category = 'radarr-movies';
EOF

# Clear all torrents (CAUTION: Torrentarr re-adds active torrents)
sqlite3 ~/config/torrentarr.db << EOF
DELETE FROM torrentlibrary;
EOF
```

### Forcing Profile Switch

```bash
# Reset profile tracking (allows new switch attempt)
sqlite3 ~/config/torrentarr.db << EOF
UPDATE moviesfilesmodel
SET LastProfileSwitchTime = NULL,
    CurrentProfileId = OriginalProfileId
WHERE EntryId = 123;
EOF
```

---

## Database Backup & Restore

### Automated Backups

Torrentarr automatically creates backups during recovery:

- **Location**: `~/config/torrentarr.db.backup`
- **Trigger**: Before repair operations
- **Retention**: Single backup (overwrites previous)

### Manual Backups

=== "Simple Copy"
    ```bash
    # Stop Torrentarr
    docker stop torrentarr

    # Backup consolidated database (v5.8.0+)
    cp ~/config/qBitManager/torrentarr.db ~/backups/torrentarr-$(date +%Y%m%d).db

    # Also backup WAL and SHM files for consistency
    cp ~/config/qBitManager/torrentarr.db* ~/backups/

    # Restart
    docker start torrentarr
    ```

    !!! tip "Single File Backup"
        With the consolidated database, you only need to backup **one file** (`torrentarr.db`) instead of multiple per-instance databases!

=== "SQLite Backup Command"
    ```bash
    # Hot backup (no stop required, but slower)
    sqlite3 ~/config/qBitManager/torrentarr.db << EOF
    .backup /backups/torrentarr-$(date +%Y%m%d).db
    EOF
    ```

=== "Dump to SQL"
    ```bash
    # Human-readable backup
    sqlite3 ~/config/qBitManager/torrentarr.db .dump > ~/backups/torrentarr-$(date +%Y%m%d).sql
    ```

### Restore from Backup

```bash
# Stop Torrentarr
docker stop torrentarr

# Restore database
cp ~/backups/torrentarr-20231127.db ~/config/torrentarr.db

# Verify integrity
sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"

# Restart
docker start torrentarr
```

---

## Performance Optimization

### VACUUM Database

Reclaims space and optimizes database file.

```bash
# Stop Torrentarr
docker stop torrentarr

# Run VACUUM
sqlite3 ~/config/torrentarr.db "VACUUM;"

# Check new size
ls -lh ~/config/torrentarr.db

# Restart
docker start torrentarr
```

**When to VACUUM**:

- After deleting large amounts of data
- Database file much larger than expected
- Query performance degradation

**Requirements**:

- Free disk space: ~2x current database size
- Downtime: 5-60 seconds depending on database size

### ANALYZE Statistics

Updates query planner statistics for better performance.

```bash
# Hot operation (no stop required)
sqlite3 ~/config/torrentarr.db "ANALYZE;"
```

Run after:

- Adding/deleting many rows
- Query performance issues
- Database grows significantly

### PRAGMA Settings

Torrentarr uses optimized PRAGMA settings (automatically applied):

```sql
PRAGMA journal_mode=WAL;           -- Write-Ahead Logging
PRAGMA synchronous=NORMAL;         -- Balance safety vs. speed
PRAGMA cache_size=-64000;          -- 64MB cache
PRAGMA temp_store=MEMORY;          -- Temp tables in RAM
PRAGMA mmap_size=268435456;        -- 256MB memory-mapped I/O
```

!!! info "Tuning"
    These settings are tuned for typical workloads. Adjust only if experiencing issues:

    ```bash
    # Increase cache for large libraries
    sqlite3 ~/config/torrentarr.db "PRAGMA cache_size=-128000;"  # 128MB
    ```

---

## Database Monitoring

### Health Checks

Torrentarr performs automatic health checks every 10 event loop iterations:

```python
# Internal code (automatic)
healthy, msg = check_database_health(db_path)
if not healthy:
    logger.warning("Database unhealthy: %s", msg)
    # Automatic recovery attempted
```

**Manual Health Check**:

```bash
# Quick check (fast)
sqlite3 ~/config/torrentarr.db "PRAGMA quick_check;"

# Full integrity check (slow, thorough)
sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"
```

Expected output: `ok`

### Database Size Monitoring

```bash
# Check database size
ls -lh ~/config/*.db

# Check WAL size (should be small)
ls -lh ~/config/*.db-wal
```

**Typical Sizes**:

| Library Size | Database Size | WAL Size |
|-------------|---------------|----------|
| Small (< 500 items) | 1-5 MB | < 1 MB |
| Medium (500-5,000) | 5-50 MB | 1-5 MB |
| Large (5,000-50,000) | 50-500 MB | 5-20 MB |
| Very Large (> 50,000) | 500 MB - 2 GB | 20-100 MB |

!!! warning "Large WAL Files"
    If WAL file grows beyond 100 MB:

    ```bash
    # Force checkpoint
    docker stop torrentarr
    sqlite3 ~/config/torrentarr.db "PRAGMA wal_checkpoint(TRUNCATE);"
    docker start torrentarr
    ```

### Query Performance Analysis

```bash
# Enable query timer
sqlite3 ~/config/torrentarr.db << EOF
.timer ON
.eqp ON

-- Test slow query
SELECT * FROM moviesfilesmodel WHERE Monitored = 1 AND QualityMet = 0;
EOF
```

If queries are slow:

1. Run `ANALYZE;`
2. Check database size (may need VACUUM)
3. Consider adding indexes (advanced)

---

## Database Migration

### Upgrading Torrentarr

Torrentarr automatically handles database migrations during startup:

```python
# Internal migration logic (automatic)
EXPECTED_CONFIG_VERSION = 4
apply_config_migrations()  # Migrates database schema
```

**Manual Verification**:

```bash
# Check schema version (no built-in version field)
sqlite3 ~/config/torrentarr.db << EOF
.schema
EOF

# Compare with expected schema (see "Database Schema" section)
```

### Downgrading Torrentarr

!!! danger "Downgrade Risk"
    Downgrading Torrentarr may fail if newer version added database fields.

**Safe Downgrade**:

```bash
# Backup database
cp ~/config/torrentarr.db ~/backups/torrentarr-before-downgrade.db

# Delete database (force rebuild)
rm ~/config/torrentarr.db

# Downgrade Torrentarr
docker pull feramance/torrentarr:5.2.0
docker stop torrentarr && docker rm torrentarr

# Start old version (rebuilds database)
docker run -d --name torrentarr feramance/torrentarr:5.2.0 ...
```

---

## Advanced Topics

### WAL Mode Details

Torrentarr uses SQLite's Write-Ahead Logging (WAL) mode for:

- **Concurrent Access**: Readers don't block writers
- **Crash Recovery**: Automatic rollback on unexpected shutdown
- **Performance**: Faster writes (batched commits)

**WAL Files**:

- `torrentarr.db-wal`: Write-ahead log (uncommitted changes)
- `torrentarr.db-shm`: Shared memory index for WAL

**Checkpoint Modes**:

| Mode | Description | Use Case |
|------|-------------|----------|
| PASSIVE | Checkpoint without blocking | Background maintenance |
| FULL | Checkpoint all frames | Before backup |
| RESTART | Reset WAL to beginning | After large writes |
| TRUNCATE | Checkpoint + shrink WAL | Reclaim disk space |

### Multi-Process Coordination

Torrentarr uses inter-process file locks to coordinate database access:

```python
# Lock file: ~/config/torrentarr.db.lock
with database_lock():
    # All database operations here
    db.execute_sql("UPDATE ...")
```

**Lock Mechanism**:

- **Windows**: `msvcrt.locking()` (mandatory locks)
- **Linux/macOS**: `fcntl.flock()` (advisory locks)

**Re-entrant**: Same process can acquire lock multiple times (reference counting).

### Custom Indexes

For very large libraries (50,000+ items), consider adding indexes:

```sql
-- Speed up monitored + quality queries
CREATE INDEX IF NOT EXISTS idx_movies_quality
ON moviesfilesmodel(Monitored, QualityMet, CustomFormatMet);

-- Speed up episode lookups
CREATE INDEX IF NOT EXISTS idx_episodes_series
ON episodefilesmodel(SeriesId, SeasonNumber, EpisodeNumber);

-- Speed up torrent hash lookups
CREATE INDEX IF NOT EXISTS idx_torrents_hash
ON torrentlibrary(Hash);
```

!!! warning "Index Overhead"
    Indexes speed up reads but slow down writes. Only add if experiencing performance issues.

---

## Related Documentation

- [Debug Logging](debug-logging.md) - Log analysis for database errors
- [Common Issues](common-issues.md) - General troubleshooting
- [Docker Guide](docker.md) - Docker-specific database issues
- [Path Mapping](path-mapping.md) - File access issues affecting database operations

---

## Quick Reference

### Common Commands

```bash
# Check database health
sqlite3 ~/config/torrentarr.db "PRAGMA quick_check;"

# View tables
sqlite3 ~/config/torrentarr.db ".tables"

# Checkpoint WAL
sqlite3 ~/config/torrentarr.db "PRAGMA wal_checkpoint(TRUNCATE);"

# Vacuum database
sqlite3 ~/config/torrentarr.db "VACUUM;"

# Backup database
cp ~/config/torrentarr.db ~/backups/torrentarr-$(date +%Y%m%d).db

# Reset all searches
sqlite3 ~/config/torrentarr.db "UPDATE moviesfilesmodel SET Searched = 0;"
```

### Emergency Recovery

```bash
# 1. Stop Torrentarr
docker stop torrentarr

# 2. Backup corrupted database
cp ~/config/torrentarr.db ~/config/torrentarr.db.corrupt

# 3. Attempt repair
sqlite3 ~/config/torrentarr.db ".dump" | sqlite3 ~/config/torrentarr.db.new
mv ~/config/torrentarr.db.new ~/config/torrentarr.db

# 4. Verify
sqlite3 ~/config/torrentarr.db "PRAGMA integrity_check;"

# 5. Restart
docker start torrentarr
```

If repair fails, delete database and rebuild:

```bash
rm ~/config/torrentarr.db
docker start torrentarr  # Rebuilds from Arr APIs
```

---

## Related Documentation

### Troubleshooting
- [Common Issues](common-issues.md) - General troubleshooting guide
- [Debug Logging](debug-logging.md) - Enable detailed logging for diagnosis
- [Docker Troubleshooting](docker.md) - Docker-specific issues
- [Path Mapping](path-mapping.md) - File access problems
- [Performance Tuning](performance.md) - Optimize Torrentarr performance

### Configuration
- [Configuration Guide](../configuration/index.md) - All configuration options
- [Environment Variables](../configuration/environment.md) - ENV var configuration

### Advanced
- [API Reference](../webui/api.md) - Direct database access via API
- [Development Guide](../development/index.md) - Database schema details

---

## Need Help?

If database issues persist after following this guide:

1. **Collect Information**:
   - Torrentarr version: `docker exec torrentarr torrentarr --version`
   - Database size: `ls -lh ~/config/*.db*`
   - Recent errors: `tail -n 100 ~/config/logs/Main.log`

2. **Try Emergency Recovery**:
   - Stop Torrentarr
   - Backup database
   - Delete database (forces rebuild)
   - Restart Torrentarr

3. **Get Support**:
   - [GitHub Issues](https://github.com/Drapersniper/Torrentarr/issues) - Report database bugs
   - [FAQ](../faq.md) - Check for known database issues
   - Provide: Torrentarr version, error messages, database size, storage type (local/NFS/SMB)
