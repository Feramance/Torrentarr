# qBitrr → Torrentarr Feature Parity Gap Analysis

Generated: 2026-02-25
Last updated: 2026-02-26 (all gaps implemented)
qBitrr ref: `C:\Users\techa\Documents\qBitrr`
Torrentarr ref: `C:\Users\techa\Documents\Torrentarr`

---

## Implementation Status

All identified gaps have been implemented. The table below tracks the complete feature set.

| Feature | Status | Implementation |
| --- | --- | --- |
| §1.1 Ombi/Overseerr request integration | ✅ DONE | `ArrSyncService.MarkRequestsAsync` — Ombi + Overseerr HTTP clients, pagination, Is4K, ApprovedOnly, IsRequest DB updates |
| §1.2 Quality profile switching (`UseTempForMissing`) | ✅ DONE | `QualityProfileSwitcherService`: SwitchToTempProfilesAsync, RestoreTimedOutProfilesAsync, ForceResetAllTempProfilesAsync |
| §1.3 `SearchAgainOnSearchCompletion` | ✅ DONE | `ArrWorkerManager.ResetSearchedFlagAsync` bulk UPDATE after search pass |
| §1.4 `ReSearchStalled` / `StalledDelay` | ✅ DONE | `TorrentProcessor` — stalled torrent detection + deletion + re-queue |
| §1.5 `MaximumETA` / `DoNotRemoveSlow` | ✅ DONE | `TorrentProcessor` — last-activity ETA check with deletable-percentage guard |
| §1.6 Tagless mode | ✅ DONE | All tag reads/writes gated on `!Settings.Tagless`; `TorrentLibrary` DB columns used instead |
| §1.7 `ArrErrorCodesToBlocklist` | ✅ DONE | `ArrSyncService.ScanQueueForBlocklistAsync` — warning+importPending queue scan, blocklist call |
| §1.8 Auto-update | ✅ DONE | `AutoUpdateBackgroundService` — cron-based, `UpdateService` for GitHub check + binary apply |
| §2.1 File filtering (FolderExclusion / FileNameExclusion / FileExtensionAllowlist) | ✅ DONE | `TorrentProcessor.ApplyFileFilterAsync` + `QBittorrentClient.SetFilePriorityAsync` |
| §2.2 Import 60-second grace period | ✅ DONE | `ArrImportService.IsReadyForImportAsync` — `CompletionOn` timestamp check |
| §2.4 Connectivity check + `NoInternetSleepTimer` | ✅ DONE | `ArrWorkerManager` main loop — `ConnectivityService.IsConnectedAsync` before processing |
| §2.5 Exponential backoff on errors | ✅ DONE | `ArrWorkerManager` — `2 × 1.5^n` up to 30 min; resets on success |
| §2.6 RSS Sync + RefreshMonitoredDownloads | ✅ DONE | `ArrWorkerManager.RunRssSyncIfDueAsync` / `RunRefreshDownloadsIfDueAsync` — timer-gated |
| §2.7 `DoUpgradeSearch` exclusivity | ✅ DONE | `ArrWorkerManager.RunSearchAsync` — upgrade search skips missing search when true |
| §2.8 `AlsoSearchSpecials` filter in candidate query | ✅ DONE | `ArrMediaService.GetSearchCandidatesAsync` — `SeasonNumber != 0` filter |
| §2.9 `SearchInReverse` ordering | ✅ DONE | `SearchExecutor` — `Year ASC` when true, `Year DESC` when false |
| §2.10 `Unmonitored` search | ✅ DONE | `ArrMediaService` — removes Monitored filter when flag set |
| §2.11 `SearchBySeries` (smart/true/false) | ✅ DONE | `SearchExecutor` — branches on SeriesSearch vs EpisodeSearch with smart multi-episode detection |
| §2.12 `PrioritizeTodaysReleases` time window | ✅ DONE | `ArrMediaService` — 25-hour/1-hour window (`UtcNow.AddHours(-25)` to `UtcNow.AddHours(-1)`) |
| §2.13 `IgnoreTorrentsYoungerThan` (settings + per-Arr) | ✅ DONE | `TorrentProcessor` — age check for special + regular categories |
| §2.14 `AutoPauseResume` gates `FreeSpaceService` | ✅ DONE | `ArrWorkerManager` — `FreeSpaceService` only runs when `Settings.AutoPauseResume = true` |
| §3.1 TrackerConfig: `RemoveIfExists` / `AddTrackerIfMissing` / `AddTags` | ✅ DONE | `SeedingService.ApplyTrackerActionsAsync` + `QBittorrentClient.RemoveTrackersAsync` / `AddTrackersAsync` |
| §3.2 `RemoveTrackerWithMessage` | ✅ DONE | `SeedingService.ProcessTrackerMessagesAsync` — message keyword matching + remove/delete |
| §3.3 Arr-level tracker overrides merged | ✅ DONE | `SeedingService.GetTrackerList` — qBit-level base, Arr-level wins on URI collision |
| §3.4 `MinSeedingTime` unit fix (minutes→days) | ✅ DONE | `TrackerConfig.MinSeedingTimeDays` (TOML key `MinSeedingTime`); `SeedingService` uses `days × 86400` |
| §3.5 `SuperSeedMode` per tracker | ✅ DONE | `TrackerConfig.SuperSeedMode?` + `SeedingService.ApplyTrackerActionsAsync` + `QBittorrentClient.SetSuperSeedingAsync` |
| §3.6 Per-tracker `MaxETA` | ✅ DONE | `TorrentProcessor` — resolves `trackerCfg?.MaxETA ?? Settings.MaximumETA` |
| AutoDelete after import | ✅ DONE | `TorrentProcessor.ImportTorrentAsync` — deletes torrent when `AutoDelete = true` |
| Settings.FreeSpace / FreeSpaceFolder | ✅ DONE | `FreeSpaceService.ParseFreeSpaceString` parses "10G"/"-1"; FreeSpaceFolder checked alongside per-instance DownloadPath |
| §6.1 Route prefix `/web/*` | ✅ DONE | All routes use `/web/` prefix |
| §6.2 Rich media responses | ✅ DONE | HasFile, QualityMet, CustomFormatMet, CustomFormatScore, IsRequest, Upgrade, Reason, QualityProfileId/Name |
| §6.3 Filter query params (q, missing) | ✅ DONE | `/web/radarr`, `/web/sonarr`, `/web/lidarr` — q, year_min/max, monitored, has_file, quality_met, is_request |
| §6.4 Aggregate counts in media responses | ✅ DONE | All three media endpoints wrap items in `{ items, counts }` envelope |
| §6.5 `/web/qbit/categories` seeding details | ✅ DONE | Returns live torrent counts + seeding stats + seeding config per category |
| §6.6 `/web/arr/test-connection` | ✅ DONE | One-shot client instantiation, returns success + quality profiles |
| §6.7 `POST /web/arr/rebuild` | ✅ DONE | RescanMovie / RescanSeries / RescanArtist dispatch |
| §6.8 Config PATCH format | ✅ DONE | `POST /web/config` — `{changes: {"section.key": value}}`, returns reload_type + affected_arr |
| §6.9 `GET /web/logs/{name}/download` | ✅ DONE | Streams log file as attachment |
| §6.10 Update management endpoints | ✅ DONE | `GET /web/meta`, `POST /web/update`, `GET /web/download-update` (+ `/api/*` mirrors) via `UpdateService` |

---

## Architecture Differences (Intentional)

These are deliberate design choices, not gaps:

| Item | qBitrr | Torrentarr | Decision |
| --- | --- | --- | --- |
| Process model | Separate OS processes per Arr instance (search + torrent) | Single sequential loop per instance | Intentional — simpler, still process-isolated at Host level |
| Per-qBit category manager | Dedicated process per qBit instance | Seeding applied during each Arr instance's torrent phase | Acceptable for current use cases |
| Cross-process DB restart signal | `multiprocessing.Event` | Each worker restarts independently via `ProcessStateManager` | Acceptable given process-isolation architecture |

---

## Test Coverage

| Project | Tests |
| --- | --- |
| `Torrentarr.Core.Tests` | 50 (config parsing, model defaults) |
| `Torrentarr.Infrastructure.Tests` | ~118 (services unit + mocked, live-gated) |
| `Torrentarr.Host.Tests` | 65 (API endpoint integration + MatchesCron unit) |
| `webui/src/__tests__/` | 43 (Vitest: API client, page rendering, components) |
| **Total** | **~276** |
