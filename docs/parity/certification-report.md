# Parity Certification Report

## Scope

This report captures the qBitrr **5.12.3** (`0b4a111`) parity closeout pass across config/migrations, database behavior, policy engine, web/API contracts, and docs alignment.

Primary tracking artifacts:

- `docs/parity/full-parity-matrix.md`
- `docs/parity/contract-baseline.md`
- `docs/parity/contributor-reference.md`
- `docs/parity/overview.md`

## Implemented in This Pass (5.12.3)

### Phase 0 — Branch hygiene
- Merged `origin/master` into `tracker-sorter` (dependency bumps only).
- Re-pinned upstream to qBitrr branch **5.12.3** @ `0b4a111` in `contributor-reference.md`.

### Phase 1 — Critical correctness
- **HnR dead-tracker (#412):** removed bare `"not found"` from `SeedingService`; added `TrackerMessageIndicatesDead` tests.
- **Auth bootstrap (5.12.2):** `WebUIAuthHelpers.IsSetPasswordAllowed()` requires setup token; LoginPage setup token field; `SetPasswordEndpointTests` updated.
- **Lidarr search timer:** documented N/A in `ArrWorkerManager` (single-loop architecture).

### Phase 2 — 5.12.x features
- **UrlBase:** `WebUI.UrlBase` config, `UrlBaseHelper`, `UsePathBase`, cookie path, `url_base` in meta, frontend `urlBase.ts` + ConfigView field.
- **Category paths:** `CategoryPathHelper` + `ConfigValidationHelper` overlap validation on config save; wired into torrent/category matching.
- **Catalog rollups:** `CatalogRollupService` with qBitrr semantics + 5s TTL; integrated into `/web|api/arr`, Radarr/Sonarr/Lidarr endpoints.
- **Lidarr artists + thumbnails:** `ArrCatalogEndpoints`, `ArrThumbnailService`, frontend `getLidarrArtists` / `getLidarrArtistDetail`.
- **OpenAPI:** expanded `docs/assets/openapi.json` (26 paths); `scripts/check-openapi-drift.sh` in CI.

### Phase 3 — Config schema
- `ExpectedConfigVersion = 6.12.2` (+1 major vs qBitrr `5.12.2`).
- Default `Settings.ConfigVersion` updated to `6.12.2`.

## Validation Evidence

Backend tests (`dotnet test --filter "Category!=Live"`):

| Project | Passed |
| --- | --- |
| Torrentarr.Core.Tests | 106 |
| Torrentarr.Host.Tests | 157 |
| Torrentarr.Infrastructure.Tests | 341 |
| **Total** | **604** |

Frontend tests (`cd webui && npx vitest run`): exit code 0 (130 tests).

OpenAPI drift: `bash scripts/check-openapi-drift.sh` — 26 Torrentarr paths ⊆ 66 qBitrr 5.12.3 paths.

Focused regression checks added/updated:

- `SeedingServiceTests` — dead-tracker false positive guard
- `CategoryPathHelperTests`, `ConfigValidationHelperTests`
- `CatalogRollupServiceTests`
- `SetPasswordEndpointTests` — setup token bootstrap

## Matrix Status

All runtime module rows in `full-parity-matrix.md` are **`full`** or **`intentional-divergence`** as of this pass. Upstream pin: **5.12.3 @ 0b4a111**.
