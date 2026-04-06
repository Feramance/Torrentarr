# Installation

Choose the installation method that best fits your needs. Torrentarr offers multiple installation options for different environments and preferences.

## Installation Methods

### Docker (Recommended)

Docker provides the easiest and most consistent way to run Torrentarr across all platforms.

**Best for:**

- Users already running qBittorrent and Arr apps in Docker
- Simplified updates and maintenance
- Consistent environment across platforms
- Users who want easy path mapping

**Get Started:** [Docker Installation Guide →](docker.md)

```bash
docker run -d \
  --name torrentarr \
  -p 6969:6969 \
  -v /path/to/config:/config \
  feramance/torrentarr:latest
```

### Binary Download

Download pre-built executables for Linux, macOS, or Windows. Self-contained builds include the .NET runtime — no separate .NET install required.

**Best for:**

- Native installs without Docker
- Simple single-file deployment
- Systems where Docker isn't available
- Quick testing without building from source

**Get Started:** [Binary Installation Guide →](binary.md)

```bash
# Linux/macOS (x64 example)
curl -L -o torrentarr https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-linux-x64
chmod +x torrentarr
./torrentarr
```

### Systemd Service

Run Torrentarr as a system service on Linux (typically with a binary under `/usr/local/bin` or similar).

**Best for:**

- Production deployments on Linux servers
- Automatic startup on boot
- Integration with system logging
- Resource management and monitoring

**Get Started:** [Systemd Setup Guide →](systemd.md)

```bash
sudo systemctl enable --now torrentarr
```

### From source

Build and run from the repository when you are developing or need an unpublished build.

**Best for:**

- Contributors and local development
- Testing changes before a release

Use a .NET SDK matching the repo (see repository `global.json` / CI), then:

```bash
dotnet run --project src/Torrentarr.Host/Torrentarr.Host.csproj
```

See the [Development Guide](../../development/index.md) for the full workflow (restore, tests, publishing).

## Comparison

| Feature | Docker | Binary | Systemd | From source |
|---------|--------|--------|---------|-------------|
| **.NET SDK required** | No | No | No | Yes |
| **Easy updates** | ✅ (`docker pull`) | ⚠️ Manual or WebUI binary update | ⚠️ Same as binary | Rebuild |
| **Auto-start** | ✅ | ⚠️ Manual | ✅ | ⚠️ Manual |
| **Resource usage** | Medium | Low | Low | Low |
| **Isolation** | ✅ | ❌ | ⚠️ Partial | ❌ |
| **Path mapping** | ✅ Easy | ⚠️ Manual | ⚠️ Manual | ⚠️ Manual |
| **Multi-user** | ✅ | ❌ | ✅ | ❌ |

## Quick comparison

### Choose Docker if:

- ✅ You're already using Docker for qBittorrent/Arr apps
- ✅ You want simple updates (just `docker pull`)
- ✅ You need consistent environments across systems
- ✅ You want easy path mapping and permission management

### Choose Binary if:

- ✅ You want a native install without Docker
- ✅ You want a single downloadable executable
- ✅ You're testing Torrentarr quickly
- ✅ Docker isn't available

### Choose Systemd if:

- ✅ You're on Linux and want system integration
- ✅ You need automatic startup on boot
- ✅ You want centralized logging via journald
- ✅ You're running a production server

### Choose From source if:

- ✅ You are developing or patching Torrentarr
- ✅ You need a build that is not yet in releases

## Prerequisites

Regardless of installation method, you'll need:

1. **qBittorrent** - Running and accessible
   - v4.x or v5.x supported
   - WebUI enabled
   - Authentication configured

2. **Arr Instance** - At least one of:
   - Radarr (v3.x, v4.x, v5.x)
   - Sonarr (v3.x, v4.x)
   - Lidarr (v1.x, v2.x)

3. **Network Access** - Torrentarr needs to reach:
   - qBittorrent WebUI
   - Arr instance(s) API
   - Internet (for auto-updates, optional)

## After Installation

Once you've installed Torrentarr using your preferred method:

1. **First Run** - Generate default configuration
   - [First Run Guide →](../quickstart.md)

2. **Configure qBittorrent** - Set connection details
   - [qBittorrent Configuration →](../../configuration/qbittorrent.md)

3. **Configure Arr Instances** - Add your Radarr/Sonarr/Lidarr
   - [Arr Configuration →](../../configuration/arr/index.md)

4. **Set Up Categories & Tags** - Essential for tracking
   - [Category Configuration →](../../configuration/torrents.md)

5. **Verify Operation** - Check logs and WebUI
   - [WebUI Guide →](../../webui/index.md)

## Migration

Already running Torrentarr and want to switch installation methods?

- [Migration Guide →](../migration.md)

## Getting Help

Need assistance with installation?

- [Troubleshooting Guide →](../../troubleshooting/index.md)
- [FAQ →](../../faq.md)
- [GitHub Discussions](https://github.com/Feramance/Torrentarr/discussions)
- [Docker Issues →](../../troubleshooting/docker.md)

## Next Steps

Ready to install? Choose your method:

- 🐳 [Docker Installation →](docker.md) (Recommended)
- 📦 [Binary Installation →](binary.md)
- ⚙️ [Systemd Service →](systemd.md)

Or jump straight to:

- 🚀 [Quick Start Guide →](../quickstart.md) - Get running in 5 minutes!
