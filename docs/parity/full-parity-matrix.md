# Full Parity Matrix (qBitrr -> Torrentarr)

This matrix tracks strict full parity against upstream qBitrr master at the Python-module level.

Status values:

- `full`: behavior and contract are implemented and verified.
- `partial`: implementation exists but differs or lacks full verification.
- `missing`: no equivalent behavior exists yet.
- `intentional-divergence`: implementation differs by design and must prove identical user-facing outcomes.

## Runtime Package Coverage (`qBitrr/qBitrr`)

| qBitrr file | Torrentarr equivalent | Status | Required actions |
| --- | --- | --- | --- |
| `qBitrr/__init__.py` | `src/Torrentarr.Host/Program.cs`, assembly metadata | partial | Define version/package metadata parity checks and startup identity behavior. |
| `qBitrr/main.py` | `src/Torrentarr.Host/Program.cs`, `src/Torrentarr.Infrastructure/Services/ArrWorkerManager.cs` | partial | Validate process orchestration parity, startup ordering, lifecycle edge cases. |
| `qBitrr/arss.py` | `TorrentPolicyHelper` + Host `ProcessTorrentPolicyAsync` / `SortManagedTorrentsByTrackerPriorityAsync`; `TorrentProcessor.cs`, `ArrSyncService.cs`, `ArrImportService.cs`, `ArrMediaService.cs`, `SearchExecutor.cs`, `src/Torrentarr.Workers/Program.cs` | partial | Continue tightening Arr loop and edge-case parity with scenario tests. |
| `qBitrr/qbit_category_manager.py` | `src/Torrentarr.Infrastructure/Services/SeedingService.cs` | partial | Verify qBit-managed category semantics, tracker merge order, and HnR parity. |
| `qBitrr/arr_tracker_index.py` | `src/Torrentarr.Infrastructure/Services/SeedingService.cs` | partial | Add explicit tracker index abstraction or equivalent deterministic behavior tests. |
| `qBitrr/config.py` | `src/Torrentarr.Core/Configuration/TorrentarrConfig.cs`, `ConfigurationLoader.cs` | partial | Perform key-by-key config contract parity and validation behavior alignment. |
| `qBitrr/gen_config.py` | `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` | partial | Port missing migration branches, backup semantics, and idempotency guarantees. |
| `qBitrr/config_version.py` | `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` | partial | Reconcile `ConfigVersion` behavior and newer-version warning/error semantics. |
| `qBitrr/env_config.py` | `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` | partial | Align environment override behavior and document exact key mapping. |
| `qBitrr/duration_config.py` | `src/Torrentarr.Core/Configuration/DurationParser.cs` | partial | Validate all format permutations against qBitrr fixtures. |
| `qBitrr/database.py` | `src/Torrentarr.Infrastructure/Database/TorrentarrDbContext.cs`, `src/Torrentarr.Infrastructure/Services/DatabaseHealthService.cs`, `src/Torrentarr.Host/Program.cs` | partial | Add deterministic migration and startup repair parity, including integrity flow. |
| `qBitrr/tables.py` | `src/Torrentarr.Infrastructure/Database/Models/*.cs`, `TorrentarrDbContext.cs` | partial | Complete schema/index diff test harness and enforce zero drift. |
| `qBitrr/db_lock.py` | EF/SQLite locking behavior in `TorrentarrDbContext` and DB services | partial | Add concurrent writer/read tests to prove equivalent runtime guarantees. |
| `qBitrr/db_recovery.py` | `src/Torrentarr.Infrastructure/Services/DatabaseHealthService.cs`, Host repair command | partial | Expand recovery parity for corruption handling and operator recovery workflow. |
| `qBitrr/search_activity_store.py` | `src/Torrentarr.Infrastructure/Database/Models/SearchActivity.cs`, worker services | partial | Verify write/update semantics and UI/API usage parity. |
| `qBitrr/webui.py` | `src/Torrentarr.Host/Program.cs`, `src/Torrentarr.WebUI/Program.cs`, `webui/src` | partial | Align route contracts, auth/OIDC flows, payload shape, and OpenAPI snapshots. |
| `qBitrr/auto_update.py` | `src/Torrentarr.Host/Services/UpdateService.cs`, `AutoUpdateBackgroundService.cs` | partial | Validate check/download/apply behavior and scheduling equivalence. |
| `qBitrr/pyarr_compat.py` | `src/Torrentarr.Infrastructure/ApiClients/Arr/*.cs` | partial | Verify compatibility semantics and error/response normalization parity. |
| `qBitrr/ffprobe.py` | `src/Torrentarr.Infrastructure/Services/MediaValidationService.cs` | partial | Confirm ffprobe install/bootstrap and validation fallback parity. |
| `qBitrr/versioning.py` | Host metadata endpoints and update services | partial | Align release/version reporting semantics and API representation. |
| `qBitrr/bundled_data.py` | Embedded assets/config defaults in Host/WebUI projects | partial | Inventory bundled resources and align lifecycle/packaging semantics. |
| `qBitrr/home_path.py` | `ConfigurationLoader.GetDefaultConfigPath()`, host path logic | partial | Verify all path fallbacks and environment priority behavior. |
| `qBitrr/logger.py` | Serilog configuration in Host/WebUI/Workers | partial | Match category names, structured fields, log level defaults, and file output behavior. |
| `qBitrr/errors.py` | Exception types across Core/Infrastructure/Host | partial | Create parity mapping for error classes and user-visible error contract. |
| `qBitrr/utils.py` | Utility methods spread across Core/Infrastructure | partial | Build utility parity checklist and fill missing helper behaviors. |

## Support / Ops / Packaging Coverage

| qBitrr file | Torrentarr equivalent | Status | Required actions |
| --- | --- | --- | --- |
| `scripts/repair_database.py` | Host `--repair-database`, `DatabaseHealthService` | partial | Add scripted repair parity docs and testable operator procedure. |
| `scripts/repair_database_targeted.py` | No direct equivalent | missing | Implement targeted repair workflow or document validated equivalent procedure. |
| `scripts/rebuild_and_deploy.py` | `build.bat`, CI pipelines, Docker workflows | partial | Align deployment automation capabilities and docs. |
| `.github/scripts/update_releases.py` | Release workflow scripts in Torrentarr CI | missing | Add release metadata automation or mark intentional with evidence-based rationale. |
| `.github/autofix/auto_fix.py` | No direct equivalent | intentional-divergence | Document CI autofix policy divergence and ensure no user-facing feature impact. |
| `setup.py` | `.csproj` packaging and release build pipeline | intentional-divergence | Document packaging model divergence and verify equivalent install/upgrade outcomes. |

## Critical Functional Parity Hotspots

- `TorrentPolicyManager` pipeline (pre-sort tracker sync, `SortTorrents` queue ordering, free-space): implemented in Host `ProcessTorrentPolicyAsync` and `TorrentPolicyHelper`; verify against live qBit matrices.
- Free-space ordering semantics: queue-position sort key aligned with qBitrr `_torrent_queue_position_sort_key`; continue validation on real clients.
- Config migration pipeline completeness: all historical migration branches must pass fixture tests.
- DB parity and repair behavior: schema compatibility plus corruption recovery behavior must be deterministic.
- API/OpenAPI parity: dual-route contracts and auth flows need snapshot-based verification.
