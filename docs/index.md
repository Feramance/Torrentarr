# Welcome to Torrentarr Documentation

<div style="text-align: center; margin: 2rem 0;">
  <img src="assets/logov2-clean.svg" alt="Torrentarr Logo" width="200"/>
</div>

**Torrentarr** is the intelligent glue between qBittorrent and the *Arr ecosystem (Radarr, Sonarr, Lidarr). It monitors torrent health, triggers instant imports when downloads complete, automates quality upgrades, manages disk space, integrates with request systems (Overseerr/Ombi), and provides a modern React dashboard for complete visibility and control.

[![GitHub release](https://img.shields.io/github/v/release/Feramance/Torrentarr)](https://github.com/Feramance/Torrentarr/releases)
[![Docker Pulls](https://img.shields.io/docker/pulls/feramance/torrentarr.svg?cacheSeconds=3600)](https://hub.docker.com/r/feramance/torrentarr)
[![License: MIT](https://img.shields.io/badge/license-MIT-green)](https://github.com/Feramance/Torrentarr/blob/master/LICENSE)

## Quick Links

<div class="feature-grid">
  <div class="feature-card">
    <h3>🚀 Getting Started</h3>
    <p>Install Torrentarr and get your first torrent monitored in minutes.</p>
    <a href="getting-started/index.md">Get Started →</a>
  </div>

  <div class="feature-card">
    <h3>⚙️ Configuration</h3>
    <p>Configure qBittorrent, Arr instances, and fine-tune your automation.</p>
    <a href="configuration/index.md">Configure →</a>
  </div>

  <div class="feature-card">
    <h3>✨ Features</h3>
    <p>Explore health monitoring, automated search, quality upgrades, and more.</p>
    <a href="features/index.md">Explore Features →</a>
  </div>

  <div class="feature-card">
    <h3>🔧 Troubleshooting</h3>
    <p>Resolve common issues and optimize your Torrentarr installation.</p>
    <a href="troubleshooting/index.md">Troubleshoot →</a>
  </div>
</div>

## Core Features

### 🚑 Torrent Health & Import Management
- **Instant imports** – trigger downloads scans the moment torrents finish
- **Stalled torrent detection** – identify and handle stuck/slow downloads
- **Failed download handling** – automatically blacklist and re-search
- **FFprobe verification** – validate media files before import
- **Smart file filtering** – exclude samples, extras, trailers

### 🔍 Automated Search & Request Integration
- **Missing media search** – automatically search for missing content
- **Quality upgrade search** – find better releases for existing media
- **Custom format scoring** – search based on custom format requirements
- **Overseerr/Ombi integration** – prioritize user requests
- **Temporary quality profiles** – use lower profiles, upgrade later

### 📊 Quality & Metadata Management
- **RSS sync automation** – schedule periodic RSS feed refreshes
- **Queue management** – keep Arr instances in sync
- **Custom format enforcement** – remove torrents not meeting CF scores
- **Quality profile switching** – dynamic profile changes per search type
- **Interactive profile configuration** – test connections from WebUI

### 🌱 Seeding & Tracker Control
- **Per-tracker settings** – configure MaxETA, ratios, seeding time
- **Global seeding limits** – upload/download rate limits
- **Automatic removal** – remove torrents by ratio or time
- **Dead tracker cleanup** – auto-remove failed trackers
- **Tag management** – auto-tag torrents by tracker

### 🛡️ Hit and Run Protection
- **Automatic HnR tracking** – prevent torrent removal until seeding obligations are met
- **Configurable thresholds** – per-tracker minimum ratio, seeding time, and download percentage
- **Partial download handling** – ratio-only clearing for incomplete downloads
- **Dead tracker bypass** – auto-exempt torrents from unregistered/unauthorized trackers
- **Tracker inheritance** – define HnR rules once at qBit level, inherited by all Arr instances

### 💾 Disk Space & Resource Management
- **Free space monitoring** – pause torrents when space is low
- **Auto pause/resume** – manage activity based on disk availability
- **Configurable thresholds** – set limits in KB, MB, GB, or TB

### 🔄 Auto-Updates & Self-Healing
- **Scheduled auto-updates** – update on a cron schedule
- **Manual update trigger** – one-click updates from WebUI
- **Installation-aware** – detects docker/dotnet/binary installs
- **Process auto-restart** – restart crashed processes automatically
- **Crash loop protection** – prevent infinite restart loops

### 💻 First-Party Web UI
- **Live process monitoring** – see all running Arr managers
- **Log viewer** – tail logs in real-time
- **Arr insights** – view movies, series, albums with filtering
- **Config editor** – edit configuration from the UI
- **Dark/light theme** – customizable appearance

## Installation

=== "Docker"

    ```bash
    docker run -d \
      --name torrentarr \
      -p 6969:6969 \
      -v /path/to/config:/config \
      feramance/torrentarr:latest
    ```

=== "Docker Compose"

    ```yaml
    services:
      torrentarr:
        image: feramance/torrentarr:latest
        container_name: torrentarr
        ports:
          - "6969:6969"
        volumes:
          - /path/to/config:/config
        restart: unless-stopped
    ```

=== "dotnet tool"

    ```bash
    dotnet tool install -g torrentarr
    torrentarr
    ```

[View detailed installation instructions →](getting-started/installation/index.md)

## Why Torrentarr?

### The Problem

Managing media downloads across qBittorrent and multiple Arr instances is complex:

- ❌ **Slow imports** - Arr apps check download folders periodically (every 1-5 minutes)
- ❌ **Failed downloads go unnoticed** - Stalled torrents waste indexer hits and bandwidth
- ❌ **Manual intervention required** - Quality upgrades need constant monitoring
- ❌ **Disk space issues** - Downloads fill up storage without warning
- ❌ **Request delays** - Overseerr/Ombi requests wait for manual searching
- ❌ **No visibility** - Difficult to track what's happening across services

### The Solution

Torrentarr bridges the gap with intelligent automation:

- ✅ **Instant imports** - Trigger imports immediately when downloads complete (seconds vs. minutes)
- ✅ **Smart health monitoring** - Detect and handle failed/stalled downloads automatically
- ✅ **Quality management** - Search for upgrades based on quality profiles and custom formats
- ✅ **Disk space management** - Pause downloads automatically when space is low
- ✅ **Request prioritization** - Process Overseerr/Ombi requests ahead of regular searches
- ✅ **Complete visibility** - Modern WebUI shows everything in real-time

### Real-World Impact

**Before Torrentarr:**
```
1. Overseerr request submitted → waiting
2. Manual search in Radarr → 5 minutes later
3. Torrent downloads → 20 minutes
4. Radarr checks download folder → +2 minutes delay
5. Import begins → +1 minute processing
Total time to library: 28 minutes
```

**With Torrentarr:**
```
1. Overseerr request submitted → detected immediately
2. Auto-search triggered → 30 seconds
3. Torrent downloads → 20 minutes
4. Torrentarr triggers instant import → +5 seconds
5. Import completes immediately
Total time to library: 20.5 minutes (26% faster)
```

---

## System Requirements

### Minimum Requirements

- **CPU**: 1 core (2+ cores recommended)
- **RAM**: 512 MB minimum (1 GB recommended)
- **Storage**: 100 MB for application + logs
- **Network**: Connectivity to qBittorrent and Arr instances

### Software Requirements

=== "Docker Installation"

    - Docker 20.10+
    - Docker Compose 2.0+ (optional but recommended)
    - No other dependencies required

=== "dotnet tool Installation"

    - .NET 8.0+ SDK or runtime
    - Or use Binary / Docker for no .NET requirement

=== "Binary Installation"

    - No .NET or Python required
    - Supported platforms:
        - Linux: x86_64, aarch64
        - macOS: Intel, Apple Silicon
        - Windows: x86_64

### Required Services

- **qBittorrent**: Version 4.1.0+ or 5.x with WebUI enabled
- **Arr Instance**: At least one of:
    - Radarr: v3.x, v4.x, v5.x
    - Sonarr: v3.x, v4.x
    - Lidarr: v1.x, v2.x

### Optional Services

- **Overseerr**: v1.26+ for request management
- **Ombi**: v4.0+ for alternative request management
- **FFprobe**: For media file validation (auto-downloaded by Torrentarr)

---

## Compatibility Matrix

### qBittorrent

| Version | Status | Notes |
|---------|--------|-------|
| 5.0+ | ✅ Fully Supported | Auto-detected |
| 4.6.x | ✅ Fully Supported | Latest stable |
| 4.5.x | ✅ Supported | Older stable |
| 4.1-4.4 | ✅ Supported | Some features limited |
| < 4.1 | ❌ Not Supported | Upgrade required |

### Radarr

| Version | Status | Notes |
|---------|--------|-------|
| 5.x | ✅ Fully Supported | Latest |
| 4.x | ✅ Fully Supported | Stable |
| 3.x | ✅ Supported | Older but functional |
| < 3.0 | ❌ Not Supported | Upgrade recommended |

### Sonarr

| Version | Status | Notes |
|---------|--------|-------|
| 4.x | ✅ Fully Supported | Latest |
| 3.x | ✅ Fully Supported | Stable |
| < 3.0 | ❌ Not Supported | Upgrade recommended |

### Lidarr

| Version | Status | Notes |
|---------|--------|-------|
| 2.x | ✅ Fully Supported | Latest |
| 1.x | ✅ Supported | Older but functional |
| < 1.0 | ❌ Not Supported | Upgrade recommended |

---

## Platform Support

### Operating Systems

| Platform | Docker | dotnet tool | Binary | Systemd |
|----------|--------|-----|--------|---------|
| **Linux (x86_64)** | ✅ | ✅ | ✅ | ✅ |
| **Linux (ARM64)** | ✅ | ✅ | ✅ | ✅ |
| **macOS (Intel)** | ✅ | ✅ | ✅ | ❌ |
| **macOS (Apple Silicon)** | ✅ | ✅ | ✅ | ❌ |
| **Windows 10/11** | ✅ | ✅ | ✅ | ❌ |
| **FreeBSD** | ⚠️ | ⚠️ | ❌ | ❌ |
| **Unraid** | ✅ | ❌ | ❌ | ❌ |
| **TrueNAS** | ✅ | ⚠️ | ❌ | ❌ |

✅ = Fully Supported | ⚠️ = Community Tested | ❌ = Not Supported

### Architectures

- **x86_64 (amd64)** - Fully supported on all platforms
- **ARM64 (aarch64)** - Fully supported (Raspberry Pi 4+, Apple Silicon)
- **ARMv7** - Community builds available
- **ARM64 (32-bit)** - Not officially supported

---

## Use Cases

### Home Media Server

Perfect for personal Plex/Jellyfin/Emby servers:

- Monitor 1-3 Arr instances
- Handle 10-100 downloads per day
- Basic quality management
- Request integration for family/friends

[**Example Setup →**](getting-started/quickstart.md#scenario-1-simple-home-server)

---

### Power User / Enthusiast

For users with extensive libraries and quality requirements:

- Multiple Radarr/Sonarr instances (4K, 1080p, anime, etc.)
- Custom format enforcement (TRaSH guides)
- Quality upgrade automation
- Per-tracker seeding rules
- Overseerr integration for public access

[**Example Setup →**](getting-started/quickstart.md#scenario-3-power-user-with-quality-control)

---

### Shared Seedbox

Manage shared seedboxes with multiple users:

- User isolation with separate Arr instances
- Strict seeding ratio enforcement
- Disk space management across users
- Per-user quality profiles
- Centralized monitoring via WebUI

[**Example Setup →**](getting-started/quickstart.md#scenario-4-docker-compose-full-stack)

---

### Private Tracker Focus

Optimize for private tracker requirements:

- Per-tracker seeding rules (RED, PTP, BTN, etc.)
- Strict ratio maintenance
- Custom format scoring for scene releases
- Long-term seeding with automatic cleanup
- Import verification with FFprobe

---

## Project Status

### Current Version

**Latest Release**: v5.9.0 (January 2025)

- ✅ Production ready
- ✅ Active development
- ✅ Regular updates
- ✅ Community support

### Recent Updates

- **v5.5** - Enhanced WebUI with real-time updates
- **v5.4** - Custom format enforcement improvements
- **v5.3** - Overseerr request integration
- **v5.2** - Auto-restart and self-healing features

[**Full Changelog →**](changelog.md)

### Roadmap

**Upcoming Features:**

- 🚧 Interactive configuration wizard
- 🚧 Torrent management from WebUI
- 🚧 Advanced statistics dashboard
- 🚧 Multi-language support
- 🚧 Mobile app (planned)

[**GitHub Projects →**](https://github.com/Feramance/Torrentarr/projects)

---

## Community & Support

### Getting Help

- **📚 Documentation**: You're reading it!
- **💬 Discussions**: [GitHub Discussions](https://github.com/Feramance/Torrentarr/discussions) - Ask questions, share setups
- **🐛 Issues**: [GitHub Issues](https://github.com/Feramance/Torrentarr/issues) - Report bugs, request features
- **💡 FAQ**: [Frequently Asked Questions](faq.md) - Common questions answered

### Contributing

Torrentarr is open source and welcomes contributions:

- **Code**: [Development Guide](development/index.md)
- **Docs**: Improve this documentation
- **Translations**: Help translate Torrentarr
- **Testing**: Test new features and report issues

### Support the Project

If Torrentarr saves you time and improves your media management:

- ⭐ **Star on GitHub**: [github.com/Feramance/Torrentarr](https://github.com/Feramance/Torrentarr)
- 💰 **Sponsor**: [Patreon](https://patreon.com/Torrentarr) | [PayPal](https://www.paypal.me/feramance)
- 📢 **Share**: Tell others about Torrentarr
- 🐛 **Report Issues**: Help improve quality

---

## Quick Navigation

### First Time User?

1. [Getting Started Guide →](getting-started/index.md)
2. [Installation →](getting-started/installation/index.md)
3. [Quick Start →](getting-started/quickstart.md)

### Already Installed?

1. [Configuration Reference →](configuration/index.md)
2. [Feature Guides →](features/index.md)
3. [WebUI Documentation →](webui/index.md)
4. [Troubleshooting →](troubleshooting/index.md)

### Advanced User?

1. [Advanced Topics →](advanced/index.md)
2. [API Reference →](webui/api.md)
3. [Development →](development/index.md)
4. [CLI Reference →](getting-started/index.md#command-line-reference)

---

## License

Torrentarr is free and open source software licensed under the [MIT License](https://github.com/Feramance/Torrentarr/blob/master/LICENSE).

```
Copyright (c) 2024 Feramance

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software.
```

---

## Credits

**Maintainer**: [Feramance](https://github.com/Feramance)

**Built With**:

- [.NET](https://dotnet.microsoft.com/) - Backend runtime
- [ASP.NET Core](https://aspnetcore.io/) - API framework
- [React](https://react.dev/) - WebUI framework
- [Mantine](https://mantine.dev/) - UI component library

**Thanks To**:

- All contributors who have submitted code, documentation, and bug reports
- The *Arr community for feature requests and feedback
- Users who support the project through sponsorship

---

**Ready to get started?** [Install Torrentarr Now →](getting-started/installation/index.md)
