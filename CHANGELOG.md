# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## v5.9.1

### Features

- C# port of qBitrr with 100% config and database compatibility
- Multi-qBittorrent instance support for load balancing and VPN isolation
- Process-isolated architecture - WebUI stays online even if workers crash
- Hit and Run protection with tracker-based seeding rules
- Per-torrent free space management with auto-pause/resume
- Missing media search and quality upgrade automation
- Media validation with ffprobe integration
- Real-time WebUI with React dashboard

### Technical

- Built on .NET 10 and ASP.NET Core
- Entity Framework Core with SQLite (WAL mode)
- RestSharp for API clients (qBittorrent, Radarr, Sonarr, Lidarr)
- Tomlyn for TOML configuration parsing
- Serilog for structured logging
- React 18 + TypeScript frontend with Vite
