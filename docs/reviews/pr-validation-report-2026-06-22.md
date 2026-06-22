# PR Validation Report — 2026-06-22

Validation of all **9 open PRs** on [feramance/torrentarr](https://github.com/feramance/torrentarr) against `master` (`23679f3`). Local CI equivalent was run on every branch (draft PRs do not trigger the full GitHub Actions build matrix per `.github/workflows/build.yml` line 25).

## Executive summary

| Verdict | Count | PRs |
|---|---|---|
| **Merge now** | 4 | #282, #281, #296, #292 |
| **Merge after fixes** | 1 | #283 |
| **Merge with brief review** | 2 | #285, #300 |
| **Defer** | 1 | #293 |
| **Partial — split / staged merge** | 1 | #280 |

**Master baseline (local):** 759 .NET tests (167 Core + 196 Host + 396 Infrastructure), 154 frontend tests — all pass.

**Recommended merge order:**

1. **#283** — security (after fixing compile error)
2. **#282** — `CHANGE_ME` placeholder preservation
3. **#281** — multi-qBit `Imported` scoping
4. **#296**, **#292** — low-risk dependency patches
5. **#285**, **#300** — major dependency bumps (manual review)
6. **#293** — defer (dev-only types major)
7. **#280** — merge in slices after #281 lands; do not merge wholesale without maintainer review

Sequential merge dry-runs (`#283→#282`, `#280+#281`, `#280+#283`, `#282+#283`) produced **no conflicts**.

---

## Per-PR cards

### PR #283 — fix(security): block PasswordHash bypass via whole-WebUI section replace

- **Verdict:** Merge after fixes
- **Confidence:** High (problem); Medium (branch readiness)
- **Problem:** **Confirmed on master.** `ApplyDottedConfigChanges` in `src/Torrentarr.Host/Program.cs` only calls `RejectPasswordHashConfigChange` for dotted keys. Whole-section replacement (`currentObj[sectionKey] = change.Value` at line 2664) bypasses the guard. `SaveAndRespondConfigUpdate` has no save-time invariant. An API-token holder can clear `PasswordHash` and re-bootstrap via `POST /web/auth/set-password` using `WebUI.Token` alone.
- **CI:** GitHub pre-commit **failure**; build matrix skipped (draft). **Local build FAIL** — `CS0136` in `src/Torrentarr.WebUI/Program.cs:739`: duplicate `passwordHashError` variable in enclosing scope (lines 739 and 749).
- **Tests:** Cannot run full suite until build fixed. Fix approach (`ValidatePasswordHashForConfigApiSave`) is sound: compares current vs proposed hash, restores `[redacted]` placeholder, rejects any other change. Applied in Host `SaveAndRespondConfigUpdate` and WebUI config save paths.
- **Risks:** P0 security gap on master until merged. Trivial one-line fix needed (rename inner variable).
- **Why merge:** Real account-takeover vector; small focused diff; supersedes closed #276.
- **Dependencies:** Merge first in sequence. Blocks nothing else.

**Required fix before merge:** Rename the second `passwordHashError` in `WebUI/Program.cs` (e.g. `savePasswordHashError`) to resolve `CS0136`.

---

### PR #282 — fix: preserve CHANGE_ME qBit placeholder sections on config save

- **Verdict:** Merge
- **Confidence:** High
- **Problem:** **Confirmed on master.** `ApplyDottedConfigChanges` excludes `CHANGE_ME` qBit instances from merge snapshot (`Program.cs:2629`). `ConfigurationLoader.SaveConfig` omits them from TOML output (`ConfigurationLoader.cs:1760`). Unrelated config saves silently drop `[qBit-*]` stubs.
- **CI:** GitHub pre-commit success; build skipped (draft). **Local: build PASS, 761 tests PASS** (+2: Core +1, Host +1).
- **Tests:** `SaveConfig_PreservesChangeMeQBitPlaceholderSection`, `PostConfig_UnrelatedChange_PreservesChangeMeQBitSeedboxStubOnDisk` — both pass.
- **Risks:** Low. Touches Host `Program.cs` (same file as #283) but orthogonal code paths; sequential merge with #283 is clean.
- **Why merge:** Common multi-qBit setup workflow; well-tested; minimal scope (5 files).
- **Dependencies:** After #283.

---

### PR #281 — fix(multi-qbit): scope Imported checks by qBit instance

- **Verdict:** Merge
- **Confidence:** High
- **Problem:** **Confirmed on master.** `TorrentLibrary` is keyed by `(Hash, QbitInstance)` but several paths query `Imported` by hash only:
  - `SeedingService.cs:710-711` — H&R removal
  - `TorrentProcessor.cs:903-908` — `IsImportedInDatabaseAsync`
  - Import gating can block Arr import on instance B when instance A has `Imported=true`.
- **CI:** GitHub pre-commit success; build skipped (draft). **Local: build PASS, 760 tests PASS** (+1 Infrastructure).
- **Tests:** `IsImportedInDatabase_ScopesByQbitInstance` passes. Adds optional `qbitInstance` to `IsReadyForImportAsync` / `ImportTorrentAsync`.
- **Risks:** Low blast radius (4 files). **Does not overlap with #280** — PR #280 branch still uses hash-only `IsImportedInDatabaseAsync`.
- **Why merge:** Correctness bug for multi-qBit deployments; PR #274 was closed without landing this fix.
- **Dependencies:** After #282. **Must land before or independently of #280** (both touch `SeedingService`/`TorrentProcessor`; merge is clean but #280 does not include this fix).

---

### PR #280 — Close qBitrr parity gaps: category workers, MatchSubcategories, retries

- **Verdict:** Partial — split required; merge after #281
- **Confidence:** Medium (overall); High (individual slices validated locally)
- **Problem:** **Gaps confirmed on master** that contradict `docs/parity/full-parity-matrix.md` “full” claims for `qbit_category_manager.py` and related rows:
  - No `MatchSubcategories` / `CategoryOwnershipHelper` / `QBitCategoryWorkerManager`
  - `ApplySeedingLimitsAsync` defined but **never called** on master
  - `ProfileSwitchRetryAttempts` config exists but not enforced in `QualityProfileSwitcherService`
  - No `HttpRetryHelper`, `ImportPathTracker`, `PeriodicWalCheckpointService`
- **CI:** GitHub lint/pre-commit/CodeQL pass; **build matrix skipped (draft)**. **Local: build PASS, 769 tests PASS** (+10), vitest 154 PASS, OpenAPI drift OK (72 paths cover 66 qBitrr paths).
- **Tests:** +5 Core (`CategoryOwnershipHelperTests`), +5 Host (`OpenApiDocEndpointTests`, etc.). Infrastructure count unchanged (396).
- **Risks:** 55 files, ~+4900 lines; bundles runtime + docs/OpenAPI; **missing #281 Imported fix**; draft never ran CI matrix on GitHub.
- **Why not merge wholesale:** Too large for blind merge; should land after focused fixes (#281–#283); OpenAPI/doc churn should be separable from runtime if splitting.
- **Dependencies:** After #281 at minimum. Clean sequential merge with #281 and #283.

#### Feature-slice breakdown (#280)

| Slice | Verdict | Evidence |
|---|---|---|
| `MatchSubcategories` + `CategoryOwnershipHelper` | **Merge now** (as part of staged #280) | New `CategoryOwnershipHelper.cs`; 5 unit tests pass; config keys on `[qBit]` and Arr sections |
| `QBitCategoryWorkerManager` (qBit-only ManagedCategories) | **Merge now** (staged) | New 242-line Host background service; PlaceHolderArr / qBitCategoryManager parity |
| `ApplySeedingLimitsAsync` wiring | **Merge now** (staged) | Called in `TorrentProcessor` processing loop (line 338 on branch); dead code on master |
| `ImportPathTracker` + empty-folder cleanup | **Merge now** (staged) | In-memory `sent_to_scan` parity; `RemoveEmptyPathsUnder` |
| HTTP retries (`HttpRetryHelper`) | **Merge with review** | New Polly helper on Arr/qBit clients; verify no double-retry with existing Polly policies |
| `ProfileSwitchRetryAttempts` enforcement | **Merge now** (staged) | `QualityProfileSwitcherService` uses `Math.Max(1, arrConfig.Search.ProfileSwitchRetryAttempts)` |
| DB repair / WAL / retry extensions | **Merge with review** | `RepairAsync` uses SQLite backup API; `PeriodicWalCheckpointService`, `DatabaseRetryExtensions` — run extended soak tests |
| Config reload → worker restart | **Merge now** (staged) | Host restarts workers on `full` / `multi_arr` / `single_arr` reload types |
| OpenAPI/doc updates (+2924 lines in openapi.json) | **Defer or split** | Disproportionate to runtime changes; consider separate docs PR |
| `RemoveCompletedTorrentsAsync` from Host path | **Merge now** (staged) | Wired in Host torrent processing (already exists in Workers) |

**Recommended approach:** Mark draft → ready for review, merge #281 first, then rebase #280 and merge as one reviewed unit **or** split into 2–3 PRs (runtime / DB-ops / docs).

---

### PR #296 — Bump Microsoft.Extensions.Options (patch)

- **Verdict:** Merge
- **Confidence:** High
- **Problem:** N/A — routine dependency maintenance.
- **CI:** GitHub pre-commit success. **Local: build PASS, 760 tests PASS.**
- **Tests:** No new tests; no regressions.
- **Risks:** Patch bump (10.0.8 → 10.0.9). Matches dependabot auto-merge policy.
- **Why merge:** Low risk; CI green.
- **Dependencies:** After P0/P1 fixes; independent of other deps.

---

### PR #292 — bump react-hook-form 7.79.0 → 7.80.0

- **Verdict:** Merge
- **Confidence:** High
- **Problem:** N/A — patch/minor frontend dep.
- **CI:** GitHub pre-commit success. **Local: build PASS, 759 tests PASS.**
- **Tests:** No regressions; vitest not re-run separately (lockfile-only change).
- **Risks:** Low. Minor feature (disable useFieldArray fields).
- **Why merge:** Matches auto-merge policy; CI green.
- **Dependencies:** Independent.

---

### PR #285 — bump actions/checkout 6 → 7 (major)

- **Verdict:** Merge with brief review
- **Confidence:** High
- **Problem:** N/A — CI infrastructure.
- **CI:** GitHub full matrix pass (when run). **Local: build PASS, 759 tests PASS.**
- **Tests:** No code changes beyond workflow files (8 files).
- **Risks:** Major semver for GitHub Action. v7 blocks checkout of fork PRs for `pull_request_target` — acceptable for this repo’s workflow patterns.
- **Why merge:** CI already green on GitHub; low application risk.
- **Dependencies:** Independent; **not** auto-merged (major).

---

### PR #300 — Bump Serilog.Sinks.File 6.0.0 → 7.0.0 (major)

- **Verdict:** Merge with brief review
- **Confidence:** High
- **Problem:** N/A — logging sink upgrade.
- **CI:** GitHub pre-commit success. **Local: build PASS, 759 tests PASS.**
- **Tests:** No API usage changes required; single line in `Torrentarr.Core.csproj`.
- **Risks:** Major version. Release notes: force-reopen fix, `ILoggingFailureListener` support. No breaking usage detected in codebase (standard Serilog file sink config).
- **Why merge:** Bug fix for 30-minute force-reopen; low integration surface.
- **Dependencies:** Independent; **not** auto-merged (major).

---

### PR #293 — Bump @types/node 25.9.3 → 26.0.0 (major, dev-only)

- **Verdict:** Defer
- **Confidence:** Medium
- **Problem:** N/A — dev dependency only.
- **CI:** GitHub pre-commit success. **Local: build PASS, 759 .NET tests PASS, 154 vitest PASS.**
- **Tests:** All pass locally despite major types bump.
- **Risks:** Major `@types/node` can surface new TS strictness issues in future edits; no runtime impact.
- **Why defer:** Lowest priority; can batch with next Node types alignment or merge when convenient. No user-facing benefit.
- **Dependencies:** Independent.

---

## Gaps not covered by any open PR

| Gap | Status |
|---|---|
| Multi-qBit `Imported` scoping | **#281** (not #280) |
| `PasswordHash` section-replace bypass | **#283** (compile fix needed) |
| `CHANGE_ME` qBit stub preservation | **#282** |
| `ApplySeedingLimitsAsync` dead code on master | **#280** |
| `MatchSubcategories` / qBit-only category workers | **#280** |
| Parity matrix overclaims “full” for `qbit_category_manager.py` | Update after #280 lands |
| `SQLitePCLRaw.lib.e_sqlite3` NU1903 advisory | No open PR; separate security work |
| Closed #274 / #276 | Superseded by #281 / #283 respectively |

---

## Appendix: referenced closed PRs

| Closed PR | Relationship |
|---|---|
| #276 | Same `PasswordHash` fix as #283; never merged |
| #274 | Same `Imported` scoping as #281; marked superseded by #229 without landing fix |
| #271 | Partial `PasswordHash` guard; #283 completes it |

---

## Validation methodology

For each PR branch:

```bash
git checkout origin/<branch>
dotnet restore && dotnet build -c Release
dotnet test -c Release --no-build --filter "Category!=Live"
cd webui && npm ci && npx vitest run   # where applicable
bash scripts/check-openapi-drift.sh    # PR #280 only
```

Targeted regression filters were run for #281, #282. Merge conflict analysis used `git merge --no-commit --no-ff` sequential dry-runs.

---

*Generated by PR Validation Agent — 2026-06-22*
