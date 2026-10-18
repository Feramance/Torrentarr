# WebUI parity implementation — 2026-10-18

Reference: local qBitrr checkout `be5cb670`; Torrentarr starting commit `a4f26fb`.
The release major remains intentionally one ahead of qBitrr. No dependency changes, database resets, live configuration saves, or maintenance
operations were needed. Existing Docker Compose edits were preserved. The completed
Docker redeployment and live checks are recorded below.

## Implemented behavior

- Restored catalog grids, bounded posters, progress/status displays, detail dialogs,
  log styling, responsive tables, shared spacing and unbranded reference icons.
  Added theme, Live and Help/OpenAPI controls using the existing settings context.
- Dialog Save persists the selected section and rename, validates that section,
  preserves other drafts and unchanged redacted secrets, and keeps edits on failure.
  Deletion uses an explicit confirmation and persists only that section removal.
  Schema live/restart hints, inline validation, tag/duration controls remain available.
  Completion fractions retain their backend semantics: `0.99` is described as `99%`.
- Torrent categories expand to individual rows with progress, state, sizes, speeds,
  transferred bytes, ETA/seeding time, peers/seeds, ratio, availability, timestamps
  and tags. Aggregate/instance selection and search follow the reference; additional
  Torrentarr category statistics and mixed policies remain supported. Standard upload
  states share one backend seeding classification across category/process/overview APIs.
- Worker cards show actual process state, search activity/time, queue/tracked counts
  and one restart action. Status-only changes now trigger rendering.
- Sync resolves quality-profile names using the same Arr instance. Artist detail
  totals use the actual album/track collection. Readarr uses shared list/grid,
  aggregate loading, filters, pagination, posters and author/book details.
- Optional structured log tails supply bounded history, cursors, rotation detection
  and search windows. Existing plain text remains supported. SSE reconnects and
  falls back to incremental polling; inactive/hidden views suspend live work.
- Internal auth/update/catalog/log URLs use the configured base. Docker metadata
  directs users to recreate containers and disables binary replacement. Maintenance
  retains its existing preview/arming safeguards with shared presentation.

## Validation

Production assets were built into Host/wwwroot and exercised through an isolated
fixture server. Desktop (1280), tablet (768) and phone (390) previews covered shared
header/navigation and representative catalog lists/grids/details. Both themes and
densities were exercised. Desktop walkthrough included Processes, Logs, Radarr,
Sonarr, Lidarr, Readarr, Torrent Clients, Maintenance and Config. Large source images
were constrained without displacing controls. The retained mobile table override
was removed after visual inspection exposed label collisions; light mobile table cards now use the theme surface.

Automated regressions cover section persistence, renames/retries, unrelated drafts,
unchanged redacted secrets, fraction formatting, upload states, status-only updates,
quality-profile synchronization, artist totals, log history/rotation/partial UTF-8,
SSE fallback, inactive streams, absent Readarr instances and URL-base API/link paths.
Existing fixtures cover authentication, errors, aggregate loading, catalog counts,
posters and maintenance safeguards. Host tests include packaged asset/base hosting.

| Check | Result |
| --- | --- |
| Frontend suite | 268 passed, 37 files; final torrent-state regressions: 9 passed |
| Core non-live suite | 271 passed |
| Infrastructure non-live suite | 852 passed after the live Lidarr fixes; focused sync regressions: 30 passed |
| Host non-live suite | 319 passed; packaged assets/URL-base/metadata/catalog regressions: 48 passed; final Lidarr API regressions: 18 passed |
| Production build | TypeScript and Vite passed |
| ESLint | 0 errors; 16 warnings (baseline 13, plus 3 Readarr fast-refresh warnings matching the existing catalog modules) |
| Patch whitespace | git diff --check passed |

This is source parity and isolated validation, not certification of every live Arr record. Metadata repairs arrive on
normal synchronization; live library totals were not forced to match unrelated
snapshots. No exhaustive screenshot comparison of every width/theme/density/page
combination or real OIDC-provider round trip was performed.

## Deployment and live verification

The current working tree was rebuilt with the existing Compose configuration and
redeployed on `http://localhost:6970/ui`. Final image identity:
`sha256:ae30f27ee5e034f13ec57d2c6a2ab38d43513494525eaac9eb487105c90c8e7f`.
The container is healthy, has zero restarts, and all seven Arr workers are alive.
The config bind mount and existing data/torrent volumes were retained.

Live verification uncovered and fixed three Lidarr ingest failures that fixtures
had not represented: `/track` requires an artist/album filter, standard tracks
inherit album monitoring, and vinyl track numbers can be text. Synchronization now
fetches per artist, completes all reads before replacing stored tracks, preserves
existing rows on request failure, and retains textual labels in the existing SQLite
INTEGER-affinity column. Durations are normalized from API milliseconds to schema
seconds. No database reset or column migration was performed.

The first completed normal sync updated 408 artists and 16,017 albums and populated
71,692 tracks. Comparing the same artist (Lidarr ID 589) in both applications now
returns 24 monitored / 24 available / 0 missing tracks. Torrent overviews in the same
snapshot both return eight categories, 320 torrents and 318 seeding. These are live
snapshots and change as the applications manage downloads.

Artist details include all locally synchronized albums/tracks (including unmonitored
rows), while list progress counts monitored rows; artist 589 therefore has 31 album
rows and 213 total track rows in its full detail response. This is separate from the
24 monitored tracks used for the matching progress figures.

Verified on the deployed UI: bounded Lidarr posters and restored progress counts,
artist detail quality-profile names, expanded torrent metrics, log controls and
structured tail API, Readarr's absent-instance state, configuration Save controls and
99% text, and Docker-specific update instructions. Phone (390) and tablet (768)
viewport checks had equal page/client widths, with no horizontal page overflow;
the temporary viewport override was reset. Recent container logs contained no
error/fatal entries after the completed sync. Live configuration mutations,
credential changes and destructive maintenance were not exercised.

## Source reconciliation

Every file in the union of both `webui/src` trees is listed below, including tests.
“Adopted” means reference presentation/behavior was brought across (with Torrentarr
contracts and safeguards retained). “Equivalent” means the same role is provided
locally; file layout and exact source text need not match. “Torrentarr-specific”
means branding, backend contracts, architecture, extra capabilities, or its test suite.
Upstream file splitting alone is not a missing feature.

| File under src | Classification | Reconciliation |
| --- | --- | --- |
| `App.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `AppShell.keepAlive.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `AppShell.tsx` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `AuthGate.tsx` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `__tests__/__mocks__/svg.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/api/client.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/api/urlBase.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/components/ConfirmDialog.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/components/Pagination.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/components/StableTable.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/components/TagInput.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/config/arrSections.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/config/configFieldsGenerated.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/config/durationUtils.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/config/packageCompatibility.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/config/torrentHandlingSummary.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/hooks/useAggregateCatalogLoader.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/App.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/ArrView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/ConfigView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/LidarrView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/LogsView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/MaintenanceView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/ProcessesView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/QbitCategoriesView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/RadarrView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/ReadarrView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/pages/SonarrView.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `__tests__/setup.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `api/client.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `api/clientConfigErrors.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `api/clientTtl.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `api/clientUrls.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `api/types.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `api/urlBase.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `api/urlBase.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `assets/logo-64.png` | Torrentarr-specific | Torrentarr SVG branding replaces the reference bitmap. |
| `components/ChangelogModal.test.tsx` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `components/ChangelogModal.tsx` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `components/ConfirmDialog.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/CopyButton.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/EmptyState.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `components/IconImage.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/Pagination.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `components/QbitTorrentListRow.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `components/SkeletonLoader.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `components/StableTable.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/TagInput.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/ArrBrowseModeToggle.tsx` | Equivalent | Identical source. |
| `components/arr/ArrExternalLink.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/ArrMiniProgress.tsx` | Equivalent | Identical source. |
| `components/arr/ArrModal.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `components/arr/ArrModal.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/ArrPosterImage.retry.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `components/arr/ArrPosterImage.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/ArrStatusCells.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/LidarrAlbumDetailBody.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/LidarrArtistDetailBody.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/RadarrMovieDetailBody.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `components/arr/ReadarrAuthorDetailBody.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `components/arr/ReadarrBookDetailBody.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `components/arr/SonarrSeriesGroupDetailBody.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `config/arrSections.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `config/categoryConfigValidation.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `config/categoryPaths.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `config/configFields.generated.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `config/durationUtils.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `config/durationUtils.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `config/reactSelectTheme.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `config/reactSelectTheme.ts` | Adopted | Reference behavior/style adapted to local contracts. |
| `config/tooltips.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `config/torrentHandlingSummary.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `constants/arrAggregateFetch.test.ts` | Equivalent | Identical source. |
| `constants/arrAggregateFetch.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `constants/arrCatalogMessages.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `context/SearchContext.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `context/ToastContext.test.tsx` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `context/ToastContext.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `context/WebUIContext.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `context/useWebUI.ts` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `context/webUISettingSetters.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `context/webUISettingSetters.ts` | Equivalent | Existing WebUIContext persists preferences. |
| `hooks/useArrBrowseMode.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `hooks/useArrIconGridPageSize.ts` | Equivalent | Identical source. |
| `hooks/useDataSync.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `hooks/useDebounce.ts` | Equivalent | Identical source. |
| `hooks/useInterval.ts` | Equivalent | Identical source. |
| `hooks/useNetworkStatus.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `hooks/useRowsStore.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `icons/alert.svg` | Equivalent | Identical source. |
| `icons/build.svg` | Equivalent | Identical source. |
| `icons/check-mark.svg` | Equivalent | Identical source. |
| `icons/close.svg` | Equivalent | Identical source. |
| `icons/download.svg` | Equivalent | Identical source. |
| `icons/gear.svg` | Equivalent | Identical source. |
| `icons/github.svg` | Equivalent | Identical source. |
| `icons/lidarr.svg` | Equivalent | Identical source. |
| `icons/live-streaming.svg` | Equivalent | Identical source. |
| `icons/loading-arrow.svg` | Equivalent | Identical source. |
| `icons/log.svg` | Equivalent | Identical source. |
| `icons/logo.svg` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `icons/logov2-clean.svg` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `icons/plus.svg` | Equivalent | Identical source. |
| `icons/process.svg` | Equivalent | Identical source. |
| `icons/qbittorrent.svg` | Equivalent | Identical source. |
| `icons/radarr.svg` | Equivalent | Identical source. |
| `icons/readarr.svg` | Equivalent | Identical source. |
| `icons/refresh-arrow.svg` | Equivalent | Identical source. |
| `icons/sonarr.svg` | Equivalent | Identical source. |
| `icons/trash.svg` | Equivalent | Identical source. |
| `icons/up-arrow.svg` | Equivalent | Identical source. |
| `icons/visibility.svg` | Equivalent | Identical source. |
| `main.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/ArrCatalogView.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/ArrView.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/ConfigView.delete.test.tsx` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/ConfigView.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/LoginPage.tsx` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `pages/LogsView.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `pages/MaintenanceView.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/ProcessesView.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/QbitCategoriesView.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `pages/ReadarrView.tsx` | Torrentarr-specific | Retained local branding, API/schema, application integration or additional capability. |
| `pages/arrCatalog/ArrCatalogBodyChrome.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/ArrCatalogDetailModalHost.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/ArrCatalogIconTile.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/ArrCatalogListOrGrid.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/ArrCatalogListOrGrid.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/ArrCatalogShell.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/ArrCatalogStandardBody.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/catalogGrouping.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/createStandardArrFilters.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/createStandardArrFilters.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/definition.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/forEachInstanceChunkedPages.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/getArrCatalogDefinition.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/lidarrDefinition.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/radarrDefinition.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/readarrDefinition.tsx` | Adopted | Reference behavior/style adapted to local contracts. |
| `pages/arrCatalog/registry.ts` | Equivalent | Identical source. |
| `pages/arrCatalog/sonarrCatalogModes.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/sonarrCatalogModes.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/sonarrDefinition.tsx` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/useAggregateCatalogLoader.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/useCatalogFetchPrimitives.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/useCatalogFetchPrimitives.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/useInstancePagedFetch.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/arrCatalog/utils.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `pages/arrCatalog/utils.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `pages/config/configDocumentUtils.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configDocumentUtils.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configFieldComponents.tsx` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configFieldErrorDisplay.test.tsx` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configFieldMerge.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configFieldMerge.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configFieldOverlays.qbitSeeding.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configFieldOverlays.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configFields.generated.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configFields.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configModals.tsx` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configSaveResult.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configSaveResult.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configTypes.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configTypes.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `pages/config/configValidation.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `pages/config/configValidation.ts` | Equivalent | Implemented in ConfigView, config/arrSections and config/configFields.generated; no module-only restructuring. |
| `styles.css` | Adopted | Reference behavior/style adapted to local contracts. |
| `tableCore.ts` | Equivalent | Existing StableTable uses the installed TanStack legacy adapter. |
| `test/setup.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `test/smoke.test.ts` | Torrentarr-specific | Reference test location; local coverage is in __tests__ and backend tests. |
| `utils/arrIconGrid.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/arrThumbnailUrl.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/clipboard.ts` | Equivalent | Identical source. |
| `utils/dataSync.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/formatVersionLabel.ts` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `utils/navigateTab.ts` | Equivalent | Existing App.tsx provides this flow with Torrentarr auth/version handling. |
| `utils/normalizeNumericId.test.ts` | Equivalent | Identical source. |
| `utils/normalizeNumericId.ts` | Equivalent | Identical source. |
| `utils/posterLoadQueue.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/posterRetry.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `utils/posterRetry.ts` | Equivalent | Identical source. |
| `utils/qbitTorrentDisplay.test.ts` | Equivalent | Identical source. |
| `utils/qbitTorrentDisplay.ts` | Equivalent | Identical source. |
| `utils/rowsStore.test.ts` | Torrentarr-specific | Local regression/fixture organization. |
| `utils/rowsStore.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/safeClick.test.tsx` | Torrentarr-specific | Local regression/fixture organization. |
| `utils/safeClick.ts` | Equivalent | Shared catalog/component/helper role retained; local API, typing or lifecycle adaptation. |
| `utils/sharedIntersectionObserver.ts` | Equivalent | Identical source. |
