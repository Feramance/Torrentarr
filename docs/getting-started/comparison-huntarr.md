# Comparison: Torrentarr vs Huntarr

Huntarr automates hunting for missing and upgradeable content across *Arr instances and offers optional Swaparr (stalled-download swapping) and Movie Hunt (standalone movie grabber). Torrentarr focuses on **qBittorrent–\*Arr glue**: torrent health, instant import, and automated search. This page highlights where Torrentarr is **as good or better** for missing-item search and torrent handling.

---

## Missing-items search: Torrentarr's strengths

### Continuous, configurable search loop

Torrentarr queries each Arr instance for missing items, sorts them by priority (year, release date, etc.), and runs searches with a configurable delay (`SearchLoopDelay`) between individual search commands. Optional `SearchAgainOnSearchCompletion` restarts the loop so newly available releases are found without manual intervention.

See [Automated Search](../features/automated-search.md) for full details.

### Per-Arr, per-instance control

Each Radarr, Sonarr, and Lidarr instance has its own `EntrySearch` settings: `SearchMissing`, `DoUpgradeSearch`, custom format options, and temporary quality profiles. You can tune search behavior separately for 4K vs 1080p, TV vs anime, or music vs movies.

### Request prioritization

[Overseerr](../configuration/search/overseerr.md) and [Ombi](../configuration/search/ombi.md) integration lets Torrentarr search for **requested** items before general missing/upgrade queues, so user requests are fulfilled sooner.

### Quality and custom formats

Torrentarr supports quality upgrade search, custom format scoring (including TRaSH-style guides), and temporary quality profiles for hard-to-find content. Optional force-reset on startup returns items to their main profile after testing.

### Same stack as torrent handling

Search runs alongside torrent monitoring in the Host/worker architecture. One config file (TOML), one deployment—no separate hunt service to install or keep in sync. Workers are isolated processes so the WebUI stays up if a worker crashes.

### Comparison note (Huntarr)

Huntarr uses scheduled hunt cycles (e.g. every 30 minutes, N missing + M upgrade items per cycle). Torrentarr uses a continuous loop with configurable delay between individual searches and optional restart-on-completion. Both support multi-instance and quality upgrades; Torrentarr adds request integration and lives in the same stack as torrent handling.

---

## Torrent handling: Torrentarr's strengths

### Instant import

When a torrent completes in qBittorrent, Torrentarr triggers the Arr's download scan **immediately** (within seconds) instead of waiting for Arr's periodic 1–5 minute poll. Media appears in your library faster.

See [Instant Imports](../features/instant-imports.md) for details.

### Stalled detection and action

Torrentarr uses time-based stalled detection (`StalledDelay`, MaxETA). When a torrent is stalled beyond the threshold, you can optionally re-search **before** removal (`ReSearchStalled`). Then Torrentarr removes it from qBittorrent and the Arr queue and triggers a new search if `ReSearch` is enabled, so the Arr finds an alternative release.

See [Health Monitoring](../features/health-monitoring.md) for the full workflow.

### Failed download handling

Failed imports are blacklisted in the Arr so the same bad release is not grabbed again. With `ReSearch` enabled, Torrentarr triggers a new search so the Arr can grab a different release.

### FFprobe verification

Torrentarr can validate media files with FFprobe before import. Invalid or corrupted files are rejected and re-search is triggered when enabled, reducing bad imports.

### Built-in stalled/failed handling

Health checks and search run together. There is no separate Swaparr-like service to install or tune—stalled and failed handling are built in.

### Stalled handling comparison (Swaparr)

Swaparr (often used with Huntarr) offers strike-based removal and an "ignore above size" option; Torrentarr uses a single time/ETA threshold and does not have size exemption or dry run. For many users, Torrentarr's time-based stalled handling plus instant import and failed/blacklist handling is sufficient and simpler to operate.

---

## Feature snapshot

| Feature | Torrentarr | Huntarr / related |
| ------- | ---------- | ----------------- |
| Missing media search | Yes; continuous loop, configurable delay | Yes; scheduled hunt cycles |
| Quality upgrade search | Yes; per-instance, custom formats | Yes |
| Request prioritization (Overseerr/Ombi) | Yes; built-in | No (separate request flow) |
| Instant import on completion | Yes; triggers Arr scan within seconds | No; relies on Arr polling |
| Stalled torrent handling | Yes; time-based, optional re-search before removal | Swaparr: strike-based, optional |
| Failed/blacklist + re-search | Yes; blacklist in Arr, trigger new search | Varies |
| FFprobe verification | Yes; validate before import | No |
| Multi-instance *Arr | Yes | Yes |
| Single config / integrated stack | Yes; one TOML, Host + workers | Huntarr + optional Swaparr/Movie Hunt |

---

## What Huntarr has that Torrentarr doesn't

- **Swaparr:** Strike-based removal (multiple checks before removal), "ignore above size" for large files, and dry-run mode. If you need those, you can run Swaparr alongside Torrentarr.
- **Movie Hunt:** A standalone movie grabber with its own indexers and download client—you can search and grab movies without adding them to Radarr. Torrentarr does not add or grab content on its own; it only manages torrents that were added by an Arr.

If you rely on standalone grabbing or Swaparr's strike/size options, you can run Huntarr (or Swaparr) alongside Torrentarr. Torrentarr does not replace standalone grabbing.

---

## When to choose Torrentarr

- You want **one tool** for missing-item search and torrent health in a qBittorrent + *Arr setup.
- You care about **instant import**, **stalled/failed handling**, and **request prioritization** without adding another scheduler or client.
- You prefer a **single config** and a [Web UI](../webui/index.md) for monitoring, with process isolation between WebUI and Arr workers.

---

## Migrating from Huntarr

1. **Install Torrentarr** — [Installation](installation/index.md) (Docker or binary).
2. **Configure** — Point Torrentarr at the same qBittorrent and Arr instances; set categories to match your Arr download clients.
3. **Enable search** — In each Arr's `EntrySearch` section, set `SearchMissing = true` and `DoUpgradeSearch = true` as needed. Optionally add [Overseerr](../configuration/search/overseerr.md) or [Ombi](../configuration/search/ombi.md).
4. **Disable or repurpose Huntarr** — Turn off Huntarr hunt cycles (or stop Huntarr) once Torrentarr is handling search and torrents to your satisfaction.

For backup, rollback, and detailed migration steps, see the [Migration Guide](migration.md).
