# Parity Certification Report

## Scope

This report captures Torrentarr's parity status after rebasing the audit from qBitrr **5.12.3** (`0b4a111`) to upstream **latest `master` / v5.12.10**.

Primary tracking artifacts:

- `docs/parity/full-parity-matrix.md`
- `docs/parity/contract-baseline.md`
- `docs/parity/contributor-reference.md`
- `docs/parity/overview.md`

## Implemented in This Pass

### Phase 0 — Baseline rebasing
- Rebased the parity audit from qBitrr **5.12.3** to upstream **latest `master` / v5.12.10** in the parity docs and OpenAPI drift tooling.
- Reclassified parity rows that were still carrying the prior closeout's `full` claim without latest-main verification.

### Phase 1 — Critical correctness
- **Import completion parity (`5.12.7`):** `TorrentProcessor` no longer marks torrents imported when the Arr scan is merely queued. It now waits for `IArrImportService.IsImportedAsync()` to confirm the item has left Arr's queue before persisting `Imported = true` and applying the imported tag / AutoDelete follow-up.

### Phase 2 — Documentation and config baseline
- `ExpectedConfigVersion` / default config references aligned to **`6.12.3`**.
- `config.example.toml` now surfaces `MatchSubcategories` in the primary qBit example for parity with upstream config docs.
- OpenAPI helper scripts and contributor docs now point at latest qBitrr `master` by default instead of the old `5.12.3` pin.

## Validation Evidence

Backend tests (`dotnet test --filter "Category!=Live"`):

| Project | Passed |
| --- | --- |
| Torrentarr.Core.Tests | 106 |
| Torrentarr.Host.Tests | 157 |
| Torrentarr.Infrastructure.Tests | 341 |
| **Total** | **604** |

Frontend tests (`cd webui && npx vitest run`): exit code 0 (130 tests).

OpenAPI drift should now be run against latest upstream `master` (or an explicit `QBITRR_OPENAPI_REF` override) after regenerating `docs/assets/openapi.json`.

Focused regression checks added/updated:

- `TorrentProcessorTests` — import remains pending until Arr confirms completion; imported state flips only after queue exit

## Matrix Status

Latest-main parity is **not yet fully closed**. `full-parity-matrix.md` now marks the still-unreverified latest-main areas as `partial`, especially the broad `arss.py` / `main.py` coverage rows and the latest multi-instance follow-up deltas.
