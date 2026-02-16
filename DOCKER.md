# Docker Deployment Guide

This guide covers deploying Commandarr using Docker and Docker Compose.

## Quick Start

### Using Docker Compose (Recommended)

The easiest way to get started is with Docker Compose, which includes all necessary services:

```bash
# 1. Clone the repository
git clone https://github.com/yourusername/commandarr.git
cd commandarr

# 2. Create config directory
mkdir -p config

# 3. Copy example config
cp config.example.toml config/config.toml

# 4. Edit configuration (see Configuration section below)
nano config/config.toml

# 5. Start all services
docker-compose up -d

# 6. Check logs
docker-compose logs -f commandarr
```

Access the WebUI at: http://localhost:6969

## Docker Compose Services

The provided `docker-compose.yml` includes:

- **commandarr** - Main Commandarr application (port 6969)
- **qbittorrent** - qBittorrent torrent client (port 8080)
- **radarr** - Movie management (port 7878)
- **sonarr** - TV show management (port 8989)
- **lidarr** - Music management (port 8686, optional)

### Service URLs

Once started, access services at:

- Commandarr WebUI: http://localhost:6969
- qBittorrent WebUI: http://localhost:8080 (default: admin/adminadmin)
- Radarr: http://localhost:7878
- Sonarr: http://localhost:8989
- Lidarr: http://localhost:8686

## Configuration

### Docker Environment Variables

Set these in your `docker-compose.yml` or when running standalone:

```yaml
environment:
  - TZ=America/New_York          # Timezone
  - PUID=1000                     # User ID
  - PGID=1000                     # Group ID
  - ASPNETCORE_ENVIRONMENT=Production
```

### Volumes

The following volumes are used:

```yaml
volumes:
  - ./config:/config              # Configuration files
  - ./data:/data                  # Application data
  - downloads:/downloads          # Shared download directory
```

### Configuration File

Create `config/config.toml` based on `config.example.toml`:

```toml
[Settings]
ConfigVersion = "5.8.8"
ConsoleLevel = "INFO"
LoopSleepTimer = 5

[WebUI]
Host = "0.0.0.0"
Port = 6969
Token = "your-secure-token-here"

[qBit]
Host = "qbittorrent"              # Use container name
Port = 8080
UserName = "admin"
Password = "adminadmin"
Disabled = false

[radarr]
URI = "http://radarr:7878"        # Use container name
APIKey = "your-radarr-api-key"
Category = "radarr-movies"
Type = "radarr"
Managed = true

[sonarr]
URI = "http://sonarr:8989"        # Use container name
APIKey = "your-sonarr-api-key"
Category = "sonarr-tv"
Type = "sonarr"
Managed = true
```

**Important:** Use Docker container names (`qbittorrent`, `radarr`, `sonarr`) instead of `localhost` when services communicate within the Docker network.

## Building the Image

### Build Locally

```bash
# Build the image
docker build -t commandarr:latest .

# Or with specific version
docker build -t commandarr:1.0.0 .
```

### Multi-Architecture Build

Build for multiple platforms (requires buildx):

```bash
docker buildx create --use
docker buildx build \
  --platform linux/amd64,linux/arm64 \
  -t commandarr:latest \
  --push \
  .
```

## Running Standalone Container

Run Commandarr without Docker Compose:

```bash
docker run -d \
  --name commandarr \
  -p 6969:6969 \
  -v $(pwd)/config:/config \
  -v $(pwd)/data:/data \
  -e TZ=America/New_York \
  -e PUID=1000 \
  -e PGID=1000 \
  --restart unless-stopped \
  commandarr:latest
```

## Docker Compose Commands

### Start Services

```bash
# Start all services
docker-compose up -d

# Start including optional services (lidarr)
docker-compose --profile full up -d

# Start specific service
docker-compose up -d commandarr
```

### Stop Services

```bash
# Stop all services
docker-compose down

# Stop and remove volumes
docker-compose down -v
```

### View Logs

```bash
# All services
docker-compose logs -f

# Specific service
docker-compose logs -f commandarr

# Last 100 lines
docker-compose logs --tail=100 commandarr
```

### Restart Services

```bash
# Restart all
docker-compose restart

# Restart specific service
docker-compose restart commandarr
```

### Update Services

```bash
# Pull latest images
docker-compose pull

# Rebuild and restart
docker-compose up -d --build
```

## Health Checks

Commandarr includes built-in health checks:

```bash
# Check container health
docker inspect commandarr | grep -A 10 Health

# Manual health check
curl http://localhost:6969/health
```

Expected response:
```json
{
  "status": "Healthy",
  "timestamp": "2024-01-01T00:00:00Z"
}
```

## Troubleshooting

### Container Won't Start

Check logs:
```bash
docker-compose logs commandarr
```

Common issues:
- Missing or invalid `config.toml`
- Port conflicts (6969 already in use)
- Permission issues with volumes

### Can't Connect to qBittorrent/Arr

1. Verify services are running:
   ```bash
   docker-compose ps
   ```

2. Check network connectivity:
   ```bash
   docker exec commandarr ping qbittorrent
   ```

3. Verify container names in config.toml match service names

### Database Issues

Reset database:
```bash
docker-compose down
rm -rf config/qbitrr.db*
docker-compose up -d
```

### Permission Issues

Ensure volumes have correct permissions:
```bash
sudo chown -R 1000:1000 config data
```

## Backup and Restore

### Backup

```bash
# Backup configuration and database
tar -czf commandarr-backup-$(date +%Y%m%d).tar.gz config/ data/

# Or use docker-compose
docker-compose down
tar -czf backup.tar.gz config/ data/
docker-compose up -d
```

### Restore

```bash
# Extract backup
tar -xzf commandarr-backup-YYYYMMDD.tar.gz

# Start services
docker-compose up -d
```

## Security Considerations

1. **Change Default Passwords**
   - qBittorrent: admin/adminadmin → Change immediately
   - Set strong WebUI token in config.toml

2. **Use Secrets for API Keys**
   - Consider Docker secrets for production
   - Don't commit config.toml with real credentials

3. **Network Isolation**
   - Services communicate via internal Docker network
   - Only expose necessary ports to host

4. **Regular Updates**
   ```bash
   docker-compose pull
   docker-compose up -d
   ```

## Production Deployment

### Using Docker Secrets

```yaml
# docker-compose.yml
secrets:
  qbit_password:
    file: ./secrets/qbit_password.txt
  radarr_api_key:
    file: ./secrets/radarr_api_key.txt

services:
  commandarr:
    secrets:
      - qbit_password
      - radarr_api_key
```

### Reverse Proxy (Nginx)

```nginx
server {
    listen 80;
    server_name commandarr.yourdomain.com;

    location / {
        proxy_pass http://localhost:6969;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
```

### Resource Limits

```yaml
services:
  commandarr:
    deploy:
      resources:
        limits:
          cpus: '2'
          memory: 1G
        reservations:
          cpus: '0.5'
          memory: 256M
```

## Monitoring

### Prometheus Metrics (Future)

```yaml
# Add to docker-compose.yml
  prometheus:
    image: prom/prometheus
    volumes:
      - ./prometheus.yml:/etc/prometheus/prometheus.yml
    ports:
      - "9090:9090"
```

### Health Check Monitoring

Use the `/health` endpoint for uptime monitoring:
- UptimeRobot
- Healthchecks.io
- Custom monitoring scripts

## Migration from qBitrr

1. **Stop qBitrr**
   ```bash
   # If running as service
   sudo systemctl stop qbitrr
   ```

2. **Copy Database**
   ```bash
   cp ~/.config/qbitrr/qbitrr.db ./config/
   ```

3. **Copy Config**
   ```bash
   cp ~/.config/qbitrr/config.toml ./config/
   ```

4. **Start Commandarr**
   ```bash
   docker-compose up -d
   ```

5. **Verify**
   - Check logs: `docker-compose logs -f commandarr`
   - Access WebUI: http://localhost:6969
   - Verify torrents are being processed

## Advanced Configuration

### Custom Network

```yaml
networks:
  commandarr-network:
    driver: bridge
    ipam:
      config:
        - subnet: 172.28.0.0/16
```

### Host Network Mode

For better performance (Linux only):

```yaml
services:
  commandarr:
    network_mode: host
```

### Named Volumes

Use named volumes for better portability:

```yaml
volumes:
  commandarr-config:
    driver: local
    driver_opts:
      type: none
      device: /path/to/config
      o: bind
```

## Support

- GitHub Issues: https://github.com/yourusername/commandarr/issues
- Docker Hub: https://hub.docker.com/r/yourusername/commandarr
- Wiki: https://github.com/yourusername/commandarr/wiki

---

**Note:** This Docker setup provides a complete media automation stack. Adjust services and configuration based on your specific needs.
