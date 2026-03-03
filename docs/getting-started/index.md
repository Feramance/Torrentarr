# Getting Started with Torrentarr

Welcome! This comprehensive guide will help you install and configure Torrentarr for the first time.

---

## What is Torrentarr?

Torrentarr is an intelligent automation tool that bridges qBittorrent and the Arr ecosystem (Radarr/Sonarr/Lidarr). It provides:

- **Intelligent Health Monitoring** - Automatically detect and handle failed/stalled downloads
- **Instant Imports** - Import media to your library as soon as downloads complete
- **Automated Search** - Continuously search for missing media and quality upgrades
- **Request Integration** - Process Overseerr/Ombi requests automatically
- **Custom Format Enforcement** - Ensure downloads meet your quality standards
- **Seeding Management** - Per-tracker seeding rules and automatic cleanup
- **Web Interface** - Modern React UI for monitoring and configuration

---

## Prerequisites

### Required Components

Before installing Torrentarr, ensure you have these components running:

#### 1. qBittorrent

- **Version:** 4.1.0+ or 5.x
- **WebUI:** Must be enabled (Settings → Web UI)
- **Authentication:** Note your username and password
- **Network:** Accessible from where Torrentarr will run

**Verify qBittorrent:**

```bash
# Test WebUI access
curl http://localhost:8080
# Should return login page
```

#### 2. At Least One Arr Instance

Install and configure at least one of:

- **Radarr** (v3.0+) - Movie management
- **Sonarr** (v3.0+) - TV show management
- **Lidarr** (v1.0+) - Music management

**Requirements for each Arr instance:**

- ✅ Configured indexers (Prowlarr or direct)
- ✅ Download client pointing to qBittorrent
- ✅ Category set in download client (e.g., `radarr-movies`)
- ✅ API key available (Settings → General → Security)

**Verify Arr instance:**

```bash
# Test Radarr API
curl -H "X-Api-Key: YOUR_API_KEY" http://localhost:7878/api/v3/system/status
# Should return JSON with version info
```

#### 3. Storage & Permissions

- **Download folder:** Accessible by qBittorrent, Arr instances, and Torrentarr
- **Config folder:** Write permissions for Torrentarr
- **Logs folder:** Write permissions for Torrentarr

### Optional Components

#### Overseerr or Ombi

For automated request processing:

- **Overseerr** (v1.26+) - Modern request management
- **Ombi** (v4.0+) - Alternative request management

#### FFprobe

For media file validation:

- Automatically downloaded by Torrentarr (recommended)
- Or manually installed (`apt install ffmpeg`)

---

## Installation Methods

Choose the installation method that best fits your infrastructure:

### 🐳 Docker (Recommended)

**Best for:**

- Most users
- Easy updates
- Isolated environment
- Cross-platform support

**Advantages:**

- ✅ Pre-configured environment
- ✅ Automatic dependency management
- ✅ Easy rollback and updates
- ✅ Consistent across all platforms

**Requirements:**

- Docker 20.10+
- Docker Compose 2.0+ (optional but recommended)

[**Docker Installation Guide →**](installation/docker.md)

---

### 📦 .NET tool

**Best for:**

- Native Linux/macOS/Windows installations
- Users who prefer not to use Docker
- Integration with existing .NET tooling

**Advantages:**

- ✅ Native performance
- ✅ Single global tool command
- ✅ Full control over environment

**Requirements:**

- .NET 8.0 SDK or runtime
- qBittorrent and at least one Arr instance

[**.NET tool Installation Guide →**](installation/dotnet.md)

---

### 🔧 Systemd Service

**Best for:**

- Linux servers
- Production deployments
- Automatic startup on boot
- Native system integration

**Advantages:**

- ✅ Native Linux service
- ✅ Automatic restart on failure
- ✅ Logging via journalctl
- ✅ Resource limits and sandboxing

**Requirements:**

- Linux with systemd
- .NET 8+ or Torrentarr binary
- sudo/root access for setup

[**Systemd Installation Guide →**](installation/systemd.md)

---

### 📥 Binary (Standalone)

**Best for:**

- Advanced users
- Minimal dependencies
- Portable installations
- Testing without .NET (use binary)

**Advantages:**

- ✅ No .NET or Python required
- ✅ Portable executable
- ✅ Quick testing

**Platforms:**

- Linux (x86_64, aarch64)
- macOS (Intel, Apple Silicon)
- Windows (x86_64)

[**Binary Installation Guide →**](installation/binary.md)

---

## Installation Comparison

| Feature | Docker | dotnet tool | Systemd | Binary |
|---------|--------|-----|---------|--------|
| **Ease of Setup** | ⭐⭐⭐⭐⭐ | ⭐⭐⭐⭐ | ⭐⭐⭐ | ⭐⭐⭐⭐ |
| **Updates** | Very Easy | Easy | Manual | Manual |
| **Dependencies** | Auto | .NET 8+ | .NET or binary | None |
| **Cross-Platform** | Yes | Yes | Linux only | Yes |
| **Performance** | Good | Excellent | Excellent | Excellent |
| **Isolation** | Excellent | None | Good | None |
| **Auto-Start** | Yes | No | Yes | No |
| **Resource Usage** | Medium | Low | Low | Low |

---

## Quick Start

Once you've chosen and completed your installation method:

### Step 1: First Run

Start Torrentarr to generate the default configuration:

=== "Docker"

    ```bash
    docker-compose up -d torrentarr
    docker logs -f torrentarr
    ```

=== "dotnet tool"

    ```bash
    torrentarr
    ```

=== "Systemd"

    ```bash
    sudo systemctl start torrentarr
    journalctl -u torrentarr -f
    ```

=== "Binary"

    ```bash
    ./torrentarr
    ```

**Look for:**

```
Configuration file not found. Generating default config...
Configuration file created at: /config/config.toml
Please edit the configuration file and restart Torrentarr.
```

### Step 2: Stop Torrentarr

Stop Torrentarr to edit the configuration:

=== "Docker"

    ```bash
    docker-compose down
    ```

=== "dotnet tool / Binary"

    Press ++ctrl+c++

=== "Systemd"

    ```bash
    sudo systemctl stop torrentarr
    ```

### Step 3: Configure Torrentarr

Edit the generated `config.toml` file:

=== "Docker"

    ```bash
    # Config is in your mounted volume
    nano /path/to/config/config.toml
    ```

=== "dotnet tool"

    ```bash
    nano ~/config/config.toml
    ```

=== "Systemd"

    ```bash
    sudo nano /home/torrentarr/config/config.toml
    ```

**Minimum required configuration:**

```toml
[qBit]
Host = "localhost"
Port = 8080
UserName = "admin"
Password = "adminpass"

[Radarr-Movies]
URI = "http://localhost:7878"
APIKey = "your_radarr_api_key_here"
Category = "radarr-movies"
```

[**Detailed Configuration Guide →**](quickstart.md)

### Step 4: Start Torrentarr

Restart Torrentarr with your configuration:

=== "Docker"

    ```bash
    docker-compose up -d
    ```

=== "dotnet tool"

    ```bash
    torrentarr
    ```

=== "Systemd"

    ```bash
    sudo systemctl start torrentarr
    sudo systemctl enable torrentarr  # Enable auto-start
    ```

### Step 5: Verify Installation

1. **Check logs for successful connections:**

    ```
    Successfully connected to qBittorrent
    Successfully connected to Radarr-Movies
    WebUI started on http://0.0.0.0:6969
    ```

2. **Access the WebUI:**

    Open http://localhost:6969/ui in your browser

3. **Verify Processes tab:**

    All Arr manager processes should show as "Running"

---

## First Steps After Installation

### 1. Test with a Manual Download

1. In Radarr/Sonarr, manually search and grab a small file
2. Watch Torrentarr logs for activity
3. Verify Torrentarr detects and monitors the download
4. Check that import triggers when complete

### 2. Enable Automated Search (Optional)

If you want Torrentarr to search for missing media:

```toml
[Radarr-Movies.EntrySearch]
SearchMissing = true
SearchLimit = 5
SearchByYear = true
SearchRequestsEvery = 300
```

[**Automated Search Guide →**](../features/automated-search.md)

### 3. Configure Health Monitoring

Customize how Torrentarr handles failed downloads:

```toml
[Radarr-Movies.Torrent]
MaximumETA = 86400  # 24 hours
StalledDelay = 15  # 15 minutes
ReSearchStalled = true
```

[**Health Monitoring Guide →**](../features/health-monitoring.md)

### 4. Set Up Request Integration (Optional)

If using Overseerr or Ombi:

```toml
[Radarr-Movies.EntrySearch.Overseerr]
SearchOverseerrRequests = true
OverseerrURI = "http://localhost:5055"
OverseerrAPIKey = "your_overseerr_api_key"
ApprovedOnly = true
```

[**Request Integration Guide →**](../features/request-integration.md)

---

## Common Initial Setup Scenarios

### Scenario 1: Simple Home Server

**Setup:**
- Single Radarr instance
- Single Sonarr instance
- Basic health monitoring
- No request management

**Installation:** Docker via docker-compose

**Time to setup:** 15 minutes

[**Example Configuration →**](quickstart.md#scenario-1-simple-home-server)

---

### Scenario 2: Advanced Multi-Instance

**Setup:**
- Radarr 1080p + Radarr 4K
- Sonarr TV + Sonarr Anime
- Overseerr integration
- Custom format enforcement

**Installation:** Systemd service

**Time to setup:** 45 minutes

[**Example Configuration →**](quickstart.md#scenario-2-multi-arr-setup)

---

### Scenario 3: Shared Seedbox

**Setup:**
- Remote qBittorrent
- Multiple users/Arr instances
- Strict seeding requirements
- Disk space management

**Installation:** dotnet tool or binary + systemd

**Time to setup:** 30 minutes

[**Example Configuration →**](quickstart.md#scenario-4-docker-compose-full-stack)

---

## Learning Path

### Beginner (Week 1)

1. ✅ Install Torrentarr
2. ✅ Configure basic connectivity
3. ✅ Test with manual downloads
4. ✅ Explore WebUI

**Resources:**

- [First Run Guide](quickstart.md)
- [qBittorrent Configuration](../configuration/qbittorrent.md)
- [WebUI Overview](../webui/index.md)

### Intermediate (Week 2-3)

1. ✅ Enable automated search
2. ✅ Configure health monitoring
3. ✅ Set up seeding rules
4. ✅ Add request integration

**Resources:**

- [Automated Search](../features/automated-search.md)
- [Health Monitoring](../features/health-monitoring.md)
- [Seeding Configuration](../configuration/seeding.md)
- [Request Integration](../features/request-integration.md)

### Advanced (Week 4+)

1. ✅ Custom format enforcement
2. ✅ Quality upgrades
3. ✅ Multi-instance setups
4. ✅ Per-tracker rules

**Resources:**

- [Custom Formats](../features/custom-formats.md)
- [Quality Upgrades](../features/quality-upgrades.md)
- [Advanced Topics](../advanced/index.md)

---

## Troubleshooting Installation

### Torrentarr Won't Start

**Check:**

1. .NET 8+ (for dotnet tool) or use Binary/Docker
2. Port 6969 not in use
3. Config file exists and is valid TOML
4. Permissions on config/logs folders

[**Troubleshooting Guide →**](../troubleshooting/index.md)

### Can't Connect to qBittorrent

**Solutions:**

1. Verify qBittorrent WebUI is enabled
2. Check host/port in config
3. Test credentials manually
4. Ensure no firewall blocking

[**qBittorrent Issues →**](../troubleshooting/common-issues.md#qbittorrent-connection-failures)

### Can't Connect to Arr

**Solutions:**

1. Verify API key is correct
2. Check Arr instance is running
3. Test API manually with curl
4. Ensure network connectivity

[**Arr Connection Issues →**](../troubleshooting/common-issues.md#arr-instance-connection-failures)

---

## Next Steps

Choose your path based on your needs:

### 🎯 Quick Setup

Just want it working fast?

1. [Quick Start Guide](quickstart.md)
2. Test with downloads

**Time required:** 20 minutes

### 🔧 Comprehensive Setup

Want to understand everything?

1. [Installation Guide](installation/index.md)
2. [Configuration Overview](../configuration/index.md)
3. [Arr Configuration](../configuration/arr/index.md)
4. [Feature Guides](../features/index.md)

**Time required:** 2-3 hours

### 🚀 Advanced Configuration

Need complex features?

1. [Advanced Topics](../advanced/index.md)
2. [Custom Formats](../features/custom-formats.md)
3. [Quality Upgrades](../features/quality-upgrades.md)
4. [Multi-Instance Setup](../configuration/arr/index.md#multiple-instances)

**Time required:** 4-6 hours

---

## Command-Line Reference

### Usage

```bash
torrentarr [OPTIONS]
```

| Option | Description | Default |
|--------|-------------|---------|
| `--config PATH` | Path to config.toml file | `~/config/config.toml` (native), `/config/config.toml` (Docker) |
| `--gen-config` | Generate default configuration file and exit | - |
| `--version` | Show version information and exit | - |
| `--help` | Show help message and exit | - |

### Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `TORRENTARR_CONFIG` | Path to config.toml | (see search order below) |
| `PUID` / `PGID` | User/Group ID (Docker) | `1000` |
| `TZ` | Timezone | `UTC` |

Config file path is taken from `TORRENTARR_CONFIG` when set. All other settings are read from `config.toml` only (no per-key env overrides).

### Signals

| Signal | Behavior |
|--------|----------|
| `SIGTERM` | Graceful shutdown (recommended) |
| `SIGINT` | Graceful shutdown (Ctrl+C) |
| `SIGKILL` | Immediate termination (not recommended) |

### Exit Codes

| Code | Meaning |
|------|---------|
| `0` | Success |
| `1` | General error |
| `2` | Configuration error |
| `3` | Connection error |
| `130` | SIGINT (Ctrl+C) |
| `143` | SIGTERM |

### Config File Search Order

1. `--config` option (if added in future)
2. `TORRENTARR_CONFIG` environment variable
3. `~/config/config.toml`
4. `~/.config/qbitrr/config.toml`
5. `~/.config/torrentarr/config.toml`
6. `./config.toml`

---

## Getting Help

### Documentation

- [FAQ](../faq.md) - Frequently asked questions
- [Troubleshooting](../troubleshooting/index.md) - Common issues and solutions
- [Configuration Reference](../configuration/config-file.md) - Complete config documentation
- [API Reference](../webui/api.md) - REST API documentation

### Community

- **GitHub Discussions** - [Ask questions, share setups](https://github.com/Feramance/Torrentarr/discussions)
- **GitHub Issues** - [Report bugs, request features](https://github.com/Feramance/Torrentarr/issues)
- **Discord** - Real-time community support

### Contributing

Want to improve Torrentarr?

- [Development Guide](../development/index.md)
- [AGENTS.md](https://github.com/Feramance/Torrentarr/blob/master/AGENTS.md)

---

## Success Stories

!!! success "Migration from Manual Management"
    "Switching from manual torrent management to Torrentarr saved me 2-3 hours per week. Failed downloads are handled automatically, and new episodes import within seconds!" - *Home media server user*

!!! success "Multi-Instance 4K Setup"
    "Running separate 1080p and 4K Radarr instances with Torrentarr's custom format enforcement ensures I only grab the quality I want. Upgrade searches keep my library pristine." - *Quality enthusiast*

!!! success "Shared Seedbox"
    "Torrentarr manages our family seedbox with per-tracker seeding rules. Everyone's Arr instances work independently, and we maintain great ratios on private trackers." - *Seedbox user*

---

Ready to get started? Pick your installation method above and follow the guide!
