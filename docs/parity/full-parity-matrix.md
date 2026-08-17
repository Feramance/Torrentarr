# Full Parity Matrix (qBitrr -> Torrentarr)

This matrix tracks the current parity audit against upstream qBitrr **v5.14.3-1** (`EXPECTED_CONFIG_VERSION = "5.14.3"`). Torrentarr’s matching schema is **6.14.3** (+1 major). There is no Torrentarr 6.13.x release; configs on `6.12.*` migrate forward in one jump.

## Parity claim policy

Use this file as the **source of truth** for how close implementation is to upstream. A strict latest-main parity claim is only defensible when **no** file row is `partial` and **no** support row is `missing`. Rows marked `intentional-divergence` document architectural differences with equivalent user-facing outcomes.

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
| `qBitrr/main.py` | `src/Torrentarr.Host/Program.cs`, `ArrWorkerManager.cs`, `QBitCategoryWorkerManager.cs`, `PeriodicWalCheckpointService.cs` | partial | Core orchestration is implemented. Latest-main follow-up needed for the qBitrr `5.12.8-5.12.10` process/session fixes because Torrentarr uses a different architecture and has not been re-certified against those upstream deltas yet. |
| `qBitrr/arss.py` | `TorrentPolicyHelper`, `CategoryOwnershipHelper`, `TorrentProcessor`, worker services | partial | Core policy/state-machine coverage is broad, but latest-main import completion semantics changed in qBitrr `5.12.7`. Torrentarr now waits for Arr confirmation before persisting `Imported = true`, and that path needs fresh regression coverage plus latest-main re-certification. |
| `qBitrr/qbit_category_manager.py` | `QBitCategoryWorkerManager.cs`, `SeedingService.cs`, `CategoryOwnershipHelper.cs` | full | **Evidence:** qBit-only `ManagedCategories` workers; `MatchSubcategories`; rate limits via `ApplySeedingLimitsAsync`; [`CategoryOwnershipHelperTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/CategoryOwnershipHelperTests.cs). |
| `qBitrr/arr_tracker_index.py` | `SeedingService.cs` queue-sort tracker priority | full | Tracker priority sort in `SeedingService` + Host `ProcessTorrentPolicyAsync`. |
| `qBitrr/config.py` | `TorrentarrConfig.cs`, `ConfigurationLoader.cs` | full | Key-by-key TOML parity including `MatchSubcategories`; `UrlBase`, `BehindHttpsProxy`, env aliases; hot reload restarts workers on Host. |
| `qBitrr/gen_config.py` | `ConfigurationLoader.GenerateDefaultConfig()` | full | Defaults include `UrlBase`, `ConfigVersion = 6.14.3`, `AutoUpdateChannel`, `[Readarr-*]`. |
| `qBitrr/config_version.py` | `ConfigurationLoader.ValidateConfigVersion()` | full | `ExpectedConfigVersion = 6.14.3`; migration on load. |
| `qBitrr/env_config.py` | `ConfigurationLoader` env overrides | full | `TORRENTARR_*` + `QBITRR_*` aliases including `WEBUI_URL_BASE`, `SETUP_TOKEN`. |
| `qBitrr/duration_config.py` | `DurationParser.cs` | full | Integer and fractional TOML durations (`1.5`, `1.5h`). **Evidence:** [`DurationParserTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Core.Tests/Configuration/DurationParserTests.cs). |
| `qBitrr/database.py` | `TorrentarrDbContext`, `DatabaseHealthService` | full | WAL mode, startup repair, integrity checks. |
| `qBitrr/tables.py` | EF models, `TorrentarrDbContext`, `ManualSqliteMigrations` | full | Includes Readarr `bookfilesmodel` / `authorfilesmodel` / `bookqueuemodel` on new **and** existing DBs. **Evidence:** [`SchemaParityTests.cs`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Database/SchemaParityTests.cs), [`ManualSqliteMigrationsTests.cs`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Database/ManualSqliteMigrationsTests.cs). |
| `qBitrr/db_lock.py` | EF/SQLite WAL, `DatabaseRetryExtensions.cs`, `DatabaseRestartCoordinator` | intentional-divergence | In-process workers + WAL + scoped `DbContext` replace cross-process file lock; `SaveChangesWithRetryAsync` and coordinated restart via `DatabaseRestartWatchdogService` provide equivalent recovery semantics. |
| `qBitrr/db_recovery.py` | `DatabaseHealthService`, Host `--repair-database`, `PeriodicWalCheckpointService` | full | Integrity + VACUUM + `RepairAsync` via SQLite backup; periodic WAL checkpoint every 5 minutes on Host. |
| `qBitrr/search_activity_store.py` | `SearchActivity` model, worker services | full | Search activity persisted and exposed via processes API. |
| `qBitrr/webui.py` | Host/WebUI `Program.cs`, `webui/src`, `docs/assets/openapi.json` | partial | Host/WebUI route surface remains broad, and the SPA already reloads after UrlBase-sensitive config saves. OpenAPI/tooling have been rebased to latest `master`, but the route diff and latest-main behavior checks still need re-certification after the upstream pin move. |
| `qBitrr/auto_update.py` | `UpdateService`, `AutoUpdateBackgroundService` | full | `AutoUpdateChannel` (`latest`/`stable`/`nightly`); source-build apply disabled. |
| `qBitrr/pyarr_compat.py` | `ApiClients/Arr/*.cs`, `HttpRetryHelper.cs` | full | Radarr/Sonarr/Lidarr/Readarr clients with normalized responses and retry policies. |
| `qBitrr/ffprobe.py` | `MediaValidationService.cs` | full | ffprobe validation; ebook/comic suffixes skip probe. |
| `qBitrr/versioning.py` | Host metadata + `UpdateService` | full | `/web/meta`, release check caching. |
| `qBitrr/bundled_data.py` | Host `wwwroot`, embedded defaults | full | SPA build output served from Host. |
| `qBitrr/home_path.py` | `ConfigurationLoader.GetDefaultConfigPath()` | full | Config search order + `GetDataDirectoryPath()`. |
| `qBitrr/logger.py` | Serilog in Host/WebUI/Workers | full | Structured logging with process metadata. |
| `qBitrr/errors.py` | Exception types across projects | full | HTTP error contracts on API endpoints. |
| `qBitrr/utils.py` | Core/Infrastructure helpers, `HttpRetryHelper.cs` | full | `with_retry` parity on Arr/qBit HTTP; helpers (`CategoryPathHelper`, `CategoryOwnershipHelper`, `UrlBaseHelper`, `ConfigValidationHelper`). |
| `qBitrr/catalog_rollups.py` (5.12.0) | `CatalogRollupService.cs` | full | **Evidence:** [`CatalogRollupServiceTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Services/CatalogRollupServiceTests.cs); wired into `/web|api/arr`, Radarr/Sonarr/Lidarr/Readarr list endpoints. |
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

## Latest-Main Delta Audit (5.12.4 -> 5.12.10)

| Upstream delta | Torrentarr status | Notes |
| --- | --- | --- |
| `5.12.5` multi-instance delete retry / recheck routing | partial | Torrentarr routes qBit actions by `QBitInstanceName`, but the latest-main qBitrr fixes have not been fully re-certified under the newer baseline. |
| `5.12.5` WebUI UrlBase cache refresh | full | Torrentarr already reloads the SPA after UrlBase-sensitive config saves in `webui/src/App.tsx`. |
| `5.12.6` multi-instance delete/pause routing and queue scoping | partial | Architecture differs from qBitrr's per-instance queue dictionaries, so parity depends on targeted Torrentarr tests rather than code-shape equivalence. |
| `5.12.6` pause/resume placeholder queue initialization | intentional-divergence | Torrentarr does not mirror qBitrr's placeholder queue dictionaries; it routes directly through per-torrent `QBitInstanceName`. |
| `5.12.7` mark imported only after successful Arr scan | full | Fixed in `TorrentProcessor` by delaying `Imported = true` until `IArrImportService.IsImportedAsync()` confirms the item has left Arr's queue, with regression coverage in `TorrentProcessorTests`. |
| `5.12.8` placeholder pause/resume queues follow-up | intentional-divergence | Same reasoning as `5.12.6`; no equivalent dictionary layer exists in Torrentarr. |
| `5.12.9` further fixes | partial | Upstream changelog is too broad to claim automatic parity; needs targeted review when specific behaviors are identified. |
| `5.12.10` qBit session sharing across forked workers | intentional-divergence | qBitrr fix addresses forked Python workers. Torrentarr uses separate .NET clients/caches rather than inherited fork state, so the exact bug class does not apply. |

## Latest-Main Delta Audit (5.12.11 -> 5.14.3)

| Upstream delta | Torrentarr status | Notes |
| --- | --- | --- |
| `5.12.11` metadata stalled / DB recovery (#497) | full | Existing `TorrentProcessor` stalled handling + `DatabaseHealthService`; no extra user-facing delta vs Torrentarr 6.12.4. |
| `5.12.12` qBit init recovery | full | Host orchestrator and Workers retry failed qBit instances; WebUI stays up. |
| `5.12.12` Overseerr TV TMDB for release-date lookup | intentional-divergence | qBitrr calls Overseerr `GET /api/v1/tv/{tmdbId}` for today's-release dates. Torrentarr uses Sonarr `AirDateUtc` and does not call that Overseerr detail API. N/A — no series `TmdbId` column. |
| `5.13.0` `AutoUpdateChannel` | full | `latest` / `stable` / `nightly`; nightly is check-only; source/`dotnet run` never applies binaries. |
| `5.13.0` fractional duration parse | full | TOML floats and `1.5h`-style suffixes. |
| `5.13` Lidarr LIVE / year-search isolation (#516, #528) | full | Candidate loop failures are isolated so one Arr type cannot kill the worker. |
| `5.13.0` WebUI empty-state / collapsed qBit categories | intentional-divergence | Upstream polish not ported; not required for behavioral parity. |
| `5.14.0` Readarr | full | Config, client, DB tables (manual migration on existing files), sync/import/search/profiles, catalog UI (authors + books, no tracks). |
| `5.14.0` Pathos dedicated-qBit-client gate | intentional-divergence | Python multiprocessing concern; Torrentarr workers already isolate qBit clients. |
| `5.14.1`–`5.14.3` Readarr allowlist / audiobook save (#550) | full | Type-aware defaults; ebook-only default expansion; WebUI save keeps `.m4b`/`.flac`. |
| `5.14.2` tracker `-1` merge with Arr/CategorySeeding | full | Unlimited only when no source sets a positive limit. **Evidence:** [`SeedingLimitMergeTests`](https://github.com/Feramance/Torrentarr/blob/master/tests/Torrentarr.Infrastructure.Tests/Services/SeedingLimitMergeTests.cs). |
| Docker `stable` / `latest` / `nightly` image channels | intentional-divergence | Torrentarr still ships `feramance/torrentarr:latest`. |
| `SkipTLSVerify` on Arr/qBit sections | missing | Present in qBitrr `config.example.toml` for all Arr types; optional follow-up, not Readarr-only. |

## Critical Functional Parity Hotspots

- **HnR dead-tracker (#412):** bare `"not found"` removed; `TrackerMessageIndicatesDead` unit tests.
- **Auth bootstrap (5.12.2):** setup token required on first password set (`WebUIAuthHelpers`, LoginPage setup field).
- **UrlBase:** config + `UsePathBase` + cookie path + frontend `urlBase.ts`.
- **Catalog rollups (5.12.0):** `available = monitored AND has_file`; 5s TTL cache.
- **Lidarr artists + thumbnails (5.12.0):** `ArrCatalogEndpoints` + `ArrThumbnailService` + frontend API client.
- **Readarr authors + books (5.14.0):** `ArrCatalogEndpoints` + `ReadarrView` (no track table).
- **OpenAPI drift guard:** `scripts/check-openapi-drift.sh` in CI vs qBitrr latest `master` (overrideable via `QBITRR_OPENAPI_REF`).
- **Config schema:** Torrentarr `6.14.3` (+1 major vs qBitrr `5.14.3` line).
- **Latest-main follow-up:** import-completion confirmation is fixed; multi-instance routing (5.12.5–5.12.9) remains `partial` unless targeted delete/pause/recheck tests land. Do not claim 100% parity while those rows remain `partial`.
