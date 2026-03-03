# Testing

Torrentarr testing strategies and guidelines. The project uses xUnit (backend) and Vitest (WebUI) for automated tests, plus manual testing against real services where needed.

## Current Testing Approach

### Manual Testing

Torrentarr uses manual testing against real services:

**Requirements:**
- qBittorrent instance (v4.3+ or v5.0+)
- At least one Arr instance (Radarr, Sonarr, or Lidarr)
- Test torrents with various states
- Test media files for FFprobe validation

**Test Environment Setup:**

```bash
# 1. Set up test config
cp config.example.toml test-config.toml

# Edit test-config.toml with test service URLs

# 2. Run Torrentarr with test config
TORRENTARR_CONFIG=/path/to/test-config.toml dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

### Testing Checklist

When making changes, test these scenarios:

#### Core Functionality
- [ ] Torrentarr starts successfully
- [ ] Connects to qBittorrent
- [ ] Connects to all configured Arr instances
- [ ] WebUI accessible at configured port
- [ ] Logs written to correct location

#### Torrent Processing
- [ ] Detects new torrents added by Arr
- [ ] Tracks torrent download progress
- [ ] Detects torrent completion
- [ ] Triggers import to Arr
- [ ] Updates torrent state in database

#### Health Monitoring
- [ ] Detects stalled torrents
- [ ] Marks torrents with ETA > MaxETA as stalled
- [ ] Handles failed trackers
- [ ] FFprobe validation (if enabled)
- [ ] Blacklists failed torrents

#### Seeding Management
- [ ] Continues seeding after import
- [ ] Tracks seed ratio and time
- [ ] Deletes torrents when seed goals met
- [ ] Respects tracker-specific rules (if configured)

#### Search Features
- [ ] Auto-search for missing content (if enabled)
- [ ] Re-search after blacklisting (if enabled)
- [ ] Search cooldown works correctly
- [ ] Search history recorded in database

#### Configuration
- [ ] Config file changes detected
- [ ] Environment variables: only `TORRENTARR_CONFIG` is supported (config path)
- [ ] Invalid config generates helpful errors
- [ ] Config validation works (see logs on load)

#### WebUI
- [ ] Dashboard loads correctly
- [ ] Processes page shows all Arr instances
- [ ] Logs page displays recent logs
- [ ] Arr-specific pages show torrents
- [ ] API endpoints return correct data
- [ ] API authentication works (if token set)

### Docker Testing

```bash
# Build test image
docker build -t torrentarr:test .

# Run with test config
docker run -d \
  --name torrentarr-test \
  -p 6969:6969 \
  -v $(pwd)/test-config.toml:/config/config.toml \
  -v /path/to/downloads:/downloads \
  torrentarr:test

# Check logs
docker logs -f torrentarr-test

# Clean up
docker stop torrentarr-test
docker rm torrentarr-test
```

## Automated Testing

Torrentarr uses **xUnit** for .NET tests and **Vitest** for the WebUI.

### .NET tests

```bash
# All non-live tests (suitable for CI)
dotnet test --filter "Category!=Live"

# Single project
dotnet test tests/Torrentarr.Infrastructure.Tests/

# Single test
dotnet test --filter "FullyQualifiedName~ConfigurationLoaderTests"
```

### Live integration tests

```bash
# Requires real qBittorrent and Arr configured in config.toml
dotnet test --filter "Category=Live"
```

### WebUI tests

```bash
cd webui
npm test          # Watch mode
npx vitest run    # Single run (CI)
```

See the repository `tests/` folder and `webui/src/__tests__/` for examples. Mock external APIs in unit tests; use `TorrentarrWebApplicationFactory` in Host.Tests for API integration tests.

## Manual Test Scenarios

### Scenario 1: Failed Download

**Setup:**
1. Add movie to Radarr
2. Radarr grabs torrent with no seeders

**Expected Behavior:**
1. Torrentarr detects torrent
2. ETA exceeds MaximumETA after StallTimeout
3. Torrent marked as stalled
4. Torrent blacklisted in Radarr
5. New search triggered (if AutoReSearch enabled)

### Scenario 2: Successful Import

**Setup:**
1. Add movie to Radarr
2. Radarr grabs popular torrent

**Expected Behavior:**
1. Torrentarr tracks download progress
2. Download completes
3. FFprobe validates file (if enabled)
4. Import triggered in Radarr
5. Torrent continues seeding
6. Deleted when seed goals met

### Scenario 3: Configuration Change

**Setup:**
1. Torrentarr running
2. Edit config.toml (change CheckInterval)

**Expected Behavior:**
1. Torrentarr detects config change
2. Reloads configuration
3. Event loops restart with new interval
4. No data loss in database

## Related Documentation

- [Development Guide](index.md) - Complete development setup
- [Contributing](contributing.md) - Contribution guidelines
- [Code Style](code-style.md) - Code formatting rules
