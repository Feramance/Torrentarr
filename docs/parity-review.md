# qBitrr Parity Gaps — Implementation Review

This document verifies that all 12 gaps from [qbitrr_parity_gaps_7ae8df72.plan.md](.cursor/plans/qbitrr_parity_gaps_7ae8df72.plan.md) have been implemented correctly.

---

## 1. `--gen-config` CLI — **DONE**

- **Location:** `src/Torrentarr.Host/Program.cs` (lines 211–219)
- **Behavior:** After `app.Build()`, if the only CLI argument is `--gen-config` or `-gc`, the Host calls `ConfigurationLoader.GetDefaultConfigPath()`, `GenerateDefaultConfig()`, `SaveConfig()`, prints the path, and returns 0. No server starts.
- **Verdict:** Implemented correctly. Docs (getting-started, config-file, docker, migration) already reference `torrentarr --gen-config`; behavior matches.

---

## 2. Config version validation and migrations — **DONE (minimal)**

- **Validation:** `ConfigurationLoader.ValidateConfigVersion()` exists (`ConfigurationLoader.cs` lines 682–696). Compares `Settings.ConfigVersion` to `ExpectedConfigVersion` ("5.9.2"); returns `(IsValid, Message, CurrentVersion)`.
- **Migrations:** No full key-by-key migration like qBitrr’s `apply_config_migrations`. On load, `LoadOrCreate()` (lines 668–676) calls `ValidateConfigVersion`; if the message is `"migration_needed"` (current &lt; expected), it sets `ConfigVersion = ExpectedConfigVersion` and saves. So older configs get a version bump only.
- **Verdict:** Per plan (“minimal migration step”), this is acceptable. Full key migrations were marked low priority.

---

## 3. Config version mismatch warning in GET /web/config — **DONE**

- **Location:** `src/Torrentarr.Host/Program.cs` (lines 1055–1060)
- **Behavior:** GET `/web/config` builds redacted config, then calls `ConfigurationLoader.ValidateConfigVersion(cfg)`. If `!validation.IsValid && validation.Message != null`, it returns `Results.Json(new { config = redacted, warning = new { type = "config_version_mismatch", message = validation.Message, currentVersion = validation.CurrentVersion } })`.
- **Frontend:** `webui/src/api/client.ts` `getConfig()` fetches as `ConfigDocument | ConfigResponseWithWarning`; if `"warning" in response && "config" in response`, it stores `warning.message` in `sessionStorage.config_version_warning` and returns `warningResponse.config`. `WebUIContext.tsx` reads that and shows a toast.
- **Verdict:** Implemented correctly. Frontend already supported the shape; backend now returns it on version mismatch.

---

## 4. Process restart limits — **DONE**

- **Location:** `src/Torrentarr.Infrastructure/Services/ArrWorkerManager.cs` (lines 136–187)
- **Behavior:** `RestartWorkerAsync()`:
  - Returns false if `!settings.AutoRestartProcesses` (with log).
  - Uses `ProcessRestartWindow` (seconds) and `MaxProcessRestarts`. Under `_restartLock`, maintains per-instance `_restartTimestamps`; removes timestamps older than `cutoff = UtcNow - windowSeconds`; if `list.Count >= maxRestarts`, returns false with log.
  - If allowed, appends current time, then applies `ProcessRestartDelay` (optional delay) before cancelling the worker and calling `StartWorker()`.
- **Config:** `TorrentarrConfig.Settings` and `ConfigurationLoader` parse `AutoRestartProcesses`, `MaxProcessRestarts`, `ProcessRestartWindow`, `ProcessRestartDelay`; defaults and TOML serialization are present.
- **Verdict:** Implemented correctly. Restart loops are gated as in qBitrr.

---

## 5. Search activity persistence (Processes page) — **DONE**

- **Persistence:** Entity `SearchActivity` in `src/Torrentarr.Infrastructure/Database/Models/SearchActivity.cs` (table `searchactivity`, columns `category`, `summary`, `timestamp`). `TorrentarrDbContext` exposes `DbSet<SearchActivity>`.
- **Write:** In `ArrWorkerManager.cs` (lines 305–319), after a search run the code updates or adds a `SearchActivity` row for the instance and calls `SaveChangesAsync`.
- **Read on startup:** In `ArrWorkerManager` (lines 104–119), after creating state entries it loads `db.SearchActivity.ToListAsync()` and for each activity updates the corresponding state (`Category + "-search"`) with `SearchSummary` and `SearchTimestamp`.
- **Verdict:** Implemented correctly. Processes page shows last search activity across restarts.

---

## 6. Database repair script / documentation — **DONE**

- **CLI:** `src/Torrentarr.Host/Program.cs` (lines 221–244). If the only argument is `--repair-database`, the Host opens `dbPath` (from `basePath`, i.e. `config/qbitrr.db` or `/config/qbitrr.db`), runs `PRAGMA wal_checkpoint(TRUNCATE);` and `PRAGMA integrity_check;`, prints the result, and exits with 0 if result is `"ok"`, else 1. No app startup.
- **Docs:** `docs/troubleshooting/database.md` now includes “Method 0: Torrentarr CLI” describing `torrentarr --repair-database` and the database path. `docs/advanced/database.md` was corrected from `--repair-db` to `--repair-database`.
- **Note:** Main troubleshooting doc uses `~/config/torrentarr.db` in many examples; the Host actually uses `qbitrr.db` in the config directory. Consider aligning doc examples with the real path (`config/qbitrr.db` or `/config/qbitrr.db`) or documenting both.

---

## 7. Default config version in GenerateDefaultConfig — **DONE**

- **Location:** `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` (line 708)
- **Behavior:** `GenerateDefaultConfig()` sets `ConfigVersion = "5.9.2"` (same as `ExpectedConfigVersion` and `TorrentarrConfig` default).
- **Verdict:** No longer inconsistent; generated config matches schema version.

---

## 8. Host free-space: respect Settings.FreeSpace string — **DONE**

- **Location:** `src/Torrentarr.Host/Program.cs` (`ProcessOrchestratorService`, lines 2359–2370, 2375–2387, 2444–2446)
- **Behavior:** In the constructor, `ParseFreeSpaceString(_config.Settings.FreeSpace)` is used. If result &lt; 0 (e.g. `"-1"`), `_freeSpaceEnabled = false` and `_minFreeSpaceBytes` is set from `FreeSpaceThresholdGB` fallback (unused when disabled). If result ≥ 0, `_freeSpaceEnabled = true` and `_minFreeSpaceBytes = freeSpaceBytes`. `ParseFreeSpaceString` supports `-1`, `"10G"`, `"500M"`, `"1024K"`, or raw number. The free-space loop runs only when `_config.Settings.AutoPauseResume && _freeSpaceEnabled && _minFreeSpaceBytes > 0`.
- **Verdict:** Implemented correctly. Host respects `FreeSpace = "-1"` (disabled) and `"10G"`/`"500M"` (threshold), with fallback to `FreeSpaceThresholdGB`.

---

## 9. Protect Settings.ConfigVersion on config update — **DONE**

- **Host:** `src/Torrentarr.Host/Program.cs` (lines 1099–1101). In POST `/web/config`, when applying changes, if the key is `Settings.ConfigVersion` (case-insensitive), returns 403 with body `{ error = "Cannot modify protected configuration key: Settings.ConfigVersion" }`.
- **WebUI:** `src/Torrentarr.WebUI/Program.cs` (lines 470–471). Same check and 403 response.
- **Verdict:** Implemented correctly in both entrypoints.

---

## 10. qBit CategorySeeding: StalledDelay and IgnoreTorrentsYoungerThan — **DONE**

- **Model:** `CategorySeedingConfig` in `TorrentarrConfig.cs` (lines 101–104) has `StalledDelay` (int, minutes, default 15) and `IgnoreTorrentsYoungerThan` (int, seconds, default 180), with §10 parity comments.
- **Parsing:** `ConfigurationLoader.ParseCategorySeeding()` (lines 314–317) reads `StalledDelay` and `IgnoreTorrentsYoungerThan` from the TOML table (used for `[qBit.CategorySeeding]` and `[qBit-*].CategorySeeding`).
- **Serialization:** `GenerateTomlContent` / config save writes `StalledDelay` and `IgnoreTorrentsYoungerThan` for qBit CategorySeeding (lines 858–859).
- **Usage:** These are available for qBit-level seeding/stalled logic. Per-Arr `Torrent` config already had its own `StalledDelay` and `IgnoreTorrentsYoungerThan`; CategorySeeding now mirrors them for qBit-managed categories.
- **Verdict:** Implemented correctly.

---

## 11. Process restart endpoint: kind parameter semantics — **DONE**

- **Location:** `src/Torrentarr.Host/Program.cs`: `/web/processes/{category}/{kind}/restart` (lines 581–593) and `/api/processes/{category}/{kind}/restart` (lines 1437–1449).
- **Behavior:** `kind` is normalized to lowercase. If it is not one of `"search"`, `"torrent"`, `"category"`, `"arr"`, the handler returns `Results.BadRequest(new { error = "kind must be search, torrent, category, or arr" })`. So `"all"` is rejected with 400. Comment states that kind is advisory (one loop per Arr); restart always restarts the whole worker.
- **Verdict:** Implemented correctly. Kind is validated; invalid values (including `all`) get 400; semantics are documented in-code.

---

## 12. (Optional) QBitConfig.DownloadPath from TOML — **DONE**

- **Location:** `src/Torrentarr.Core/Configuration/ConfigurationLoader.cs` (lines 187–188 in `ParseQBit()`), and serialization (lines 832–833).
- **Behavior:** When parsing a qBit section, `table.TryGetValue("DownloadPath", out var downloadPath)` sets `qbit.DownloadPath = downloadPath?.ToString()`. Save writes `DownloadPath` when non-empty.
- **Verdict:** Implemented. Optional extension is present; can be used for free-space path or UI.

---

## Summary

| # | Gap | Status | Notes |
|---|-----|--------|--------|
| 1 | `--gen-config` CLI | Done | Matches docs |
| 2 | Config version validation + migrations | Done | Version validation + bump on load; no full key migrations |
| 3 | Config version mismatch warning GET /web/config | Done | Backend + frontend |
| 4 | Process restart limits | Done | AutoRestartProcesses, MaxProcessRestarts, window, delay |
| 5 | Search activity persistence | Done | SearchActivity entity + load/save in ArrWorkerManager |
| 6 | DB repair script/docs | Done | `--repair-database` CLI + doc updates |
| 7 | GenerateDefaultConfig ConfigVersion 5.9.2 | Done | Single constant |
| 8 | Host free-space Settings.FreeSpace | Done | ParseFreeSpaceString, _freeSpaceEnabled gate |
| 9 | Protect ConfigVersion on update | Done | 403 in Host and WebUI |
| 10 | CategorySeeding StalledDelay / IgnoreTorrentsYoungerThan | Done | Model, parse, serialize |
| 11 | Process restart kind validation | Done | 400 for invalid kind; comment on semantics |
| 12 | QBitConfig.DownloadPath from TOML | Done | Parse + serialize |

---

## Minor follow-ups (non-blocking)

1. **Database path naming:** Host uses `qbitrr.db`; `docs/troubleshooting/database.md` (and others) often use `torrentarr.db`. Consider documenting that the actual file is `config/qbitrr.db` (or `/config/qbitrr.db` in Docker) for consistency, or standardizing one name in docs/code.
2. **docs/advanced/database.md:** References `torrentarr --vacuum-db`; there is no `--vacuum-db` CLI in the Host. Either remove that example or add a short note that manual VACUUM is via sqlite3 (as in the main database troubleshooting doc).
3. **ConfigVersionWarning.currentVersion:** In `webui/src/api/types.ts`, `ConfigVersionWarning` has `currentVersion: number`; the API returns `currentVersion` as a string. The UI only uses `message` for the toast, so behavior is fine; the type could be `string` for accuracy.

---

*Review completed; all 12 parity gaps are implemented. Two documentation edits were applied: `--repair-db` → `--repair-database` in advanced/database.md, and addition of “Method 0: Torrentarr CLI” in troubleshooting/database.md.*
