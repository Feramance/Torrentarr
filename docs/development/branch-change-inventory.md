> This inventory predates the Storage cleanup redesign. For current maintenance behavior and migration, see [Storage cleanup](../features/torrent-maintenance.md).

# Branch change and documentation inventory

Reconciled on 2026-10-21 for `codex/webui-parity-overhaul`. Merge base with
origin/master: `5985be9de3ef99eddee141c998f231521c0ab279`. Branch HEAD at inventory: `504420e43b82d6977e3e9f6c15f0192d3262ca8a`.
Includes branch commits, staged/unstaged changes and untracked files. Build output,
ignored local config, credentials and private torrent evidence are excluded.

## User-facing behavior checklist

| Change | Current documentation / rationale |
| --- | --- |
| Uniform client instances; canonical and legacy paths, category ownership, no primary client | [Configuration](../configuration/index.md), [file reference](../configuration/config-file.md), [API](../webui/api.md) |
| Per-client DownloadPath, path translation and explicit mounts; equal clients | [Editor](../webui/config-editor.md), [maintenance configuration](../configuration/maintenance.md), [path troubleshooting](../troubleshooting/path-mapping.md) |
| Shared-filesystem free-space budgets, user pause ownership, migration precedence and ambiguous monitors | [Disk space](../features/disk-space.md), [migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), config.example.toml |
| Safe config publication, pending/applied acknowledgments, retired connections, coordinated renames and recovery journal | [Editor](../webui/config-editor.md), [API](../webui/api.md), [maintenance](../features/torrent-maintenance.md) |
| Outcome-first maintenance protection dashboard; Recover/Organize/Clean up tool groups; practical examples; contextual checks, drafts, scheduling, cancellation, expiry and caps | [Maintenance](../features/torrent-maintenance.md), [complete controls](../configuration/maintenance-controls.md), [API](../webui/api.md) |
| Every maintenance scalar/list/rule collection; independent retention, explicit roots, owned tag clearing, recovery controls | Generated [control reference](../configuration/maintenance-controls.md), [examples](../configuration/maintenance.md), config.example.toml; reflection/generator checks enforce coverage |
| Shared protected/eligible/unknown outcomes, no dead-message bypass, all obligations and fresh checks | [Seeding](../configuration/seeding.md), [maintenance](../features/torrent-maintenance.md), [current overview](../index.md) |
| TorrentLeech Power User eight-day/ratio rule and partial/freeleech handling | [Seeding rules and sources](../configuration/seeding.md); client ratio alone defaults to disabled |
| OnlyEncodes mandatory completion-based 48 hours; uncertain 168-hour warning deadline; account ratio separate | [Seeding](../configuration/seeding.md); user-supplied rule provenance and uncertainty retained |
| Deadline boundaries, margins, accounting grace, reserve, ETA uncertainty and missed-deadline protection | [Seeding](../configuration/seeding.md), [maintenance](../features/torrent-maintenance.md), generated controls |
| Durable recovery, observations, owned pauses/native limit restoration and additive SQLite state | [Database](../advanced/database.md), [maintenance](../features/torrent-maintenance.md), [rollback guidance](../getting-started/quickstart.md) |
| Conservative schedules, two inspection detail requests/client, recheck caps, backoff and cursors | [Maintenance](../features/torrent-maintenance.md), [advanced guidance](../advanced/index.md); orphan name enumeration remains a documented IO limitation |
| Arr catalogs, list/grid, rollups, safe external links and request/quality metadata | [Arr views](../webui/arr-views.md), [WebUI API](../webui/api.md); historical visual/parity reports retained |
| Logs filtering/search/live streaming, table state and theme/density settings | [API](../webui/api.md), [logs](../webui/logs.md), [WebUI](../webui/index.md) |
| Process cards/heartbeats, category filtering and restart keys | [Processes](../webui/processes.md), [API](../webui/api.md) |
| Invalid HTML thumbnail rejection and cache recovery | [API](../webui/api.md), [Arr views](../webui/arr-views.md) |
| Embedded assets, frontend-before-Host build order and Docker rebuild/recreate | [Quick start](../getting-started/quickstart.md), [verification](maintenance-verification.md), [Docker installation](../getting-started/installation/docker.md) |
| OpenAPI additions distinct from pinned upstream reference; schema/docs generation | [API](../webui/api.md), [verification](maintenance-verification.md), generator scripts and contract tests |
| Test harnesses, fixtures, source-only refactors and icons | No independent operator instructions needed; tests verify the associated documented behavior |
| Historical review/parity reports | Kept historical; current behavior is described in current guides and this inventory, rather than rewriting prior audit findings |

## Verification scope and limits

The outcome-first redesign verification uses synthetic data and performs no deployment, live torrent mutations, tracker-account actions, scraping or point spending. An earlier revision was deployed separately at the user’s request; this redesign is not deployed. Named torrents were investigated read-only; current client
records cannot establish historical tracker credit or the precise cause of warnings.
The pictured OnlyEncodes Hybrid release was not present in the current client list;
a different Superman release was not treated as equivalent evidence.

The orphan inspection cap bounds file eligibility checks, but ordering directory
names still enumerates the root. This is a known load-control limitation on very
large roots. External operations can still race remote/file changes; protection
preserves uncertainty rather than promising atomic client/filesystem operations.

Verification commands and invariants are in [maintenance verification](maintenance-verification.md).
Current results are recorded below after the final runs; older report test counts
are historical. No release version is invented for these changes.

## File-by-file reconciliation

Each path below is mapped to current behavior documentation or an explicit
no-independent-documentation rationale. Sources: B branch commit, S staged, W unstaged,
N new/untracked. Documentation links are repository-relative to this page.

| Changed path | Sources | Documentation or rationale |
| --- | --- | --- |
| `.gitignore` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `README.md` | BW | Operator entry point/example updated; [configuration](../configuration/index.md), [maintenance](../features/torrent-maintenance.md). |
| `config.example.toml` | W | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `docker-compose.yml` | SW | [Quick start/build order](../getting-started/quickstart.md), [verification](maintenance-verification.md), [installation](../getting-started/installation/index.md). |
| `docs/advanced/database.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/advanced/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/assets/maintenance-example-synthetic.png` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/assets/maintenance-mobile-synthetic.png` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/assets/maintenance-overview-synthetic.png` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/assets/maintenance-results-synthetic.png` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/assets/openapi.json` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/arr/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/arr/lidarr.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/arr/radarr.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/arr/sonarr.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/config-file.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/maintenance-controls.md` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/maintenance.md` | BW | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/qbittorrent.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/quality-profiles.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/search/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/seeding.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/torrents.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/configuration/webui.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/development/contributing.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/development/maintenance-verification.md` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/development/pr-520-review-follow-up.md` | B | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/development/testing.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/development/webui-parity.md` | B | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/development/webui-source-reconciliation.md` | B | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/development/webui-visual-checks.json` | B | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/faq.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/auto-updates.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/automated-search.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/custom-formats.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/disk-space.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/health-monitoring.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/instant-imports.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/maintenance-examples.md` | N | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/process-management.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/quality-upgrades.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/features/torrent-maintenance.md` | BW | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/getting-started/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/getting-started/installation/docker.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/getting-started/installation/systemd.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/getting-started/migration.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/getting-started/quickstart.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/parity/full-parity-matrix.md` | W | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/parity/full-parity-path-report.md` | W | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/parity/pr-504-review-follow-up.md` | B | Current documentation/artifact; historical audit records retain their original findings. |
| `docs/troubleshooting/common-issues.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/troubleshooting/debug-logging.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/troubleshooting/docker.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/troubleshooting/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/troubleshooting/path-mapping.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/troubleshooting/performance.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/webui/api.md` | BW | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/webui/config-editor.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `docs/webui/index.md` | W | Current guidance or generated API/control documentation; navigation links updated. |
| `mkdocs.yml` | BW | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `scripts/check-docs.py` | N | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `scripts/check-openapi-drift.py` | W | [API contract and generation](../webui/api.md), [verification](maintenance-verification.md). |
| `scripts/generate-openapi-from-qbitrr.py` | W | [API contract and generation](../webui/api.md), [verification](maintenance-verification.md). |
| `scripts/openapi_maintenance.py` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `scripts/smoke-standalone.ps1` | B | [Quick start/build order](../getting-started/quickstart.md), [verification](maintenance-verification.md), [installation](../getting-started/installation/index.md). |
| `scripts/smoke-standalone.sh` | B | [Quick start/build order](../getting-started/quickstart.md), [verification](maintenance-verification.md), [installation](../getting-started/installation/index.md). |
| `scripts/webui-parity-preview.py` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `src/Torrentarr.Core/Configuration/ConfigMutationGuard.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/ConfigRenameHelper.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/ConfigSchemaBuilder.cs` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/ConfigValidationHelper.cs` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/ConfigurationSnapshot.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/FreeSpaceThreshold.cs` | B | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Configuration/MaintenanceConfigValidation.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Configuration/MaintenancePolicies.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Configuration/StorageConfigMigration.cs` | N | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Configuration/TorrentPolicyHelper.cs` | W | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/TorrentarrConfig.cs` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Configuration/TrackerProtectionValidation.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Interfaces/IProcessOrchestrator.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Interfaces/ITorrentClient.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Models/MaintenanceModels.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Models/ProtectionDecision.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Models/TorrentClientModels.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Models/TorrentInfo.cs` | BW | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Models/WorkerHeartbeat.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Services/IConfigReloader.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Core/Services/IFreeSpaceService.cs` | W | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Core/Services/IMaintenanceServices.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Core/Services/MaintenanceRequirements.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Host/AutoUpdateBackgroundService.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Host/Program.cs` | BW | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Host/UpdateService.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/ApiClients/Arr/LidarrClient.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/ApiClients/QBittorrent/QBittorrentClient.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Database/ManualSqliteMigrations.cs` | W | [Database](../advanced/database.md), [maintenance recovery](../features/torrent-maintenance.md). |
| `src/Torrentarr.Infrastructure/Database/Models/MaintenanceState.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Database/Models/TrackFilesModel.cs` | B | [Database](../advanced/database.md), [maintenance recovery](../features/torrent-maintenance.md). |
| `src/Torrentarr.Infrastructure/Database/TorrentarrDbContext.cs` | W | [Database](../advanced/database.md), [maintenance recovery](../features/torrent-maintenance.md). |
| `src/Torrentarr.Infrastructure/Endpoints/ArrCatalogEndpoints.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Endpoints/LogFileApi.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/ArrSyncService.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/ArrThumbnailService.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/ArrWorkerManager.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/ClientDownloadPaths.cs` | N | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/ConfigMutationMiddleware.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Infrastructure/Services/ConfigReloader.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Infrastructure/Services/FreeSpaceInventory.cs` | W | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/FreeSpaceService.cs` | BW | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/InstanceRenamePersistence.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Infrastructure/Services/LiveConfigurationRuntime.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Infrastructure/Services/LiveWebConfiguration.cs` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `src/Torrentarr.Infrastructure/Services/MaintenanceCoordinator.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/MaintenanceNotificationService.cs` | B | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/MaintenancePersistence.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/MaintenanceRecovery.cs` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/PathMappingService.cs` | B | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/QBitCategoryEnsureService.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/QBitCategoryWorkerManager.cs` | BW | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/QbitOverviewBuilder.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/RegisteredTorrentClient.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/SafeDeletionService.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/SeedingService.cs` | W | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/TorrentClientApiBuilder.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/TorrentInventoryService.cs` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs` | W | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Infrastructure/Services/WorkerProcessSupervisor.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.WebUI/Program.cs` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `src/Torrentarr.Workers/Program.cs` | BW | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `tests/Torrentarr.Core.Tests/Configuration/ConfigMutationGuardTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/ConfigRenameHelperTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/ConfigSchemaBuilderTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/ConfigurationLoaderTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/MaintenanceCompatibilityTests.cs` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/MaintenanceConfigValidationTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/StorageMigrationTests.cs` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/TorrentClientMaintenanceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/TorrentPolicyHelperTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Configuration/TrackerProtectionTests.cs` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Core.Tests/Models/TorrentInfoTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/ArrTestConnectionEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/ConfigEndpointTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/ConfigSchemaAndOverviewEndpointTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/ConfigurationEventEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/EmbeddedWebAssetEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/LidarrArtistsEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/MaintenanceEndpointTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/PathMappingSaveTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/ProcessesEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/StandaloneRadarrEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/Api/UrlBaseEndpointTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/ProcessOrchestratorServiceTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Host.Tests/UpdateServiceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Database/SchemaParityTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/ArrSyncSearchProgressTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/ArrThumbnailServiceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/ConfigReloaderTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/FreeSpaceReviewTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/FreeSpaceServiceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/InstanceRenamePersistenceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/LiveConfigurationRuntimeTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/LiveWebConfigurationTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/LogFileApiTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/MaintenanceSafetyTests.cs` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/PathMappingServiceTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/ProtectionLimitTests.cs` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/QbitOverviewBuilderTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/SeedingServiceTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/SharedStorageTests.cs` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/TaglessInstanceScopeTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/WorkerLifecycleTests.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/Services/WorkerSetupTests.cs` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/Torrentarr.Infrastructure.Tests/TestPaths.cs` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tests/scripts/test_check_openapi_drift.py` | W | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `tools/Torrentarr.ConfigSchemaGenerator/Program.cs` | W | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/fixtures/parity.json` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/index.html` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/public/sw.js` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/App.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/AppShell.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/AuthGate.tsx` | B | [Authentication](../configuration/webui-authentication.md), [API](../webui/api.md). |
| `webui/src/__tests__/WebUIContext.events.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/api/client.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/components/StableTable.store.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/MaintenanceConfig.test.tsx` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/categoryConfigValidation.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/configFieldsGenerated.test.ts` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/configGroups.test.ts` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/maintenanceOperations.test.ts` | N | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/config/torrentHandlingSummary.test.ts` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/App.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/AppShell.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/ArrCatalogView.failure.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/ArrConnectionModal.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/AuthGate.storage.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/ConfigView.test.tsx` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/LogsView.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/MaintenanceView.test.tsx` | BW | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/ProcessesView.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/QbitCategoriesView.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/RadarrView.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/pages/ReadarrView.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/reviewRegressions.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/serviceWorker.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/__tests__/setup.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/api/client.ts` | BW | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/api/clientConfigErrors.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/api/clientTtl.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/api/types.ts` | BW | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/api/urlBase.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/components/ChangelogModal.tsx` | B | [Logs](../webui/logs.md), [API](../webui/api.md). |
| `webui/src/components/CopyButton.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/components/QbitTorrentListRow.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/components/StableTable.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/components/TagInput.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/components/arr/ArrExternalLink.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/components/arr/ArrPosterImage.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/components/arr/LidarrAlbumDetailBody.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/components/arr/ReadarrAuthorDetailBody.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/components/arr/ReadarrBookDetailBody.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/config/categoryConfigValidation.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/config/categoryPaths.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/config/configFields.generated.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/config/maintenanceOperations.ts` | N | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `webui/src/config/reactSelectTheme.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/config/tooltips.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/config/torrentHandlingSummary.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/constants/arrCatalogMessages.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/context/ToastContext.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/context/WebUIContext.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/context/useWebUI.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/context/webUISettingSetters.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/hooks/useArrBrowseMode.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/icons/build.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/icons/gear.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/icons/log.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/icons/readarr.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/icons/refresh-arrow.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/icons/up-arrow.svg` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/src/main.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/pages/ArrCatalogView.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/ConfigView.tsx` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/LoginPage.tsx` | B | [Logs](../webui/logs.md), [API](../webui/api.md). |
| `webui/src/pages/LogsView.tsx` | B | [Logs](../webui/logs.md), [API](../webui/api.md). |
| `webui/src/pages/MaintenanceView.tsx` | BW | [Maintenance](../features/torrent-maintenance.md), [controls](../configuration/maintenance-controls.md), [seeding](../configuration/seeding.md), [API](../webui/api.md). |
| `webui/src/pages/ProcessesView.tsx` | B | [Processes](../webui/processes.md), [configuration publication](../webui/config-editor.md), [maintenance safeguards](../features/torrent-maintenance.md). |
| `webui/src/pages/QbitCategoriesView.tsx` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/pages/ReadarrView.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/ArrCatalogListOrGrid.test.tsx` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/pages/arrCatalog/ArrCatalogListOrGrid.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/ArrCatalogShell.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/ArrCatalogStandardBody.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/definition.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/forEachInstanceChunkedPages.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/getArrCatalogDefinition.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/lidarrDefinition.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/radarrDefinition.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/readarrDefinition.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/registry.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/sonarrDefinition.tsx` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/arrCatalog/useAggregateCatalogLoader.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/pages/config/PathMappingsEditor.tsx` | BW | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `webui/src/pages/config/configDocumentUtils.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configFieldComponents.tsx` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configFieldOverlays.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configFields.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configGroups.ts` | N | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configModals.tsx` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configSaveResult.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configTypes.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/configValidation.ts` | BW | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |
| `webui/src/pages/config/pathMappingConfig.ts` | BW | [Disk space](../features/disk-space.md), [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors), [editor](../webui/config-editor.md). |
| `webui/src/styles.css` | BW | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/tableCore.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/utils/arrThumbnailUrl.ts` | B | [Arr views](../webui/arr-views.md), [API](../webui/api.md), [editor](../webui/config-editor.md). |
| `webui/src/utils/formatVersionLabel.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/utils/navigateTab.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/src/utils/qbitTorrentDisplay.test.ts` | B | No independent operator documentation needed: isolated verification/fixtures for the associated behavior. |
| `webui/src/utils/qbitTorrentDisplay.ts` | B | [WebUI](../webui/index.md), [API](../webui/api.md), [editor](../webui/config-editor.md); no separate operator contract for internal support/refactoring. |
| `webui/tsconfig.app.json` | B | No independent operator documentation needed: visual/build support without a separate configuration contract. |
| `webui/vite.config.ts` | B | [Configuration](../configuration/index.md), [editor](../webui/config-editor.md), [API](../webui/api.md), [verification](maintenance-verification.md). |

## Earlier verification before the previous deployment — 2026-10-21

- Full non-live backend suite: Core 340, Infrastructure 940, Host API 359 passing. After the final native-limit restoration adjustment, all three focused native-limit cases passed; the final schema clarification passed all 14 schema tests.
- Frontend: 50 files, 356 tests passing with two workers and a 20-second per-test allowance. Initial five-second timeout failures under concurrent build load were rerun successfully; the longer allowance changes no production behavior.
- Production frontend assets were built before the Release Host. The Release build passed without warnings or errors.
- Generated configuration schema/control documentation check and pinned upstream/Torrentarr OpenAPI drift checks passed, including all 24 API-checker regression tests and exact generated maintenance models/operations.
- All 728 current TOML examples passed syntax checks and the project-loader test. Strict MkDocs and 20,003 internal links/anchors passed. Git revision metadata was disabled for the local strict build because new untracked documentation has no commit history; default CI metadata remains enabled.
- Client, Arr, Settings and Authentication dialogs were visually inspected at desktop and 390-pixel widths, along with populated maintenance results, blockers, pagination and delayed-inspection status. All previews used isolated synthetic data.
- The inventory reconciles 279 changed paths against the recorded merge base. Existing storage/config work and historical audit reports were preserved. No deployment, live mutation or tracker-account action was performed.

Known limits remain explicit: tracker acceptance and uninterrupted service cannot be guaranteed; missing evidence preserves protection. Orphan root name ordering still enumerates directory entries before the file eligibility-inspection cap. The pictured OnlyEncodes Superman Hybrid release was absent from the current client, so its historical warning cause remains unconfirmed.


## Outcome-first redesign follow-up — 2026-10-21

The current page opens with seeding protection, compact tool groups and recent
results. Tools explain their benefit, example, consequences and requirements before
settings. Shared settings appear separately; the dashboard uses one policy enable
control while client configuration retains editable legacy flags. Check now and
Apply N changes once are contextual; Scheduling keeps session and persistent
authorization separate. No backend maintenance algorithms or API endpoints changed.

The [cookbook](../features/maintenance-examples.md) contains ten complete validated
manual-only TOML examples, expected checks, resulting changes, blockers, four
practical walkthroughs, separate HnR scenarios and synthetic desktop/mobile images.
Descriptions are shared with the client configuration editor. README, configuration,
editor guidance and navigation point at current behavior and examples.

Verification: the full frontend suite, focused workflow tests, 20 Core schema/docs
checks (including all current TOML through the project loader and every cookbook
policy’s validation/manual mode), production build, generated-schema check, 24 API
checker tests, pinned upstream/maintenance API drift check, strict MkDocs and internal
links are required. Final run results are recorded after the final checks below.
No live mutation or deployment is included in this follow-up.

Final follow-up results: 51 frontend files / 366 tests passed, including contextual
controls, draft retention, manual/session/automatic separation, single enable
control, keyboard tabs and truthful result states. All 44 focused maintenance,
shared-description and config-editor tests also passed after the accessible label
fix. Twenty Core schema/documentation checks passed, covering all 738 current TOML
examples and validating one manual-only cookbook policy for every tool. Targeted
ESLint, production frontend and Release Host builds passed; the Host build reported
zero warnings and errors. Generated-schema and pinned/generated-maintenance API
checks passed, including 24 API-checker regression tests. Strict MkDocs and 20,477
internal links/anchors passed with local Git date metadata disabled for untracked
pages. Desktop and 390-pixel layouts were inspected with populated synthetic
results in dark/light themes; the captured walkthroughs remain synthetic examples.
The inventory now reconciles 285 changed paths. No live torrent actions or deployment
were performed for this redesign.
