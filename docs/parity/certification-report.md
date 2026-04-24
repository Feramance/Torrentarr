# Parity Certification Report

## Scope

This report captures the implementation pass for strict qBitrr parity work across config/migrations, database behavior, policy engine behavior, web/API contracts, and docs alignment.

Primary tracking artifacts:

- `docs/parity/full-parity-matrix.md`
- `docs/parity/contract-baseline.md`
- `docs/parity/contributor-reference.md` (upstream pin, test matrices, OpenAPI, internal checklists; **not** for end users)
- `docs/parity/overview.md` (user-facing qBitrr relationship)

## Implemented in This Pass

- Added exhaustive parity matrix coverage for all 31 qBitrr Python files.
- Added contract baseline document for configuration, database, policy engine, and web/API.
- Updated configuration parity in `ConfigurationLoader`:
  - `ExpectedConfigVersion` aligned to `6.1.0`.
  - qBitrr-compatible env aliases (`QBITRR_*`) accepted alongside `TORRENTARR_*`.
  - migration flow now runs even on current/newer versions for idempotent cleanup.
  - legacy `WebUI.SecureCookies` migration to `WebUI.BehindHttpsProxy`.
  - qBit default fallback values aligned to qBitrr-style defaults.
- Added `WebUI.BehindHttpsProxy` to C# config model and TOML parse/save flow.
- Added tracker-level `SortTorrents` config support and parsing.
- Added qBit queue `priority` field to `TorrentInfo`.
- Added qBittorrent API client support for:
  - `/api/v2/torrents/info?sort=...`
  - `/api/v2/torrents/topPrio`
- Hardened DB parity:
  - ArrInstance indexes added in EF model.
  - startup manual migration now cleans blank `arrinstance` rows.
  - startup manual migration now ensures ArrInstance indexes exist.
- Implemented policy behavior improvements in Host orchestrator:
  - tracker-priority sorting pass for managed torrents when `SortTorrents` is enabled.
  - free-space manager now consumes queue-priority sort semantics instead of AddedOn-only ordering.
- Updated stale documentation:
  - `docs/configuration/environment.md`
  - `docs/parity-review.md` (superseded pointer)
  - targeted contract corrections in `docs/webui/api.md`.

## Validation Evidence

Backend tests:

- `dotnet test tests/Torrentarr.Core.Tests/Torrentarr.Core.Tests.csproj`
  - Passed: 79
- `dotnet test --filter "Category!=Live"`
  - Core: 79 passed
  - Host: 156 passed
  - Infrastructure: 332 passed
  - Total: 567 passed

Frontend tests:

- `npx vitest run` in `webui/`
  - Exit code: 0

Focused regression checks added:

- `Load_Migration1_RenamesSecureCookies_ToBehindHttpsProxy`
- `Load_AcceptsQbitrrEnvironmentVariableAliases`
- `Load_ParsesTrackerSortTorrentsFlag`

## Remaining Work Gate

Use `docs/parity/full-parity-matrix.md` as the final closeout checklist.
A strict “100% parity” claim is only valid when no rows remain `partial` or `missing`.

## Deep-dive program (this pass)

- Pinned default upstream: **`v5.11.1`** in [contributor-reference.md#upstream-qbitrr-baseline](contributor-reference.md#upstream-qbitrr-baseline) (re-verify commit SHA when rebasing the pin).
- **Intentional** differences and internal procedures: [contributor-reference.md](contributor-reference.md).
- **Schema table-name CI harness:** [SchemaParityTests.cs](../../tests/Torrentarr.Infrastructure.Tests/Database/SchemaParityTests.cs).
- **Targeted repair / release scripts** matrix rows: evidence under [Targeted database repair](contributor-reference.md#targeted-database-repair) and [Support scripts and CI](contributor-reference.md#support-scripts-and-ci).
- **Web / policy / long-tail** review templates: same file — [Web and API field coverage](contributor-reference.md#web-and-api-field-coverage), [Policy engine test matrix](contributor-reference.md#policy-engine-test-matrix), [Long-tail module mapping](contributor-reference.md#long-tail-module-mapping).

Runtime module rows **remain `partial` overall**; this pass adds evidence links and process docs toward eventual `full` status per row.
