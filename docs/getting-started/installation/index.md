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

### dotnet tool

Install Torrentarr as a global .NET tool — the simplest native installation method.

**Best for:**

- Users with .NET 8.0+ already installed
- Native performance requirements
- Easy updates with a single command
- Developers and power users

**Get Started:** [dotnet tool Installation Guide →](dotnet.md)

```bash
dotnet tool install -g torrentarr
torrentarr
```

### Binary Download

Download pre-built executables for Linux, macOS, or Windows. No Python required!

**Best for:**

- Users who don't have Python installed
- Simple single-file deployment
- Systems where Docker isn't available
- Quick testing without dependencies

**Get Started:** [Binary Installation Guide →](binary.md)

```bash
# Linux/macOS
curl -L -o torrentarr https://github.com/Feramance/Torrentarr/releases/latest/download/torrentarr-linux-x64
chmod +x torrentarr
./torrentarr
```

### Systemd Service

Run Torrentarr as a system service on Linux.

**Best for:**

- Production deployments on Linux servers
- Automatic startup on boot
- Integration with system logging
- Resource management and monitoring

**Get Started:** [Systemd Setup Guide →](systemd.md)

```bash
sudo systemctl enable --now torrentarr
```

## Comparison

| Feature | Docker | dotnet tool | Binary | Systemd |
|---------|--------|-------------|--------|---------|
| **.NET Required** | No | Yes (8.0+) | No | Yes |
| **Easy Updates** | ✅ | ✅ | ⚠️ Manual | ✅ |
| **Auto-start** | ✅ | ⚠️ Manual | ⚠️ Manual | ✅ |
| **Resource Usage** | Medium | Low | Low | Low |
| **Isolation** | ✅ | ❌ | ❌ | ⚠️ Partial |
| **Path Mapping** | ✅ Easy | ⚠️ Complex | ⚠️ Complex | ⚠️ Complex |
| **Multi-user** | ✅ | ❌ | ❌ | ✅ |

## Quick Comparison

### Choose Docker if:
- ✅ You're already using Docker for qBittorrent/Arr apps
- ✅ You want simple updates (just `docker pull`)
- ✅ You need consistent environments across systems
- ✅ You want easy path mapping and permission management

### Choose dotnet tool if:
- ✅ You already have .NET 8.0+ installed
- ✅ You want native performance
- ✅ You prefer simple `dotnet tool update` upgrades
- ✅ You need development flexibility

### Choose Binary if:
- ✅ You don't have .NET and prefer not to install it
- ✅ You want a single executable file
- ✅ You're testing Torrentarr quickly
- ✅ Docker isn't available

### Choose Systemd if:
- ✅ You're on Linux and want system integration
- ✅ You need automatic startup on boot
- ✅ You want centralized logging via journald
- ✅ You're running a production server

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
- ⚙️ [dotnet tool Installation →](dotnet.md)
- 📦 [Binary Installation →](binary.md)
- ⚙️ [Systemd Service →](systemd.md)

Or jump straight to:

- 🚀 [Quick Start Guide →](../quickstart.md) - Get running in 5 minutes!
