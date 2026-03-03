# Changelog

## v5.9.1 (2026-02-22)

### Features
- chore: Add project essentials for open source release ([c189dd6](https://github.com/Torrentarr/Torrentarr/commit/c189dd619ffa62d486bffcb841b69d8a03a4efbf))
- docs: Add comprehensive project summary ([7aad038](https://github.com/Torrentarr/Torrentarr/commit/7aad038bb90afec87b2b2b7d39a9c446c70c8cc3))
- feat: Add comprehensive Docker support with multi-stage builds ([5cde09b](https://github.com/Torrentarr/Torrentarr/commit/5cde09b4af54299617979c9dbf4287e854d942f7))
- feat: Add React frontend with dashboard and media management ([219acda](https://github.com/Torrentarr/Torrentarr/commit/219acdac8c3deb9411ca46aa76795c4523614c95))
- feat: Integrate services into Workers and add comprehensive WebUI API endpoints ([62cf74e](https://github.com/Torrentarr/Torrentarr/commit/62cf74e56608201561feff5af352df05803188d9))
- feat: Add Arr media, seeding, and free space management services ([b248e42](https://github.com/Torrentarr/Torrentarr/commit/b248e420490672f95b9a2ec4648c6bacb10cd6e6))
- feat: Integrate torrent processing services into Workers ([4469512](https://github.com/Torrentarr/Torrentarr/commit/4469512d43692ebfd7b07efde05e3ac75fa0c952))
- Implement database models, API clients, orchestrator, and workers ([7353c23](https://github.com/Torrentarr/Torrentarr/commit/7353c23844c36bb95555e62ac9f88f9868877f4a))
- Add comprehensive README with setup instructions and project overview ([6b9db04](https://github.com/Torrentarr/Torrentarr/commit/6b9db046f447f902f4287355abc5dbe314bddd29))
- Implement core infrastructure: config, database, API clients, WebUI ([18c4c64](https://github.com/Torrentarr/Torrentarr/commit/18c4c64eb399f032ec29a2e7621875e2450bdcff))

### Bug Fixes
- [patch] test: trigger patch release v5.9.5 ([3608984](https://github.com/Torrentarr/Torrentarr/commit/36089848f449b0b2b647fc091985299ed316d6ef))
- [patch] test: trigger patch release v5.9.3 (attempt 3) ([4653c5f](https://github.com/Torrentarr/Torrentarr/commit/4653c5f7b7f5a1f0f1272083e07af8691f754eae))
- [patch] test: trigger patch release v5.9.3 ([8da047c](https://github.com/Torrentarr/Torrentarr/commit/8da047c09feb3f8260b7c3be5c67cb10802a38a6))
- [patch] test: trigger patch release to verify workflows ([1339ec4](https://github.com/Torrentarr/Torrentarr/commit/1339ec40d9e759530322a7e0267ea60d8ad1dc41))
- [patch] docs: update PLAN.md with release workflow notes ([4a9b4a1](https://github.com/Torrentarr/Torrentarr/commit/4a9b4a1e9af69a1db3f4b2485033830038619233))
- [patch] docs: update PLAN.md with docker build notes ([f3e9f6b](https://github.com/Torrentarr/Torrentarr/commit/f3e9f6b3353ce0bb28588c0338ea5802102f29ac))
- [patch] fix(ci): fix release workflow and CodeQL ([0489ac0](https://github.com/Torrentarr/Torrentarr/commit/0489ac0cc6577b1062a0d11a481eea7638a2996e))

### Documentation
- docs: Update README with current implementation status ([d530c0c](https://github.com/Torrentarr/Torrentarr/commit/d530c0c3559990edba52399c8eca88e3af438c59))

### Refactoring
- refactor: unify qBit instances and add comprehensive test suite ([94d5cba](https://github.com/Torrentarr/Torrentarr/commit/94d5cba2ea14599d38710236a279b2ebd819e911))

### Maintenance
- fix(ci): add stash/pull/pop before changelog commit ([e0abf30](https://github.com/Torrentarr/Torrentarr/commit/e0abf30d7d4f089e8ab3580332a6bd4269ea2c2e))
- fix(ci): remove redundant pull step in changelog job ([cff5e9a](https://github.com/Torrentarr/Torrentarr/commit/cff5e9a94bf51cd469ba9ee89447bc204ce46470))
- fix(ci): fix bump2version config and add version sync step ([c73fec9](https://github.com/Torrentarr/Torrentarr/commit/c73fec9769b00a7a00aac0b3f64a0db6739a588a))
- fix(docker): add npm timeout settings and cache mount for faster builds ([30b3a5b](https://github.com/Torrentarr/Torrentarr/commit/30b3a5bf677978c644327912b4384c4f46023bcb))
- fix(ci): add git pull rebase before changelog commit ([752b62d](https://github.com/Torrentarr/Torrentarr/commit/752b62d2ec317100c41502b5027ade24a24cd354))
- fix(ci): update all version fields in bump2version config ([2ad487b](https://github.com/Torrentarr/Torrentarr/commit/2ad487b04e52b635cf428771e9931f8803be63ad))
- fix(ci): escape braces in bump2version config ([e54a7d0](https://github.com/Torrentarr/Torrentarr/commit/e54a7d0ae5753066b1d8347958bfaa94275bc8bc))
- fix(ci): fix bump2version search pattern for TorrentarrConfig.cs ([e1e47f8](https://github.com/Torrentarr/Torrentarr/commit/e1e47f8225f3132b9ee67f9eb28c0689a150f7cb))
- fix(ci): fix workflow failures ([c37cfcc](https://github.com/Torrentarr/Torrentarr/commit/c37cfccc47eb3d15ae15bc44e31a3330e59b60c9))
- chore(deps-dev): bump @tailwindcss/postcss in /webui (#8) ([c55ec66](https://github.com/Torrentarr/Torrentarr/commit/c55ec6681b2f7d1d86ceccbc3cdebd50316f07ac))
- chore(deps-dev): bump tailwindcss from 4.1.18 to 4.2.0 in /webui (#6) ([8a71c60](https://github.com/Torrentarr/Torrentarr/commit/8a71c60ae40711ddb2dfc6824fcd6442426f0fb8))
- chore(deps): bump react-hook-form from 7.71.1 to 7.71.2 in /webui (#4) ([5949ade](https://github.com/Torrentarr/Torrentarr/commit/5949ade09336b6750f67cd6c8095e38386783f26))
- chore: add GitHub workflows, bump2version, issue templates, and fix logo ([d53bf97](https://github.com/Torrentarr/Torrentarr/commit/d53bf9737596edce71c0c6f916865c542629d3b9))
- refactor(seeding): move HnR to tracker-only config, add state-based removal logic ([8eb4ce5](https://github.com/Torrentarr/Torrentarr/commit/8eb4ce5008067eeb9b976a1c7220d383ce4a585e))
- chore: remove stale Commandarr.* files from pre-rename scaffold ([11dfaeb](https://github.com/Torrentarr/Torrentarr/commit/11dfaeb0606ea24521c733b1c8bf367ca099d98b))
- fix(docker): fix content root and config path in runtime container ([1dcbb8e](https://github.com/Torrentarr/Torrentarr/commit/1dcbb8eff574a35e5ace2f450cd19acd9d7d27fc))
- fix(docker): fix Dockerfile to build correctly with Vite frontend ([ebb3a4a](https://github.com/Torrentarr/Torrentarr/commit/ebb3a4a010fb62451772d727b049da097cb3164a))
- fix(webui/logs): security, reliability, and UX improvements ([28a7e07](https://github.com/Torrentarr/Torrentarr/commit/28a7e07aa37b530d27ba650b6ffb91a264fa9d9f))
- Initial project structure with .NET solution and NuGet packages ([7b54c13](https://github.com/Torrentarr/Torrentarr/commit/7b54c13e6faa976716ba3b45268538bf7e691e92))

---

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## v5.9.1

### Features

- C# port of qBitrr with same config.toml format and SQLite schema; database file is `torrentarr.db` (not `qbitrr.db`)
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
