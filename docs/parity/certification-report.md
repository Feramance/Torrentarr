# Parity Certification Report

## Scope

This report captures Torrentarr's parity status after rebasing the audit from qBitrr **v5.12.10** through **v5.14.4-1**. Torrentarr schema is **6.14.4**.

Primary tracking artifacts:

- `docs/parity/full-parity-matrix.md`
- `docs/parity/full-parity-path-report.md`
- `docs/parity/contract-baseline.md`
- `docs/parity/contributor-reference.md`
- `docs/parity/overview.md`

## Implemented in This Pass

### Phase 0 — Baseline rebasing
- Rebased the parity audit from qBitrr **v5.12.10** to **v5.14.3-1**.
- Reclassified parity rows that were still carrying the prior closeout's `full` claim without latest-main verification.

### Phase 1 — Critical correctness
- **Import completion parity (`5.12.7`):** `TorrentProcessor` no longer marks torrents imported when the Arr scan is merely queued. It now waits for `IArrImportService.IsImportedAsync()` to confirm the item has left Arr's queue before persisting `Imported = true` and applying the imported tag / AutoDelete follow-up.

### Implemented later (qBitrr 5.14.3-1 path-report closeout)
- Post-import FFprobe AutoDelete cleanup (`folder_cleanup`), Host DI, optional `FFprobeAutoUpdate`.
- `SearchMissing` master switch for search loop and Ombi/Overseerr request marking.
- Explicit `loop_completed` (reset `Searched` and `Upgrade`).
- Lidarr `QualityMet` from `percentOfTracks` + cutoff/`upgradeAllowed`.
- Removed unused `IsQualityUpgradeAsync`.
- In-process dual torrent/search tasks in `ArrWorkerManager` (Host does not spawn `Torrentarr.Workers`).
- `ExpectedConfigVersion` / default config references aligned to **`6.14.3`**.
- Readarr (`[Readarr-*]`), `AutoUpdateChannel`, fractional durations, qBit init retry, seeding `-1` merge, and existing-DB table creation via `ManualSqliteMigrations`.
- `config.example.toml` now surfaces `MatchSubcategories` in the primary qBit example for parity with upstream config docs.
- OpenAPI helper scripts and contributor docs now point at latest qBitrr `master` by default instead of the old `5.12.3` pin.

### Implemented later (qBitrr 5.14.4-1)
- Stalled-upload `MaxSeedingTime` clock (`stalledUP` observation, not `last_activity`); HnR still uses real `seeding_time` / ratio.
- Per-series Sonarr episode HTTP skip (including 415); transport abort; all-fail rethrow so ingest is not marked complete.
- `ExpectedConfigVersion` / default config references aligned to **`6.14.4`**.

## Validation Evidence

Backend tests (`dotnet test --filter "Category!=Live"`):

| Project | Passed |
| --- | --- |
| Torrentarr.Core.Tests | 218 |
| Torrentarr.Host.Tests | 218 |
| Torrentarr.Infrastructure.Tests | 483 |
| **Total** | **919** |

Frontend tests (`cd webui && npx vitest run`): not re-run this pass (no frontend changes). Prior closeout: exit code 0 (167 tests).

OpenAPI drift should now be run against latest upstream `master` (or an explicit `QBITRR_OPENAPI_REF` override) after regenerating `docs/assets/openapi.json`.

Focused regression checks added/updated:

- `TorrentProcessorTests` / `TorrentProcessorAutoDeleteTests` — post-import AutoDelete FFprobe cleanup; import remains pending until Arr confirms completion
- `ArrWorkerManagerTests` — `SearchMissing` master switch, explicit `loop_completed` reset of Searched+Upgrade, in-process dual loops
- `ArrSyncServiceTests` — Lidarr `QualityMet` from percentOfTracks + cutoff/`upgradeAllowed`
- `MediaValidationServiceTests` — missing ffprobe treated as valid; ebook skip; allowlist

## Matrix Status

Latest-main user-facing parity is closed against qBitrr **v5.14.4-1**. Remaining `intentional-divergence` rows in `full-parity-matrix.md` are architecture or packaging only (process isolation, WAL vs `db_lock`, fork session sharing, Pathos, placeholder defaultdicts, `setup.py` / CI autofix).
