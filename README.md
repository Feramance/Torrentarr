# Torrentarr

[![Build Status](https://img.shields.io/badge/build-passing-brightgreen)]()
[![Feature Parity](https://img.shields.io/badge/feature_parity-99%25-blue)]()
[![License: MIT](https://img.shields.io/badge/license-MIT-green)]()
[![Documentation](https://img.shields.io/badge/docs-feramance.github.io%2FTorrentarr-blue)](https://feramance.github.io/Torrentarr/)
[![Docker Pulls](https://img.shields.io/docker/pulls/feramance/torrentarr.svg)](https://hub.docker.com/r/feramance/torrentarr)

> A high-performance C# port of [qBitrr](https://github.com/Feramance/qBitrr) — intelligent automation for qBittorrent and the *Arr ecosystem (Radarr, Sonarr, Lidarr). Same `config.toml` format and SQLite schema as qBitrr; database file is `torrentarr.db` (not `qbitrr.db`).

## Documentation

- **Full documentation:** [https://feramance.github.io/Torrentarr/](https://feramance.github.io/Torrentarr/)
- **Getting Started** – Installation guides for Docker and native setups
- **Configuration** – qBittorrent, Arr instances, quality profiles, and more
- **Features** – Health monitoring, automated search, quality management, disk space
- **WebUI** – Built-in React dashboard with live monitoring
- **Troubleshooting** – Common issues and debug logging

## Quick Start

### Run with Docker

```bash
docker run -d \
  --name torrentarr \
  -e TZ=America/New_York \
  -p 6969:6969 \
  -v /path/to/appdata/torrentarr:/config \
  -v /path/to/completed/downloads:/completed_downloads:rw \
  --restart unless-stopped \
  feramance/torrentarr:latest
```

**Docker Compose:**
```yaml
services:
  torrentarr:
    image: feramance/torrentarr:latest
    container_name: torrentarr
    restart: unless-stopped
    environment:
      TZ: America/New_York
    ports:
      - "6969:6969"
    volumes:
      - /path/to/appdata/torrentarr:/config
      - /path/to/completed/downloads:/completed_downloads:rw
```

### Native Installation

```bash
git clone https://github.com/Feramance/Torrentarr.git
cd Torrentarr

# Build (frontend is built into Host/wwwroot; not committed)
./build.sh       # Linux/macOS: builds React then .NET
# or: build.bat  # Windows
# Or manually: cd webui && npm run build && cd .. && dotnet restore && dotnet build

# Run (creates ~/config/config.toml on first run)
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

Access the WebUI at `http://<host>:6969/ui` after startup.

## Key Features

- **Multi-qBittorrent Support** – Manage torrents across multiple qBittorrent instances for load balancing, redundancy, and VPN isolation
- **Torrent Health Monitoring** – Detect stalled/failed downloads, auto-blacklist, trigger re-searches
- **Automated Search** – Missing media, quality upgrades, custom format scoring
- **Quality Management** – RSS sync, queue refresh, profile switching, custom format enforcement
- **Seeding Control** – Per-tracker settings, ratio/time limits, tracker injection
- **Hit and Run Protection** – Automatic HnR obligation tracking with configurable thresholds, partial download handling, and dead tracker bypass
- **Disk Space Management** – Auto-pause when low on space, configurable thresholds
- **Modern WebUI** – Live process monitoring, log viewer, Arr insights
- **WebUI authentication** – Optional login (local or OIDC), API token, and secure set-password
- **Config & schema compatible** – Same `config.toml` format as qBitrr; SQLite schema matches (same tables). Database file is `torrentarr.db` in the config directory, not `qbitrr.db`.
- **Process Isolation** – WebUI stays online even if a worker crashes

## Essential Configuration

1. **Configure qBittorrent** in `~/config/config.toml`:
   ```toml
   [qBit]
   Host = "localhost"
   Port = 8080
   UserName = "admin"
   Password = "adminpass"
   ```

2. **Add Arr instances**:
   ```toml
   [Radarr-Movies]
   URI = "http://localhost:7878"
   APIKey = "your-radarr-api-key"
   Category = "radarr-movies"
   ```

3. **Set completed folder**:
   ```toml
   [Settings]
   CompletedDownloadFolder = "/path/to/completed"
   ```

### Multi-qBittorrent

Manage torrents across multiple qBittorrent instances:

```toml
[qBit]  # Default instance (required)
Host = "localhost"
Port = 8080
UserName = "admin"
Password = "password"

[qBit-seedbox]  # Additional instance (optional)
Host = "192.168.1.100"
Port = 8080
UserName = "admin"
Password = "seedboxpass"
```

See [config.example.toml](https://github.com/Feramance/Torrentarr/blob/master/config.example.toml) for all available options.

## Architecture

```
Torrentarr.Host (orchestrator)
├── Hosts Torrentarr.WebUI (always online, port 6969)
├── Manages free space globally (across ALL qBit instances)
├── Handles special categories (failed, recheck)
└── Spawns per-Arr Worker processes
    ├── Radarr Worker (Torrentarr.Workers)
    ├── Sonarr Worker (Torrentarr.Workers)
    └── Lidarr Worker (Torrentarr.Workers)
```

## Resources

- **Torrentarr Documentation:** [https://feramance.github.io/Torrentarr/](https://feramance.github.io/Torrentarr/)
- **Example Config:** [config.example.toml](https://github.com/Feramance/Torrentarr/blob/master/config.example.toml)
- **Original qBitrr:** [Feramance/qBitrr](https://github.com/Feramance/qBitrr) — [qBitrr Documentation](https://feramance.github.io/qBitrr/)

## Development

```bash
# .NET backend
dotnet build
dotnet test --filter "Category!=Live"

# React frontend
cd webui
npm install
npm run dev     # Dev server at localhost:5173
npm run build   # Production bundle
```

## Comparison with qBitrr

| Feature | qBitrr (Python) | Torrentarr (C#) |
|---------|-----------------|-----------------|
| **Config Format** | TOML | TOML (same file) |
| **Database** | SQLite (Peewee) | SQLite (EF Core) |
| **Web Framework** | Flask | ASP.NET Core |
| **Performance** | Good | Excellent |
| **Memory Usage** | ~100MB | ~80MB |
| **Startup Time** | ~2s | ~0.5s |
| **Process Isolation** | Single process | Multi-process |
| **Health Checks** | Custom | Built-in |
| **Feature Parity** | 100% | **99%** |

## Issues & Support

- **Report Bugs:** [GitHub Issues](https://github.com/Feramance/Torrentarr/issues)
- **Discussions:** [GitHub Discussions](https://github.com/Feramance/Torrentarr/discussions)

## Contributing

Contributions welcome! Please:
1. Fork the repository
2. Create a feature branch
3. Make your changes
4. Add tests
5. Submit a pull request

## License

Released under the [MIT License](LICENSE).

## Credits

- Original [qBitrr](https://github.com/Feramance/qBitrr) by Drapersniper & Feramance

## Star History

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://api.star-history.com/svg?repos=Feramance/Torrentarr&type=Date&theme=dark" />
  <source media="(prefers-color-scheme: light)" srcset="https://api.star-history.com/svg?repos=Feramance/Torrentarr&type=Date" />
  <img alt="Star History Chart" src="https://api.star-history.com/svg?repos=Feramance/Torrentarr&type=Date" />
</picture>

---

<div align="center">

**Made with ❤️ as a C# port of qBitrr**

[GitHub](https://github.com/Feramance/Torrentarr) • [qBitrr](https://github.com/Feramance/qBitrr)

</div>
