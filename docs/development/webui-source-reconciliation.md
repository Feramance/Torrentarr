# Canonical frontend source reconciliation — 2026-10-19

Pinned reference: qBitrr `be5cb670`. This inventory covers every tracked path under its `webui/`, plus Torrentarr-only frontend files. It replaces the previous blanket parity claim.

**Adopted** means byte-identical; **formatting** means identical output after Prettier 3.2.5 normalization (source evidence, not a separate behavioral claim). **Adapted** lists the concrete boundary/safeguard and verification. Test-only omissions are recorded without claiming their assertions were all ported.

| Reference file | Classification | Reason / verification |
| --- | --- | --- |
| `webui/.npmrc` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/eslint.config.js` | Adapted | Retained installed lint configuration; lint 0 errors/13 warnings. |
| `webui/index.html` | Adapted | Torrentarr title/relative manifest and favicon; Host injects URL-base; packaged asset tests. |
| `webui/package-lock.json` | Adapted | Existing installed dependency lock retained; build and 286 frontend tests. |
| `webui/package.json` | Adapted | Existing dependency versions and .NET schema generation commands retained; production build. |
| `webui/postcss.config.js` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/public/favicon-16x16.png` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/favicon-32x32.png` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/favicon-48x48.png` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/favicon.ico` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/icon-192.png` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/icon-512.png` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/public/manifest.json` | Adapted | Torrentarr name, start URL and existing icons; relative packaged hosting. |
| `webui/public/sw.js` | Adapted | Content-versioned static cache only; authenticated/API requests excluded; six service-worker tests. |
| `webui/public/vite.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/App.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/AppShell.keepAlive.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/AppShell.tsx` | Adapted | Maintenance, configured/offline instance navigation, retained views/scroll, selected category+client; shell lifecycle/navigation tests. |
| `webui/src/AuthGate.tsx` | Adapted | Torrentarr branding; canonical auth boundary; App setup/login tests. |
| `webui/src/api/client.ts` | Adapted | Canonical TTL/retry/error transport, .NET endpoint contracts, extra clients/logs/maintenance; API regression tests. |
| `webui/src/api/clientConfigErrors.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/api/clientTtl.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/api/clientUrls.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/api/types.ts` | Adapted | Preserved .NET catalog/client/log fields; structured heartbeat loops and restartability; Host contract tests. |
| `webui/src/api/urlBase.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/api/urlBase.ts` | Adapted | Root /ui and prefix resolution; URL-base frontend and packaged Host tests. |
| `webui/src/assets/logo-64.png` | Omitted | Torrentarr SVG logo retained instead of qBitrr raster branding. |
| `webui/src/components/ChangelogModal.test.tsx` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/components/ChangelogModal.tsx` | Adapted | Docker/source/.NET guidance and prefixed download links; metadata contracts/build. |
| `webui/src/components/ConfirmDialog.tsx` | Adapted | Retained focus restoration, keyboard trap and accessible dialog; component tests. |
| `webui/src/components/CopyButton.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/IconImage.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/QbitTorrentListRow.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/StableTable.tsx` | Adapted | Canonical row subscriptions plus current store-backed cell accessors; StableTable.store regression. |
| `webui/src/components/TagInput.tsx` | Adapted | Accessible control name added; configuration tests. |
| `webui/src/components/arr/ArrBrowseModeToggle.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/components/arr/ArrExternalLink.tsx` | Adapted | Authenticated/safe instance-aware link adapter retained; catalog/API tests. |
| `webui/src/components/arr/ArrMiniProgress.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/components/arr/ArrModal.tsx` | Adapted | Retained keyboard, focus restoration and body lock; modal tests. |
| `webui/src/components/arr/ArrPosterImage.retry.test.tsx` | Adapted | Observer mocks adapted to local test harness; poster retry suite. |
| `webui/src/components/arr/ArrPosterImage.tsx` | Adapted | Retained authenticated blob media loading/cancellation; poster retry suite and fixture images. |
| `webui/src/components/arr/ArrStatusCells.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/LidarrAlbumDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/LidarrArtistDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/RadarrMovieDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/ReadarrAuthorDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/ReadarrBookDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/components/arr/SonarrSeriesGroupDetailBody.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/config/categoryConfigValidation.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/config/categoryPaths.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/config/durationUtils.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/config/durationUtils.ts` | Adapted | Serialize numeric units expected by C# DurationParser; duration/config tests. |
| `webui/src/config/reactSelectTheme.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/config/reactSelectTheme.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/config/tooltips.ts` | Adapted | Torrentarr branding and environment names; runtime .NET schema descriptions remain authoritative. |
| `webui/src/config/torrentHandlingSummary.ts` | Adapted | Torrentarr setting semantics and completion fraction percentage conversion; summary tests. |
| `webui/src/constants/arrAggregateFetch.test.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/constants/arrAggregateFetch.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/constants/arrCatalogMessages.ts` | Adapted | Torrentarr sync branding; catalog empty-state fixtures. |
| `webui/src/context/SearchContext.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/context/ToastContext.test.tsx` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/context/ToastContext.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/context/WebUIContext.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/context/webUISettingSetters.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/context/webUISettingSetters.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/hooks/useArrBrowseMode.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/hooks/useArrIconGridPageSize.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/hooks/useDataSync.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/hooks/useDebounce.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/hooks/useInterval.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/hooks/useNetworkStatus.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/hooks/useRowsStore.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/icons/alert.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/build.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/check-mark.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/close.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/download.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/gear.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/github.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/lidarr.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/live-streaming.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/loading-arrow.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/log.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/plus.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/process.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/qbittorrent.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/radarr.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/readarr.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/refresh-arrow.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/sonarr.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/trash.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/up-arrow.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/icons/visibility.svg` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/main.tsx` | Adapted | Canonical theme bootstrap, production relative SW registration; packaged build/cache tests. |
| `webui/src/pages/ArrCatalogView.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/pages/ConfigView.delete.test.tsx` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/ConfigView.tsx` | Adapted | Canonical composed dialogs with real section saves, rename chains, draft/secret preservation and all clients; 22 ConfigView tests. |
| `webui/src/pages/LoginPage.tsx` | Adapted | Torrentarr branding and setup-token/URL-base authentication contract; App login tests. |
| `webui/src/pages/LogsView.tsx` | Adapted | Existing .NET structured tail, rotation, SSE/poll fallback and bounded history retained with canonical chrome; LogsView/Host log tests. |
| `webui/src/pages/ProcessesView.tsx` | Adapted | Canonical cards; one physical worker, Search/Torrent activity, separate connectivity, exact category target; ten process tests. |
| `webui/src/pages/QbitCategoriesView.tsx` | Adapted | Additional clients/stats and exact category+instance target retained; torrent view tests. |
| `webui/src/pages/arrCatalog/ArrCatalogBodyChrome.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/ArrCatalogDetailModalHost.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/ArrCatalogIconTile.tsx` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/ArrCatalogListOrGrid.test.tsx` | Adapted | Asserts store wiring rather than discarded inputs; list/grid suite. |
| `webui/src/pages/arrCatalog/ArrCatalogListOrGrid.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/pages/arrCatalog/ArrCatalogShell.tsx` | Adapted | Canonical shell with instance-name selection instead of ambiguous category; shared category tests. |
| `webui/src/pages/arrCatalog/ArrCatalogStandardBody.tsx` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/pages/arrCatalog/catalogGrouping.test.ts` | Adapted | Local fixtures cover duplicate identities and monitored totals; grouping suite. |
| `webui/src/pages/arrCatalog/createStandardArrFilters.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/createStandardArrFilters.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/definition.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/forEachInstanceChunkedPages.ts` | Adapted | Use instance name for requests; duplicate-category fixture regression. |
| `webui/src/pages/arrCatalog/getArrCatalogDefinition.ts` | Adapted | Readarr branch reordered; registry behavior tested by catalog suites. |
| `webui/src/pages/arrCatalog/lidarrDefinition.tsx` | Adapted | Canonical workflow retained with existing .NET DTO fields, stable instance+record identities and AppColumnDef store typing; corresponding catalog suites/fixtures. |
| `webui/src/pages/arrCatalog/radarrDefinition.tsx` | Adapted | Canonical workflow retained with existing .NET DTO fields, stable instance+record identities and AppColumnDef store typing; corresponding catalog suites/fixtures. |
| `webui/src/pages/arrCatalog/readarrDefinition.tsx` | Adapted | Canonical workflow retained with existing .NET DTO fields, stable instance+record identities and AppColumnDef store typing; corresponding catalog suites/fixtures. |
| `webui/src/pages/arrCatalog/registry.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/pages/arrCatalog/sonarrCatalogModes.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/sonarrCatalogModes.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/sonarrDefinition.tsx` | Adapted | Canonical workflow retained with existing .NET DTO fields, stable instance+record identities and AppColumnDef store typing; corresponding catalog suites/fixtures. |
| `webui/src/pages/arrCatalog/useAggregateCatalogLoader.ts` | Adapted | Request each instance by stable name; aggregate catalog tests. |
| `webui/src/pages/arrCatalog/useCatalogFetchPrimitives.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/arrCatalog/useCatalogFetchPrimitives.ts` | Adapted | Retained lifecycle fixes: latest search callback, page-size change guard; fetch primitive tests. |
| `webui/src/pages/arrCatalog/useInstancePagedFetch.ts` | Adapted | Retained cancellation, initial empty-state resolution and current-page cache; fetch/catalog tests. |
| `webui/src/pages/arrCatalog/utils.test.ts` | Adapted | Stable name/duplicate category fixtures; catalog utils tests. |
| `webui/src/pages/arrCatalog/utils.ts` | Adapted | Selection and request identity use instance name; catalog utils tests. |
| `webui/src/pages/config/configDocumentUtils.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configDocumentUtils.ts` | Adapted | Case-insensitive .NET keys with original key preservation; ConfigView save/rename tests. |
| `webui/src/pages/config/configFieldComponents.tsx` | Adapted | Canonical controls with accessible labels, .NET live/restart hints, structured client arrays; ConfigView tests. |
| `webui/src/pages/config/configFieldErrorDisplay.test.tsx` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configFieldMerge.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configFieldMerge.ts` | Omitted | Python field registry/merge superseded by .NET schema adapter; ConfigView tests. |
| `webui/src/pages/config/configFieldOverlays.qbitSeeding.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configFieldOverlays.ts` | Adapted | Only WebUI validation overlays retained; schema owns settings; ConfigView validation. |
| `webui/src/pages/config/configFields.generated.ts` | Omitted | Python field registry/merge superseded by .NET schema adapter; ConfigView tests. |
| `webui/src/pages/config/configFields.ts` | Adapted | Generate canonical field groups from authoritative .NET schema, including extra clients; configuration tests. |
| `webui/src/pages/config/configModals.tsx` | Adapted | Canonical modals with focus trapping/scroll restoration; shared extra-client dialog; ConfigView tests. |
| `webui/src/pages/config/configSaveResult.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configSaveResult.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/pages/config/configTypes.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configTypes.ts` | Adapted | Optional .NET field metadata and structured-array field type; build/config tests. |
| `webui/src/pages/config/configValidation.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/pages/config/configValidation.ts` | Adapted | Optional selects accepted and .NET schema constraints used; configuration tests. |
| `webui/src/styles.css` | Adapted | Canonical CSS; Maintenance/additional clients, accessible dialogs and measured mobile overflow/poster corrections; three viewport matrix. |
| `webui/src/tableCore.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/test/setup.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/test/smoke.test.ts` | Omitted | Upstream test/harness path not copied; local tests listed in webui-parity.md. This is not an assertion of identical test coverage. |
| `webui/src/utils/arrIconGrid.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/utils/arrThumbnailUrl.ts` | Adapted | Retained authenticated, base-aware instance media endpoints; thumbnail tests. |
| `webui/src/utils/clipboard.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/utils/dataSync.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/utils/formatVersionLabel.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/utils/navigateTab.ts` | Adapted | Category navigation includes stable client identity; process navigation tests. |
| `webui/src/utils/normalizeNumericId.test.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/utils/normalizeNumericId.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/utils/posterLoadQueue.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/utils/posterRetry.test.ts` | Formatting | Pinned implementation retained; formatter-only difference. |
| `webui/src/utils/posterRetry.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/src/utils/qbitTorrentDisplay.test.ts` | Adapted | Additional standard upload-state fixtures; torrent-state suite. |
| `webui/src/utils/qbitTorrentDisplay.ts` | Adapted | Retained complete upload-state classification used by metrics; torrent-state suite. |
| `webui/src/utils/rowsStore.test.ts` | Adapted | Retained order-only and stable row identity regressions; rows-store suite. |
| `webui/src/utils/rowsStore.ts` | Adapted | Retained order-only updates and safe reset notifications; rows-store suite. |
| `webui/src/utils/safeClick.ts` | Adapted | Retained modifier-key/interactive-target safeguards; safe click/catalog tests. |
| `webui/src/utils/sharedIntersectionObserver.ts` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/tailwind.config.js` | Adapted | Existing installed build configuration retained; production build. |
| `webui/tsconfig.app.json` | Adapted | Exclude test modules from production typing; tests still discovered by Vitest. |
| `webui/tsconfig.json` | Adopted | Pinned source/assets adopted unchanged. |
| `webui/tsconfig.node.json` | Adapted | Existing Vite/Node configuration retained; production build. |
| `webui/vite.config.ts` | Adapted | Relative .NET Host output and content-derived deploy cache version; production/cache/Host asset tests. |
| `webui/vitest.config.ts` | Adapted | Existing jsdom/MSW setup and sequential file execution retained; 286 frontend tests. |

## Torrentarr-only frontend files

These retain Torrentarr backend contracts and regression coverage. Generated production files and node_modules are excluded.

| File | Purpose |
| --- | --- |
| `webui/fixtures/parity.json` | Synthetic deterministic cross-application comparison data. |
| `webui/public/logov2-clean.png` | Existing Torrentarr branding/icon assets. |
| `webui/public/logov2-clean.svg` | Existing Torrentarr branding/icon assets. |
| `webui/src/__tests__/__mocks__/svg.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/api/client.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/api/urlBase.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/components/ConfirmDialog.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/components/Pagination.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/components/StableTable.store.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/components/StableTable.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/components/TagInput.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/config/arrSections.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/config/configFieldsGenerated.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/config/durationUtils.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/config/packageCompatibility.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/config/torrentHandlingSummary.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/hooks/useAggregateCatalogLoader.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/App.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/AppShell.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/ArrView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/ConfigView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/LidarrView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/LogsView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/MaintenanceView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/ProcessesView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/QbitCategoriesView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/RadarrView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/ReadarrView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/pages/SonarrView.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/serviceWorker.test.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/__tests__/setup.ts` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/components/EmptyState.tsx` | Existing Torrentarr shared API/lifecycle/catalog support; retained by the canonical components. |
| `webui/src/components/Pagination.tsx` | Existing Torrentarr shared API/lifecycle/catalog support; retained by the canonical components. |
| `webui/src/components/SkeletonLoader.tsx` | Existing Torrentarr shared API/lifecycle/catalog support; retained by the canonical components. |
| `webui/src/components/arr/ArrModal.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
| `webui/src/config/arrSections.ts` | Authoritative .NET schema, Arr identity, units and client configuration adapters. |
| `webui/src/config/configFields.generated.ts` | Authoritative .NET schema, Arr identity, units and client configuration adapters. |
| `webui/src/context/useWebUI.ts` | Existing Torrentarr shared API/lifecycle/catalog support; retained by the canonical components. |
| `webui/src/icons/logo.svg` | Existing Torrentarr branding/icon assets. |
| `webui/src/icons/logov2-clean.svg` | Existing Torrentarr branding/icon assets. |
| `webui/src/pages/ArrView.tsx` | Existing Torrentarr shared API/lifecycle/catalog support; retained by the canonical components. |
| `webui/src/pages/MaintenanceView.tsx` | Torrentarr Maintenance safeguards and shared presentation. |
| `webui/src/pages/ReadarrView.tsx` | Compatibility wrapper delegating to the single shared ArrCatalogView. |
| `webui/src/utils/safeClick.test.tsx` | Torrentarr regression fixture/test harness; frontend suite. |
