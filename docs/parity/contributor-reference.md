# qBitrr parity: contributor reference

Internal notes for **maintainers and contributors** comparing Torrentarr to upstream [qBitrr](https://github.com/Feramance/qBitrr). End users: see [overview.md](overview.md) instead. Row-level status: [full-parity-matrix.md](full-parity-matrix.md).

---

## Upstream qBitrr baseline

Torrentarr parity work is diffed against a **pinned** upstream ref so behavior does not float with every `master` commit.

### Pinned release

| Field | Value |
| --- | --- |
| **Tag** | `v5.11.1` |
| **Role** | Default behavioral baseline for policy (`arss.py`), `SortTorrents` + `arr_tracker_index.py`, WebUI/OpenAPI additions, and docs at that line. |
| **Commit (verify locally)** | `git ls-remote --tags https://github.com/Feramance/qBitrr v5.11.1` or `git show v5.11.1` after clone. Re-record the full SHA in release notes when bumping the pin. |

To move the pin: update this section, re-run the inventories below, and adjust [full-parity-matrix.md](full-parity-matrix.md) / tests as needed.

### Upstream file inventory (compare surface)

| Area | Upstream path (Feramance/qBitrr) |
| --- | --- |
| Config model | `qBitrr/config.py`, `qBitrr/gen_config.py`, `qBitrr/config_version.py`, `qBitrr/env_config.py` |
| Durations | `qBitrr/duration_config.py` |
| DB schema | `qBitrr/tables.py`, `qBitrr/database.py`, `qBitrr/db_lock.py`, `qBitrr/db_recovery.py` |
| Core loop + policy | `qBitrr/arss.py` |
| Seeding / trackers | `qBitrr/qbit_category_manager.py`, `qBitrr/arr_tracker_index.py` |
| Web + API | `qBitrr/webui.py` |
| OpenAPI (if present on tag) | `qBitrr/openapi.json` |
| Config example | `config.example.toml` |
| Public API doc | `docs/webui/api.md` |
| Operator scripts | `scripts/repair_database.py`, `scripts/repair_database_targeted.py` |

**Browse tag:** [v5.11.1 on GitHub](https://github.com/Feramance/qBitrr/releases/tag/v5.11.1). **Raw prefix:** `https://raw.githubusercontent.com/Feramance/qBitrr/v5.11.1/`

### Torrentarr mapping (where to look)

| Upstream | Torrentarr (primary) |
| --- | --- |
| `config*.py` | [ConfigurationLoader.cs](../../src/Torrentarr.Core/Configuration/ConfigurationLoader.cs), [TorrentarrConfig.cs](../../src/Torrentarr.Core/Configuration/TorrentarrConfig.cs) |
| `arss.py` / policy | [TorrentPolicyHelper.cs](../../src/Torrentarr.Core/Configuration/TorrentPolicyHelper.cs), [Host Program.cs](../../src/Torrentarr.Host/Program.cs), [TorrentProcessor.cs](../../src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs) |
| `webui.py` | [WebUI Program.cs](../../src/Torrentarr.WebUI/Program.cs), [Host Program.cs](../../src/Torrentarr.Host/Program.cs), `webui/` |
| `tables.py` | [TorrentarrDbContext.cs](../../src/Torrentarr.Infrastructure/Database/TorrentarrDbContext.cs), `Database/Models/*.cs` |

### Release validation checklist

1. Diff `config.example.toml` (upstream) vs Torrentarr’s documented config and [ConfigurationLoader](../../src/Torrentarr.Core/Configuration/ConfigurationLoader.cs) for new keys.
2. Diff route lists: upstream `docs/webui/api.md` and `openapi.json` vs [docs/assets/openapi.json](../assets/openapi.json) and [docs/webui/api.md](../webui/api.md) — use [OpenAPI alignment](#openapi-alignment) below.
3. Run: `dotnet test --filter "Category!=Live"`, `npx vitest run` in `webui/`.

---

## Intentional differences

Parity means **compatible config**, **equivalent external behavior** (qBittorrent + Arr + SQLite + HTTP API), and **documented** exceptions. These are **by design**; they are not bugs.

| Area | qBitrr (Python) | Torrentarr (C#) | Notes |
| --- | --- | --- | --- |
| **Process model** | Single process | [Host](https://github.com/Feramance/Torrentarr) orchestrates WebUI + per-Arr workers | WebUI stays up if a worker crashes. |
| **Runtime / install** | Python, `pip`, `setup.py` | .NET 10, releases/Docker | `setup.py` row in matrix: intentional-divergence. |
| **Database filename** | `qbitrr.db` (conventional) | `torrentarr.db` | Same schema intent; name reflects product. |
| **Release major version** | 5.x | 6.x (+1 major) | [AGENTS.md](../../AGENTS.md), [index.md](../index.md) |
| **Migrations** | Peewee / Python | EF Core + [ConfigurationLoader](../../src/Torrentarr.Core/Configuration/ConfigurationLoader.cs) | Preserve TOML + DB upgrade stories. |
| **Logging** | Python logging | Serilog | Parity **goal** for events; format differs. |
| **CI automations** | e.g. `.github/autofix` | See [Support scripts and CI](#support-scripts-and-ci) | No user feature required to match. |

If you find a difference not listed, open an issue or add a matrix row with status `intentional-divergence` and evidence.

---

## Targeted database repair

Upstream may ship `repair_database_targeted.py`. Torrentarr does not port that script.

1. **Stop** all processes using the DB.
2. Use Host `--repair-database` and [DatabaseHealthService](../../src/Torrentarr.Infrastructure/Services/DatabaseHealthService.cs) — [database troubleshooting](../troubleshooting/database.md).
3. For isolated bad rows: **backup** `torrentarr.db`, then `sqlite3` with `PRAGMA foreign_key_check;` and targeted `DELETE`/`UPDATE` as appropriate.
4. `PRAGMA integrity_check;`, then start Torrentarr.

**Matrix:** `repair_database_targeted.py` = intentional-divergence. Tests: [DatabaseHealthServiceTests.cs](../../tests/Torrentarr.Infrastructure.Tests/Services/DatabaseHealthServiceTests.cs).

---

## Policy engine test matrix

Maps upstream concepts to CI tests; live qBittorrent still needed for full ordering proof.

### Unit / integration (CI)

| Concern | qBitrr | Torrentarr | Tests |
| --- | --- | --- | --- |
| `SortTorrents` gating | `global_sort_torrents_enabled` | [TorrentPolicyHelper](../../src/Torrentarr.Core/Configuration/TorrentPolicyHelper.cs) | [TorrentPolicyHelperTests.cs](../../tests/Torrentarr.Core.Tests/Configuration/TorrentPolicyHelperTests.cs) |
| Queue position sort | `_torrent_queue_position_sort_key` | `TorrentQueuePositionSortKey` | same |
| Queue seeding for sort | `is_queue_seeding_for_sort` | `IsQueueSeedingForSort` | same |
| Monitored policy categories | Union | `IsMonitoredPolicyCategory` / cache | same |
| Free space gate | + AutoPause + qBit | `EnableFreeSpace` | same |
| Tracker merge (priority) | `merge_global_tracker_tag_to_priority_max` | `MergeGlobalTrackerTagToPriorityMax` | same |
| State machine | `_process_single_torrent` | [TorrentProcessor](../../src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs) | [TorrentProcessorTests.cs](../../tests/Torrentarr.Infrastructure.Tests/Services/TorrentProcessorTests.cs) |
| Seeding / HnR | `qbit_category_manager` | [SeedingService](../../src/Torrentarr.Infrastructure/Services/SeedingService.cs) | [SeedingServiceTrackerMergeTests.cs](../../tests/Torrentarr.Infrastructure.Tests/Services/SeedingServiceTrackerMergeTests.cs) |

### Live (`Category=Live`) — optional

| Scenario | Goal |
| --- | --- |
| `SortTorrents` on, multiple trackers | Queue / `topPrio` vs tracker priority — [Host Program.cs](../../src/Torrentarr.Host/Program.cs) |
| Free space + `AutoPauseResume` | [FreeSpaceService](../../src/Torrentarr.Infrastructure/Services/FreeSpaceService.cs) |
| Multi `qBit-*` | Per-instance `QBitInstanceName` |

`dotnet test --filter "Category=Live"` with real [config](../configuration/config-file.md).

---

## Web and API field coverage

Compare to upstream on the [pinned tag](#upstream-qbitrr-baseline) for **behavior-affecting** API/UI only (not layout).

- **Routes:** [WebUI Program.cs](../../src/Torrentarr.WebUI/Program.cs) + Host vs `webui.py` and [upstream API](https://github.com/Feramance/qBitrr/blob/v5.11.1/docs/webui/api.md).
- **OpenAPI:** [docs/assets/openapi.json](../assets/openapi.json) vs upstream `qBitrr/openapi.json` — [OpenAPI alignment](#openapi-alignment).
- **React:** Config (all TOML keys, including `SortTorrents` on trackers), Processes, Logs, Auth — [docs/webui/api.md](../webui/api.md).

---

## Long-tail module mapping

| qBitrr | Role | Torrentarr |
| --- | --- | --- |
| `pyarr_compat.py` | Arr API normalization | [ApiClients/Arr/](../../src/Torrentarr.Infrastructure/ApiClients/Arr/) |
| `ffprobe.py` | Media checks | [MediaValidationService.cs](../../src/Torrentarr.Infrastructure/Services/MediaValidationService.cs) |
| `errors.py` | Errors | Exception types + HTTP JSON in WebUI |
| `logger.py` | Logging | Serilog in Host/WebUI/Workers |
| `utils.py` | Helpers | Search `qBitrr` in Infrastructure |
| `versioning` / `bundled_data` | Version/assets | [UpdateService](../../src/Torrentarr.Host/UpdateService.cs) |
| `home_path.py` | Config paths | [ConfigurationLoader](../../src/Torrentarr.Core/Configuration/ConfigurationLoader.cs) |

---

## Support scripts and CI

| Upstream | Torrentarr |
| --- | --- |
| `scripts/repair_database.py` | [DatabaseHealthService](../../src/Torrentarr.Infrastructure/Services/DatabaseHealthService.cs), `--repair-database` |
| `repair_database_targeted.py` | [Targeted database repair](#targeted-database-repair) — no separate script |
| `.github/scripts/update_releases.py` | [Release process](../development/release-process.md) + GitHub Actions |
| `.github/autofix` | pre-commit, `dotnet format`, PR review — no user feature |
| `setup.py` / PyPI | NuGet / releases / Docker — intentional |

---

## OpenAPI alignment

**Pin:** use the [Upstream baseline](#upstream-qbitrr-baseline) tag when fetching upstream `qBitrr/openapi.json`.

Torrentarr: [docs/assets/openapi.json](../assets/openapi.json), Swagger at `/swagger`. Comparing to upstream is a **drift check**, not a byte-identical merge.

**When** changing WebUI DTOs/controllers: diff paths/methods for `/web/*`, `/api/*`, auth, health.

**Optional diff:**

```bash
curl -sL "https://raw.githubusercontent.com/Feramance/qBitrr/v5.11.1/qBitrr/openapi.json" -o /tmp/qbitrr-openapi.json
# diff with docs/assets/openapi.json (format JSON first for meaningful output)
```

Reference: [docs/webui/api.md](../webui/api.md), [contract-baseline.md](contract-baseline.md) section 4.
