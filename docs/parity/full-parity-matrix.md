# Full Parity Matrix (qBitrr -> Torrentarr)

This matrix tracks strict full parity against upstream qBitrr **5.12.3** (`0b4a111`) at the Python-module level.

## Parity claim policy

Use this file as the **source of truth** for how close implementation is to upstream. While Torrentarr **targets** qBitrr behavior and shares `config.toml` + SQLite compatibility, a **strict “100% parity”** claim is only defensible when **no** file row is `partial` and **no** support row is `missing` (per [certification-report.md](certification-report.md)). Public messaging should say **“aligned with / port of qBitrr”** or point readers here—**not** “complete parity”—until the matrix is closed out.

**Contributors:** upstream pin, test matrices, OpenAPI diffs, and internal checklists are in [contributor-reference.md](contributor-reference.md) (not needed for end users; see [overview.md](overview.md)).

Status values:

- `full`: behavior and contract are implemented and verified.
- `partial`: implementation exists but differs or lacks full verification.
- `missing`: no equivalent behavior exists yet.
- `intentional-divergence`: implementation differs by design and must prove identical user-facing outcomes.

## Runtime Package Coverage (`qBitrr/qBitrr`)

| qBitrr file | Torrentarr equivalent | Status | Required actions |
| --- | --- | --- | --- |
| `qBitrr/__init__.py` | `src/Torrentarr.Host/Program.cs`, assembly metadata | full | Version metadata via `/web/meta`; `UpdateService` reports `patched_version` semantics. |
| `qBitrr/main.py` | `src/Torrentarr.Host/Program.cs`, `ArrWorkerManager.cs` | full | Process orchestration + worker lifecycle; Lidarr search timer starvation N/A (documented in `ArrWorkerManager`). |
| `qBitrr/arss.py` | `TorrentPolicyHelper`, Host policy passes, worker services | full | **Evidence:** [`TorrentPolicyHelperTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/TorrentPolicyHelperTests.cs), [contributor-reference policy matrix](contributor-reference.md#policy-engine-test-matrix). |
| `qBitrr/qbit_category_manager.py` | `SeedingService.cs`, `CategoryPathHelper` | full | **Evidence:** [`SeedingServiceTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Services/SeedingServiceTests.cs) (HnR dead-tracker #412), subcategory matching in Host qBit categories. |
| `qBitrr/arr_tracker_index.py` | `SeedingService.cs` queue-sort tracker priority | full | Tracker priority sort in `SeedingService` + Host `ProcessTorrentPolicyAsync`. |
| `qBitrr/config.py` | `TorrentarrConfig.cs`, `ConfigurationLoader.cs` | full | Key-by-key TOML parity; `UrlBase`, `BehindHttpsProxy`, env aliases. |
| `qBitrr/gen_config.py` | `ConfigurationLoader.GenerateDefaultConfig()` | full | Defaults include `UrlBase`, `ConfigVersion = 6.12.2`. |
| `qBitrr/config_version.py` | `ConfigurationLoader.ValidateConfigVersion()` | full | `ExpectedConfigVersion = 6.12.2`; migration on load. |
| `qBitrr/env_config.py` | `ConfigurationLoader` env overrides | full | `TORRENTARR_*` + `QBITRR_*` aliases including `WEBUI_URL_BASE`, `SETUP_TOKEN`. |
| `qBitrr/duration_config.py` | `DurationParser.cs` | full | **Evidence:** [`DurationParserTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/DurationParserTests.cs). |
| `qBitrr/database.py` | `TorrentarrDbContext`, `DatabaseHealthService` | full | WAL mode, startup repair, integrity checks. |
| `qBitrr/tables.py` | EF models, `TorrentarrDbContext` | full | **Evidence:** [`SchemaParityTests.cs`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Database/SchemaParityTests.cs). |
| `qBitrr/db_lock.py` | EF/SQLite locking | full | SQLite WAL + scoped DbContext per request/worker. |
| `qBitrr/db_recovery.py` | `DatabaseHealthService`, Host `--repair-database` | full | Integrity + VACUUM + operator repair workflow. |
| `qBitrr/search_activity_store.py` | `SearchActivity` model, worker services | full | Search activity persisted and exposed via processes API. |
| `qBitrr/webui.py` | Host/WebUI `Program.cs`, `webui/src` | full | **Evidence:** UrlBase end-to-end, auth bootstrap, catalog rollups, Lidarr artists/thumbnails, [`SetPasswordEndpointTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Host.Tests/Api/SetPasswordEndpointTests.cs), [openapi.json](../assets/openapi.json) + CI drift check. |
| `qBitrr/auto_update.py` | `UpdateService`, `AutoUpdateBackgroundService` | full | Check/download/apply + cron scheduling. |
| `qBitrr/pyarr_compat.py` | `ApiClients/Arr/*.cs` | full | Arr API clients with normalized responses. |
| `qBitrr/ffprobe.py` | `MediaValidationService.cs` | full | ffprobe validation integration. |
| `qBitrr/versioning.py` | Host metadata + `UpdateService` | full | `/web/meta`, release check caching. |
| `qBitrr/bundled_data.py` | Host `wwwroot`, embedded defaults | full | SPA build output served from Host. |
| `qBitrr/home_path.py` | `ConfigurationLoader.GetDefaultConfigPath()` | full | Config search order + `GetDataDirectoryPath()`. |
| `qBitrr/logger.py` | Serilog in Host/WebUI/Workers | full | Structured logging with process metadata. |
| `qBitrr/errors.py` | Exception types across projects | full | HTTP error contracts on API endpoints. |
| `qBitrr/utils.py` | Core/Infrastructure helpers | full | Shared helpers (`CategoryPathHelper`, `UrlBaseHelper`, `ConfigValidationHelper`). |
| `qBitrr/catalog_rollups.py` (5.12.0) | `CatalogRollupService.cs` | full | **Evidence:** [`CatalogRollupServiceTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Services/CatalogRollupServiceTests.cs); wired into `/web|api/arr`, Radarr/Sonarr/Lidarr list endpoints. |
| `qBitrr/category_paths.py` (5.12.0) | `CategoryPathHelper.cs`, `ConfigValidationHelper.cs` | full | **Evidence:** [`CategoryPathHelperTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/CategoryPathHelperTests.cs), [`ConfigValidationHelperTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/ConfigValidationHelperTests.cs); wired into torrent/category matching + config save validation. |

## Support / Ops / Packaging Coverage

| qBitrr file | Torrentarr equivalent | Status | Required actions |
| --- | --- | --- | --- |
| `scripts/repair_database.py` | Host `--repair-database`, `DatabaseHealthService` | full | Operator repair via Host CLI + WebUI health. |
| `scripts/repair_database_targeted.py` | No direct equivalent | intentional-divergence | **Evidence:** [Targeted database repair](contributor-reference.md#targeted-database-repair). |
| `scripts/rebuild_and_deploy.py` | `build.sh`, CI, Docker | full | `build.sh` + GitHub Actions matrix. |
| `.github/scripts/update_releases.py` | Release workflow | intentional-divergence | **Evidence:** [Support scripts and CI](contributor-reference.md#support-scripts-and-ci). |
| `.github/autofix/auto_fix.py` | No direct equivalent | intentional-divergence | Documented CI policy divergence. |
| `setup.py` | `.csproj` + Docker | intentional-divergence | .NET publish + container images. |

## Critical Functional Parity Hotspots (5.12.3 closeout)

- **HnR dead-tracker (#412):** bare `"not found"` removed; `TrackerMessageIndicatesDead` unit tests.
- **Auth bootstrap (5.12.2):** setup token required on first password set (`WebUIAuthHelpers`, LoginPage setup field).
- **UrlBase (5.12.3):** config + `UsePathBase` + cookie path + frontend `urlBase.ts`.
- **Catalog rollups (5.12.0):** `available = monitored AND has_file`; 5s TTL cache.
- **Lidarr artists + thumbnails (5.12.0):** `ArrCatalogEndpoints` + `ArrThumbnailService` + frontend API client.
- **OpenAPI drift guard:** `scripts/check-openapi-drift.sh` in CI vs qBitrr `5.12.3`.
- **Config schema:** Torrentarr `6.12.2` (+1 major vs qBitrr `5.12.2`).
