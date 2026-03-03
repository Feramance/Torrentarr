# Troubleshooting

Having issues with Torrentarr? This section provides solutions to common problems and debugging techniques.

## Quick Links

- [Common Issues](common-issues.md) - Frequently encountered problems and solutions
- [Docker Issues](docker.md) - Docker-specific troubleshooting
- [Database Issues](database.md) - Database troubleshooting, corruption prevention, and recovery
- [FAQ](../faq.md) - Frequently asked questions

## Common Problems

### Connection Issues

Can't connect to qBittorrent or Arr instances?

**Quick Fixes:**
- Verify all services are running
- Check host/port configuration
- Verify API keys are correct
- Test connections from Torrentarr container/host

**Detailed Guide:** [Connection Problems](common-issues.md#connection-issues)

### Import Issues

Downloads complete but don't import to Arr?

**Quick Fixes:**
- Check path mappings
- Verify file permissions
- Enable instant import
- Check Arr logs

**Detailed Guide:** [Import Failures](common-issues.md#import-issues)

### Docker Networking

Problems with Docker container communication?

**Quick Fixes:**
- Use container names instead of localhost
- Check Docker network configuration
- Verify port mappings
- Test inter-container connectivity

**Detailed Guide:** [Docker Networking](docker.md#networking-issues)

### Stalled Torrents

Torrents stuck and not completing?

**Quick Fixes:**
- Check torrent health monitoring is enabled
- Verify tracker connectivity
- Check available disk space
- Review torrent client logs

**Detailed Guide:** [Stalled Torrents](common-issues.md#stalled-torrents-not-being-handled)

## Debugging Steps

### 1. Check Logs

Torrentarr logs provide detailed information about operations:

=== "Docker"
    ```bash
    # View logs
    docker logs torrentarr

    # Follow logs in real-time
    docker logs -f torrentarr

    # Save logs to file
    docker logs torrentarr > torrentarr.log 2>&1
    ```

=== "Systemd"
    ```bash
    # View logs
    journalctl -u torrentarr

    # Follow logs
    journalctl -u torrentarr -f

    # Recent logs
    journalctl -u torrentarr -n 100
    ```

=== "Native (dotnet/binary)"
    ```bash
    # Logs are in ~/config/logs/
    tail -f ~/config/logs/Main.log
    ```

### 2. Verify Configuration

Check your configuration file for errors:

```bash
# Native
cat ~/config/config.toml

# Docker
docker exec torrentarr cat /config/config.toml
```

Common configuration mistakes:
- Wrong API keys
- Incorrect host/port
- Missing required fields
- Typos in service names

### 3. Test Connectivity

Verify services are accessible:

```bash
# Test qBittorrent
curl http://localhost:8080

# Test Radarr
curl http://localhost:7878/api/v3/system/status \
  -H "X-Api-Key: your-api-key"

# Test Sonarr
curl http://localhost:8989/api/v3/system/status \
  -H "X-Api-Key: your-api-key"
```

### 4. Check Service Status

Ensure all required services are running:

=== "Docker"
    ```bash
    docker ps | grep -E "qbittorrent|radarr|sonarr|torrentarr"
    ```

=== "Systemd"
    ```bash
    systemctl status qbittorrent radarr sonarr torrentarr
    ```

### 5. Review Permissions

File permission issues can cause imports to fail:

```bash
# Check ownership
ls -la /path/to/downloads

# Check Torrentarr can read files
docker exec torrentarr ls -la /downloads
```

## Getting Help

### Before Asking for Help

1. **Check the documentation:**
   - [Common Issues](common-issues.md)
   - [Docker Troubleshooting](docker.md)
   - [FAQ](../faq.md)

2. **Gather information:**
   - Torrentarr version
   - Installation method (Docker/dotnet/binary)
   - Arr versions
   - Relevant log excerpts
   - Configuration (redact API keys!)

3. **Search existing issues:**
   - [GitHub Issues](https://github.com/Feramance/Torrentarr/issues)
   - [Discussions](https://github.com/Feramance/Torrentarr/discussions)

### Where to Get Help

- **GitHub Issues** - For bugs and feature requests
- **GitHub Discussions** - For questions and community support
- **Discord** - Real-time help from the community

### Creating a Good Issue Report

Include:

1. **Environment:**
   ```
   Torrentarr version: 5.5.5
   Installation: Docker
   OS: Ubuntu 22.04
   qBittorrent: 4.6.0
   Radarr: 5.0.0
   ```

2. **Problem Description:**
   - What you expected to happen
   - What actually happened
   - Steps to reproduce

3. **Relevant Logs:**
   ```
   [Include relevant log excerpts]
   ```

4. **Configuration:**
   ```toml
   # Redact sensitive information!
   [qBit]
   Host = "http://qbittorrent"
   Port = 8080
   Username = "admin"
   Password = "REDACTED"
   ```

## Debug Mode

Enable debug logging for more detailed information:

```toml
[Settings]
LogLevel = "DEBUG"
```

**Warning:** Debug logs can be very verbose and may impact performance. Only enable when troubleshooting.

---

## Performance Issues

### High CPU Usage

**Symptoms:** Torrentarr consuming excessive CPU

**Common Causes:**

1. **Too frequent torrent checks:**
   ```toml
   [Settings]
   CheckInterval = 300  # Increase interval (default: 60)
   ```

2. **Too many concurrent health checks:**
   ```toml
   [Settings]
   MaxConcurrentChecks = 5  # Reduce concurrent operations
   ```

3. **Large torrent counts:**
   - Limit categories to only what Torrentarr needs to manage
   - Use category filtering in qBittorrent

4. **FFprobe validation on large files:**
   ```toml
   [Settings]
   FFprobeAutoUpdate = false  # Temporarily disable to test
   ```

**Monitor Resource Usage:**

```bash
# Docker
docker stats torrentarr --no-stream

# Native
ps aux | grep torrentarr
top -p $(pidof torrentarr)
```

---

### High Memory Usage

**Symptoms:** Torrentarr using excessive RAM

**Solutions:**

1. **Reduce log retention:**
   ```toml
   [Settings]
   LogRetentionDays = 7  # Reduce from default 30
   ```

2. **Limit torrent history:**
   ```toml
   [Settings]
   TorrentHistoryDays = 30  # Clean old entries
   ```

3. **Vacuum database regularly:**
   ```bash
   sqlite3 ~/config/Torrentarr.db "VACUUM;"
   ```

4. **Restart Torrentarr periodically:**
   - Set up a cron job or systemd timer for weekly restarts

---

### Slow WebUI Response

**Symptoms:** WebUI is slow or unresponsive

**Solutions:**

1. **Reduce data fetching:**
   - Limit log entries displayed (50-100 instead of 500)
   - Use filters to reduce table sizes
   - Disable auto-refresh when not actively monitoring

2. **Check API response times:**
   ```bash
   time curl http://localhost:6969/api/health
   ```

3. **Database optimization:**
   ```bash
   # Check database size
   ls -lh ~/config/Torrentarr.db

   # If > 100MB, vacuum
   sqlite3 ~/config/Torrentarr.db "VACUUM;"
   ```

4. **Browser performance:**
   - Clear browser cache
   - Disable unnecessary browser extensions
   - Try a different browser

---

### Slow Import Processing

**Symptoms:** Torrents complete but imports take a long time

**Solutions:**

1. **Enable instant imports:**
   ```toml
   [[Radarr]]
   InstantImport = true  # Trigger immediate imports
   ```

2. **Optimize Arr refresh intervals:**
   ```toml
   [[Radarr]]
   RefreshDownloadsTimer = 1  # Check every 1 minute
   ```

3. **Check Arr instance performance:**
   - Review Arr logs for slow operations
   - Optimize Arr database
   - Reduce Arr's own refresh intervals

4. **Path mapping issues:**
   - Verify paths are consistent across all services
   - See [Path Mapping Guide](path-mapping.md)

---

## Network Issues

### Timeout Errors

**Symptoms:** "Connection timeout" errors in logs

**Solutions:**

1. **Increase timeout values:**
   ```toml
   [Settings]
   ConnectionTimeout = 60  # Increase from default 30
   ReadTimeout = 120       # Increase from default 60
   ```

2. **Check network connectivity:**
   ```bash
   # Test from Torrentarr container/host
   ping qbittorrent
   ping radarr
   curl -v http://qbittorrent:8080
   ```

3. **Verify services are responding:**
   ```bash
   # Check service health
   docker exec torrentarr curl http://qbittorrent:8080/api/v2/app/version
   ```

4. **Review firewall rules:**
   ```bash
   # Ubuntu/Debian
   sudo ufw status

   # Check if ports are open
   sudo netstat -tulpn | grep 8080
   ```

---

### DNS Resolution Issues

**Symptoms:** "Name or service not known" errors

**Solutions:**

1. **Use IP addresses instead of hostnames:**
   ```toml
   [qBit]
   Host = "http://192.168.1.100"  # Instead of hostname
   ```

2. **Check DNS configuration (Docker):**
   ```yaml
   services:
     torrentarr:
       dns:
         - 8.8.8.8
         - 1.1.1.1
   ```

3. **Verify container can resolve names:**
   ```bash
   docker exec torrentarr nslookup radarr
   docker exec torrentarr ping -c 3 radarr
   ```

4. **Use Docker network aliases:**
   ```yaml
   services:
     radarr:
       networks:
         mediastack:
           aliases:
             - radarr
   ```

---

### Connection Refused

**Symptoms:** "Connection refused" when trying to reach services

**Solutions:**

1. **Verify service is listening:**
   ```bash
   docker ps | grep radarr  # Check if running
   netstat -tulpn | grep 7878  # Check if listening
   ```

2. **Check bind address:**
   - Services should bind to `0.0.0.0` not `127.0.0.1` for Docker

3. **Verify port mappings (Docker):**
   ```bash
   docker port torrentarr
   docker port radarr
   ```

4. **Test from correct network namespace:**
   ```bash
   # From within Torrentarr container
   docker exec torrentarr curl http://radarr:7878
   ```

---

## Configuration Issues

### Invalid TOML Syntax

**Symptoms:** Torrentarr won't start, "Invalid configuration" error

**Common Mistakes:**

1. **Missing quotes:**
   ```toml
   # ❌ Wrong
   Host = http://localhost

   # ✅ Correct
   Host = "http://localhost"
   ```

2. **Wrong quotes:**
   ```toml
   # ❌ Wrong (single quotes don't work for all values)
   APIKey = 'abc123'

   # ✅ Correct
   APIKey = "abc123"
   ```

3. **Unclosed brackets:**
   ```toml
   # ❌ Wrong
   [[Radarr]
   Name = "Movies"

   # ✅ Correct
   [[Radarr]]
   Name = "Movies"
   ```

4. **Duplicate sections:**
   ```toml
   # ❌ Wrong (duplicate [Settings])
   [Settings]
   LogLevel = "INFO"

   [Settings]
   CheckInterval = 60

   # ✅ Correct
   [Settings]
   LogLevel = "INFO"
   CheckInterval = 60
   ```

**Validate TOML:**

```bash
# .NET / runtime validation
python3 -c "import toml; toml.load(open('config.toml'))"

# Online validator
# Copy config to https://www.toml-lint.com/
```

---

### Missing Required Fields

**Symptoms:** "Config key 'X' requires a value" error

**Solutions:**

Check these required fields are set:

```toml
[qBit]
Host = "http://localhost:8080"  # REQUIRED
# Username and Password (if qBit has auth enabled)

[[Radarr]]
URI = "http://localhost:7878"   # REQUIRED
APIKey = "your-api-key"         # REQUIRED
Category = "radarr"             # REQUIRED (must match qBit download client)
```

---

### Environment Variable Overrides Not Working

**Symptoms:** Config path environment variable not applied

**Solutions:**

1. **Use the correct variable:** Torrentarr only reads `TORRENTARR_CONFIG` (path to config.toml). It does not support per-setting env overrides.
   ```bash
   export TORRENTARR_CONFIG=/path/to/config/config.toml
   ```

2. **Verify variable is passed to container:**
   ```bash
   docker exec torrentarr env | grep TORRENTARR_CONFIG
   ```

3. **In Docker Compose:**
   ```yaml
   environment:
     TORRENTARR_CONFIG: /config/config.toml
   ```

---

## Automated Search Issues

### Searches Not Triggering

**Symptoms:** Torrentarr doesn't search for missing content

**Solutions:**

1. **Enable search functionality:**
   ```toml
   [[Radarr]]
   [Radarr.EntrySearch]
   SearchMissing = true
   ```

2. **Check search intervals:**
   ```toml
   SearchRequestsEvery = 300  # Search every 5 minutes
   ```

3. **Verify Arr has indexers:**
   - Radarr/Sonarr/Lidarr must have working indexers configured
   - Test indexers in Arr UI

4. **Review search logs:**
   ```bash
   grep -i "search" ~/config/logs/Radarr-Movies.log
   ```

5. **Check for rate limiting:**
   - Some indexers rate limit searches
   - Review indexer API limits

---

### Too Many Searches

**Symptoms:** Overwhelming indexers with searches, rate limited

**Solutions:**

1. **Increase search interval:**
   ```toml
   SearchRequestsEvery = 600  # 10 minutes instead of 5
   ```

2. **Reduce search limit:**
   ```toml
   SearchLimit = 3  # Fewer concurrent searches
   ```

3. **Disable upgrade searches temporarily:**
   ```toml
   DoUpgradeSearch = false
   QualityUnmetSearch = false
   ```

4. **Monitor indexer stats:**
   - Check Prowlarr for indexer statistics
   - Review API hit counts

---

## Seeding & Tracker Issues

### Torrents Removed Too Early

**Symptoms:** Torrents deleted before reaching seeding goals

**Solutions:**

1. **Adjust seeding limits:**
   ```toml
   [[Radarr]]
   [Radarr.Torrent.SeedingMode]
   MaxUploadRatio = 2.0      # Increase ratio
   MaxSeedingTime = 1209600  # 14 days in seconds
   RemoveTorrent = 4         # Remove only when BOTH met
   ```

2. **Check tracker-specific rules:**
   ```toml
   [[Radarr.Torrent.Trackers]]
   Name = "PrivateTracker"
   URI = "https://tracker.example.com"
   MaxUploadRatio = 3.0      # Higher for private trackers
   ```

3. **Disable automatic removal:**
   ```toml
   RemoveTorrent = -1  # Never auto-remove
   ```

---

### Dead Tracker Detection Issues

**Symptoms:** Torrents marked as failed due to temporary tracker issues

**Solutions:**

1. **Increase stall delay:**
   ```toml
   StalledDelay = 30  # 30 minutes before considering stalled
   ```

2. **Disable tracker error removal:**
   ```toml
   RemoveDeadTrackers = false
   ```

3. **Customize tracker error messages:**
   ```toml
   RemoveTrackerWithMessage = [
     # Only remove on these specific errors
     "info hash is not authorized with this tracker"
   ]
   ```

---

## Logging Issues

### Too Many Logs

**Symptoms:** Log files growing too large

**Solutions:**

1. **Reduce log level:**
   ```toml
   [Settings]
   LogLevel = "INFO"  # Change from DEBUG
   ```

2. **Enable log rotation:**
   ```toml
   LogRotation = true
   LogMaxSize = 10485760    # 10 MB
   LogBackupCount = 5
   ```

3. **Reduce retention:**
   ```toml
   LogRetentionDays = 7  # Keep only 1 week
   ```

---

### Can't Find Logs

**Symptoms:** Don't know where logs are located

**Log Locations:**

=== "Docker"
    ```bash
    # Console logs
    docker logs torrentarr

    # File logs (if volume mounted)
    ls -la /path/to/config/logs/
    ```

=== "Native (dotnet/binary)"
    ```
    ~/config/logs/
    ├── Main.log
    ├── WebUI.log
    └── Radarr-Movies.log
    ```

=== "Systemd"
    ```bash
    # Journald logs
    journalctl -u torrentarr -f

    # File logs
    ls -la /home/torrentarr/config/logs/
    ```

---

## Update & Upgrade Issues

### Update Fails

**Symptoms:** Auto-update or manual update fails

**Solutions:**

1. **Check for sufficient disk space:**
   ```bash
   df -h /path/to/torrentarr
   ```

2. **Verify permissions:**
   ```bash
   # For dotnet tool
   dotnet tool update -g torrentarr

   # For Docker
   docker pull feramance/torrentarr:latest
   ```

3. **Check for breaking changes:**
   - Review [CHANGELOG.md](../changelog.md)
   - Backup config before upgrading

4. **Manual update:**
   ```bash
   # dotnet tool
   dotnet tool update -g torrentarr

   # Docker
   docker-compose pull torrentarr
   docker-compose up -d torrentarr
   ```

---

### Version Mismatch Issues

**Symptoms:** Errors after update, incompatible features

**Solutions:**

1. **Check version:**
   ```bash
   torrentarr --version  # native
   docker exec torrentarr torrentarr --version  # Docker
   ```

2. **Review config migrations:**
   - Torrentarr auto-migrates config on version change
   - Check logs for migration messages

3. **Rollback if needed:**
   ```bash
   # dotnet tool
   dotnet tool install -g torrentarr --version 5.4.0  # Specific version

   # Docker
   docker pull feramance/torrentarr:5.4.0
   ```

## Clean Restart

Sometimes a clean restart resolves issues:

=== "Docker"
    ```bash
    docker stop torrentarr
    docker rm torrentarr
    # Then recreate with your docker run command
    ```

=== "Systemd"
    ```bash
    sudo systemctl restart torrentarr
    ```

=== "Native (dotnet/binary)"
    ```bash
    # Stop Torrentarr (Ctrl+C if running in foreground)
    # Then restart
    torrentarr
    ```

## Database Issues

If the database becomes corrupt:

1. **Backup existing database:**
   ```bash
   cp ~/config/Torrentarr.db ~/config/Torrentarr.db.backup
   ```

2. **Let Torrentarr recreate:**
   ```bash
   rm ~/config/Torrentarr.db
   # Restart Torrentarr - it will create a new database
   ```

3. **Recovery:** Torrentarr has automatic database recovery. Check logs for recovery messages.

## Still Need Help?

If you've tried the troubleshooting steps and still have issues:

1. Review [Common Issues](common-issues.md) for detailed solutions
2. Check [Docker-specific issues](docker.md) if using Docker
3. Search [GitHub Issues](https://github.com/Feramance/Torrentarr/issues)
4. Ask in [GitHub Discussions](https://github.com/Feramance/Torrentarr/discussions)

We're here to help! 🚀
