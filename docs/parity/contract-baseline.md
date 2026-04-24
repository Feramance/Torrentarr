# Parity Contract Baseline

This document defines the contract baselines Torrentarr must match to claim strict full parity with upstream qBitrr master.

**Intentional product differences** and **pinned upstream** ref for maintainers: [contributor-reference.md](contributor-reference.md) (end users: [overview.md](overview.md)).

## 1) Configuration Contract Baseline

Authoritative upstream sources (upstream qBitrr repository paths):

- `qBitrr/qBitrr/config.py`
- `qBitrr/qBitrr/gen_config.py`
- `qBitrr/qBitrr/config_version.py`
- `qBitrr/qBitrr/env_config.py`

Torrentarr implementation surface:

- `src/Torrentarr.Core/Configuration/TorrentarrConfig.cs`
- `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs`

Required parity guarantees:

- Equivalent key set and defaults for all supported config sections.
- Equivalent version/migration behavior for old/new configs.
- Equivalent environment override behavior and precedence.
- Equivalent serialization safety behavior for TOML edge cases.

## 2) Database Contract Baseline

Authoritative upstream sources (upstream qBitrr repository paths):

- `qBitrr/qBitrr/tables.py`
- `qBitrr/qBitrr/database.py`
- `qBitrr/qBitrr/db_recovery.py`
- `qBitrr/qBitrr/db_lock.py`

Torrentarr implementation surface:

- `src/Torrentarr.Infrastructure/Database/TorrentarrDbContext.cs`
- `src/Torrentarr.Infrastructure/Database/Models/*.cs`
- `src/Torrentarr.Infrastructure/Services/DatabaseHealthService.cs`
- `src/Torrentarr.Host/Program.cs`

Required parity guarantees:

- Equivalent table/column/index contract and uniqueness constraints.
- Compatible startup behavior with existing DB files.
- Equivalent integrity-check and repair flows.
- Equivalent multi-process read/write safety guarantees.

## 3) Policy Engine Baseline (Free Space + Tracker Sort)

Authoritative upstream source (upstream qBitrr repository path):

- `qBitrr/qBitrr/arss.py` (`TorrentPolicyManager`, tracker sort, free-space simulation)

Torrentarr implementation surface:

- `src/Torrentarr.Core/Configuration/TorrentPolicyHelper.cs` (gating + queue keys; mirrors `TorrentPolicyManager` flags)
- `src/Torrentarr.Host/Program.cs` (`ProcessTorrentPolicyAsync`, `ProcessFreeSpaceManagerAsync`, tracker sort)
- `src/Torrentarr.Infrastructure/Services/FreeSpaceService.cs`
- `src/Torrentarr.Infrastructure/Services/SeedingService.cs` (queue-sort priority + tracker actions)
- `src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs` (skips duplicate tracker sync when policy owns it)

Required parity guarantees:

- Equivalent monitored-category scope.
- Equivalent queue ordering algorithm and pause/resume boundaries.
- Equivalent tracker-priority sort behavior (`SortTorrents`).
- Equivalent ownership semantics between policy engine and worker tracker sync.

## 4) Web/API Contract Baseline

Authoritative upstream sources (upstream qBitrr repository paths):

- `qBitrr/qBitrr/webui.py`
- `qBitrr/docs/webui/api.md`

Torrentarr implementation surface:

- `src/Torrentarr.Host/Program.cs`
- `src/Torrentarr.WebUI/Program.cs`
- `webui/src`
- `docs/assets/openapi.json`

Periodic drift check vs upstream: [OpenAPI alignment](contributor-reference.md#openapi-alignment) in [contributor-reference.md](contributor-reference.md).

Required parity guarantees:

- Equivalent route coverage (`/api/*` and `/web/*` semantics).
- Equivalent auth behavior (disabled/local/OIDC).
- Equivalent request/response payload schemas.
- Equivalent UI workflows and process/state surfaces.

## 5) Verification Baseline

Parity claim gate:

- Every qBitrr Python file in `docs/parity/full-parity-matrix.md` has a final status with evidence.
- Critical behavior paths are covered by deterministic automated tests.
- API/OpenAPI and migration fixtures are snapshot-verified in CI.
- Docs describe implemented behavior with no known drift.

**Public messaging:** The index page, README, and release notes should **not** use phrases like “complete” or “100% parity” with qBitrr until the matrix has no `partial` or `missing` module rows. Prefer **“C# port targeting qBitrr”** and link to the matrix for status.
