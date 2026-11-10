import { STATISTICS_UPDATED } from "./hooks/useStatisticsUpdates";
import { useStatisticsStream } from "./hooks/useStatisticsStream";
import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
  startTransition,
  type JSX,
  lazy,
  Suspense,
} from "react";
import { formatVersionLabel } from "./utils/formatVersionLabel";
import { useToast } from "./context/ToastContext";
import { useSearch } from "./context/SearchContext";
import { useWebUI } from "./context/WebUIContext";
import { useNetworkStatus } from "./hooks/useNetworkStatus";
import {
  getMeta,
  getStatus,
  triggerUpdate,
  getIssuesSummary,
} from "./api/client";
import { webPath } from "./api/urlBase";
import type { ArrInfo, MetaResponse } from "./api/types";
import { arrTypeLabel } from "./config/arrSections";
import { THEME_OPTIONS } from "./utils/theme";
import { IconImage } from "./components/IconImage";
import GitHubIcon from "./icons/github.svg";
import RefreshIcon from "./icons/refresh-arrow.svg";
import UpdateIcon from "./icons/up-arrow.svg";
import ProcessesIcon from "./icons/process.svg";
import IssuesIcon from "./icons/alert.svg";
import LogsIcon from "./icons/log.svg";
import RadarrIcon from "./icons/radarr.svg";
import SonarrIcon from "./icons/sonarr.svg";
import LidarrIcon from "./icons/lidarr.svg";
import ReadarrIcon from "./icons/readarr.svg";
import QbitIcon from "./icons/qbittorrent.svg";
import ConfigIcon from "./icons/gear.svg";
import logoUrl from "./icons/logov2-clean.svg";
import { ShellDialog } from "./components/ShellDialog";
import { safeClick } from "./utils/safeClick";
import {
  NAVIGATE_TAB_EVENT,
  type NavigateTabDetail,
  type NavigableTab,
} from "./utils/navigateTab";

type Tab =
  | "issues"
  | "processes"
  | "logs"
  | "radarr"
  | "sonarr"
  | "lidarr"
  | "readarr"
  | "qbittorrent"
  | "config"
  | "maintenance";

const loadProcessesView = () =>
  import("./pages/ProcessesView").then((module) => ({
    default: module.ProcessesView,
  }));
const loadLogsView = () =>
  import("./pages/LogsView").then((module) => ({ default: module.LogsView }));
const loadArrCatalogView = () =>
  import("./pages/ArrCatalogView").then((module) => ({
    default: module.ArrCatalogView,
  }));
const loadQbitCategoriesView = () =>
  import("./pages/QbitCategoriesView").then((module) => ({
    default: module.QbitCategoriesView,
  }));
const loadConfigView = () =>
  import("./pages/ConfigView").then((module) => ({
    default: module.ConfigView,
  }));

const loadIssuesView = () =>
  import("./pages/IssuesView").then((module) => ({
    default: module.IssuesView,
  }));
const IssuesView = lazy(loadIssuesView);
const ProcessesView = lazy(loadProcessesView);
const LogsView = lazy(loadLogsView);
const ArrCatalogView = lazy(loadArrCatalogView);
const QbitCategoriesView = lazy(loadQbitCategoriesView);
const ConfigView = lazy(loadConfigView);
const loadMaintenanceView = () =>
  import("./pages/MaintenanceView").then((module) => ({
    default: module.MaintenanceView,
  }));
const MaintenanceView = lazy(loadMaintenanceView);
const ChangelogModal = lazy(() =>
  import("./components/ChangelogModal").then((module) => ({
    default: module.ChangelogModal,
  })),
);

const TAB_PREFETCHERS: Record<Tab, () => Promise<unknown>> = {
  issues: loadIssuesView,
  processes: loadProcessesView,
  logs: loadLogsView,
  radarr: loadArrCatalogView,
  sonarr: loadArrCatalogView,
  lidarr: loadArrCatalogView,
  readarr: loadArrCatalogView,
  qbittorrent: loadQbitCategoriesView,
  config: loadConfigView,
  maintenance: loadMaintenanceView,
};

function prefetchTab(tabId: Tab): void {
  void TAB_PREFETCHERS[tabId]().catch(() => {
    /* The route displays load failures when opened. */
  });
}

interface NavTab {
  id: Tab;
  label: string;
  icon: string;
}

function AppShell({
  authRequired,
  onSignOut,
  initialMeta = null,
}: {
  authRequired: boolean;
  onSignOut: () => void;
  initialMeta?: MetaResponse | null;
}): JSX.Element {
  useStatisticsStream();
  const [navInstances, setNavInstances] = useState<
    Partial<Record<Tab, string[]>>
  >({});
  const [instanceSelections, setInstanceSelections] = useState<
    Partial<Record<Tab, string>>
  >({});
  const [navigationOpen, setNavigationOpen] = useState(false);
  const [navCollapsed, setNavCollapsed] = useState(false);
  const [railSelectionPending, setRailSelectionPending] = useState(false);
  const [expandedNav, setExpandedNav] = useState<Partial<Record<Tab, boolean>>>(
    {},
  );
  const selectInstance = useCallback((tab: Tab, instance: string) => {
    setInstanceSelections((previous) => ({ ...previous, [tab]: instance }));
  }, []);
  const [issueSelection, setIssueSelection] = useState<{
    instance: string;
    category: "automation" | "system";
  }>();
  const [issueCount, setIssueCount] = useState<number | null>(null);
  useEffect(() => {
    let mounted = true;
    let busy = false;
    const refresh = async () => {
      if (document.hidden || busy) return;
      busy = true;
      try {
        const summary = await getIssuesSummary();
        if (mounted)
          setIssueCount(summary.available ? summary.activeGroups : null);
      } catch {
        if (mounted) setIssueCount(null);
      } finally {
        busy = false;
      }
    };
    void refresh();
    const interval = window.setInterval(() => void refresh(), 15000);
    const visible = () => {
      if (!document.hidden) void refresh();
    };
    document.addEventListener("visibilitychange", visible);
    window.addEventListener(STATISTICS_UPDATED, visible);
    return () => {
      mounted = false;
      window.clearInterval(interval);
      document.removeEventListener("visibilitychange", visible);
      window.removeEventListener(STATISTICS_UPDATED, visible);
    };
  }, []);
  const [activeTab, setActiveTab] = useState<Tab>("processes");
  const tabScroll = useRef<Partial<Record<Tab, number>>>({});
  useLayoutEffect(() => {
    window.scrollTo(0, tabScroll.current[activeTab] ?? 0);
  }, [activeTab]);
  const [visitedTabs, setVisitedTabs] = useState<ReadonlySet<Tab>>(
    () => new Set<Tab>(["processes"]),
  );
  const [configuredTabs, setConfiguredTabs] = useState<{
    radarr: boolean;
    sonarr: boolean;
    lidarr: boolean;
    readarr: boolean;
    qbittorrent: boolean;
  }>({
    radarr: false,
    sonarr: false,
    lidarr: false,
    readarr: false,
    qbittorrent: false,
  });
  const [manageDirty, setManageDirty] = useState(false);
  const [manageKey, setManageKey] = useState(0);
  const [manageClientId, setManageClientId] = useState<string>();
  const [configDirty, setConfigDirty] = useState(false);
  const [configKey, setConfigKey] = useState(0);
  const { push } = useToast();
  const { setValue: setSearchValue } = useSearch();

  const markTabVisited = useCallback((tabId: Tab) => {
    setVisitedTabs((prev) => {
      if (prev.has(tabId)) {
        return prev;
      }
      const next = new Set(prev);
      next.add(tabId);
      return next;
    });
  }, []);

  const switchTab = useCallback(
    (tabId: Tab, preserveConfigDraft = false) => {
      if (
        activeTab === "maintenance" &&
        tabId !== "maintenance" &&
        manageDirty
      ) {
        if (
          !window.confirm(
            "You have unsaved Manage settings. Discard them and leave?",
          )
        )
          return;
        setManageKey((key) => key + 1);
        setManageDirty(false);
      }
      if (
        activeTab === "config" &&
        tabId !== "config" &&
        configDirty &&
        !preserveConfigDraft
      ) {
        const shouldLeave = window.confirm(
          "You have unsaved configuration changes. Leave without saving?",
        );
        if (!shouldLeave) {
          return;
        }
        // Remount Config so discarded drafts match previous leave-without-saving semantics.
        setConfigKey((prev) => prev + 1);
        setConfigDirty(false);
      }
      if (railSelectionPending) {
        setRailSelectionPending(false);
        setNavCollapsed(true);
      }
      setExpandedNav((current) => (current[tabId] ? current : {}));
      setNavigationOpen(false);
      tabScroll.current[activeTab] = window.scrollY;
      startTransition(() => {
        setActiveTab(tabId);
        markTabVisited(tabId);
      });
      setSearchValue("");
    },
    [
      activeTab,
      configDirty,
      manageDirty,
      markTabVisited,
      railSelectionPending,
      setSearchValue,
    ],
  );
  const { viewDensity, setViewDensity, theme, setTheme, liveArr, setLiveArr } =
    useWebUI();
  const isOnline = useNetworkStatus();
  const [meta, setMeta] = useState<MetaResponse | null>(initialMeta);
  const [metaLoading, setMetaLoading] = useState(false);
  const [displayOpen, setDisplayOpen] = useState(false);
  const [showChangelog, setShowChangelog] = useState(false);
  const [showAlreadyUpToDateModal, setShowAlreadyUpToDateModal] =
    useState(false);
  const [updateBusy, setUpdateBusy] = useState(false);
  const [backendRestarting, setBackendRestarting] = useState(false);
  const restartPollCount = useRef(0);
  const prevUpdateResult = useRef<string | null>(null);
  const backendReadyRef = useRef(false);
  const backendWarnedRef = useRef(false);
  const backendTimerRef = useRef<number | null>(null);
  const [showWelcomeChangelog, setShowWelcomeChangelog] = useState(false);

  // Idle-prefetch lazy route chunks so first visits after cold start are cheaper.
  useEffect(() => {
    const run = () => {
      for (const tabId of Object.keys(TAB_PREFETCHERS) as Tab[]) {
        prefetchTab(tabId);
      }
    };
    if (typeof window.requestIdleCallback === "function") {
      const id = window.requestIdleCallback(run, { timeout: 4000 });
      return () => window.cancelIdleCallback(id);
    }
    const timer = window.setTimeout(run, 2000);
    return () => window.clearTimeout(timer);
  }, []);

  // Theme is now managed by WebUIContext and applied automatically

  const refreshMeta = useCallback(
    async (options?: {
      force?: boolean;
      silent?: boolean;
    }): Promise<MetaResponse | null> => {
      const force = options?.force ?? false;
      const silent = options?.silent ?? !force;
      if (!silent) {
        setMetaLoading(true);
      }
      try {
        const data = await getMeta({ force });
        setMeta(data);
        return data;
      } catch (error) {
        if (!silent) {
          const message =
            error instanceof Error
              ? error.message
              : "Failed to fetch version information";
          push(message, "error");
        }
        return null;
      } finally {
        if (!silent) {
          setMetaLoading(false);
        }
      }
    },
    [push],
  );

  // Soft-reuse AuthGate meta when present; only fetch if we mounted without it.
  // Reserve force for update UI / visibility — quiet 5-min poll covers freshness.
  useEffect(() => {
    if (initialMeta) {
      return;
    }
    const id = window.setTimeout(() => {
      void refreshMeta({ force: false, silent: true });
    }, 0);
    return () => window.clearTimeout(id);
  }, [initialMeta, refreshMeta]);

  // Check for new version on first launch - show welcome popup with changelog
  useEffect(() => {
    if (!meta?.current_version) {
      return;
    }

    let lastSeenVersion: string | null = null;
    try {
      lastSeenVersion = localStorage.getItem("lastSeenVersion");
    } catch {
      /* Version history is optional. */
    }
    const currentVersion = meta.current_version;

    // Show welcome popup if this is a new version (but not on very first install)
    if (lastSeenVersion && lastSeenVersion !== currentVersion) {
      // Ensure we have changelog data before showing popup
      if (!meta.current_version_changelog && !meta.changelog) {
        window.setTimeout(() => {
          void refreshMeta({ force: true, silent: true });
        }, 0);
      }
      window.setTimeout(() => {
        setShowWelcomeChangelog(true);
      }, 0);
    }

    // Store current version as last seen when user opens the app (first install)
    if (!lastSeenVersion) {
      try {
        localStorage.setItem("lastSeenVersion", currentVersion);
      } catch {
        /* Version history is optional. */
      }
    }
  }, [
    meta?.current_version,
    meta?.current_version_changelog,
    meta?.changelog,
    refreshMeta,
  ]);

  // Network status notifications
  useEffect(() => {
    if (!isOnline) {
      push("You are offline. Some features may not work.", "warning");
    }
  }, [isOnline, push]);

  // Keyboard shortcuts
  useEffect(() => {
    const handleKeyDown = (event: KeyboardEvent) => {
      // Don't trigger shortcuts when typing in inputs
      if (
        event.target instanceof HTMLInputElement ||
        event.target instanceof HTMLTextAreaElement ||
        event.target instanceof HTMLSelectElement
      ) {
        return;
      }

      const isMod = event.ctrlKey || event.metaKey;

      // Ctrl/Cmd + K - Focus search
      if (isMod && event.key === "k") {
        event.preventDefault();
        const searchInput = document.querySelector(
          'input[type="text"][placeholder*="Search"]',
        ) as HTMLInputElement;
        searchInput?.focus();
        return;
      }

      // ESC - Clear search
      if (event.key === "Escape") {
        setSearchValue("");
        return;
      }

      // Number keys for visible tab switching
      if (event.key >= "1" && event.key <= "9" && !isMod) {
        event.preventDefault();
        const tabIndex = parseInt(event.key) - 1;
        const tabIds: Tab[] = [
          "processes",
          "issues",
          "logs",
          ...(configuredTabs.radarr ? (["radarr"] as Tab[]) : []),
          ...(configuredTabs.sonarr ? (["sonarr"] as Tab[]) : []),
          ...(configuredTabs.lidarr ? (["lidarr"] as Tab[]) : []),
          ...(configuredTabs.readarr ? (["readarr"] as Tab[]) : []),
          ...(configuredTabs.qbittorrent ? (["qbittorrent"] as Tab[]) : []),
          "maintenance",
          "config",
        ];
        if (tabIndex < tabIds.length) {
          switchTab(tabIds[tabIndex]);
        }
        return;
      }
    };

    window.addEventListener("keydown", handleKeyDown);
    return () => window.removeEventListener("keydown", handleKeyDown);
  }, [setSearchValue, configuredTabs, switchTab]);

  useEffect(() => {
    const onNavigate = (event: Event) => {
      const detail = (event as CustomEvent<NavigateTabDetail>).detail;
      if (!detail?.tab) return;
      const tab = detail.tab as NavigableTab;
      if (
        (tab === "radarr" && !configuredTabs.radarr) ||
        (tab === "sonarr" && !configuredTabs.sonarr) ||
        (tab === "lidarr" && !configuredTabs.lidarr) ||
        (tab === "readarr" && !configuredTabs.readarr) ||
        (tab === "qbittorrent" && !configuredTabs.qbittorrent)
      ) {
        return;
      }
      if (tab === "issues")
        setIssueSelection({
          instance: detail.issueInstance ?? "",
          category: detail.issueCategory ?? "automation",
        });
      if (detail.manageClientId) setManageClientId(detail.manageClientId);
      switchTab(tab as Tab, detail.preserveConfigDraft);
      if (tab === "qbittorrent" && (detail.qbitCategory || detail.qbitInstance))
        selectInstance("qbittorrent", detail.qbitInstance ?? "aggregate");
      if (detail.qbitCategory) {
        try {
          sessionStorage.setItem(
            "qbitrr:focusQbitCategory",
            detail.qbitCategory,
          );
          if (detail.qbitInstance)
            sessionStorage.setItem(
              "torrentarr:focusQbitInstance",
              detail.qbitInstance,
            );
          else sessionStorage.removeItem("torrentarr:focusQbitInstance");
        } catch {
          /* Navigation still works without a persisted focus hint. */
        }
      }
    };
    window.addEventListener(NAVIGATE_TAB_EVENT, onNavigate);
    return () => window.removeEventListener(NAVIGATE_TAB_EVENT, onNavigate);
  }, [configuredTabs, switchTab, selectInstance]);

  useEffect(() => {
    const id = window.setInterval(
      () => {
        void refreshMeta();
      },
      5 * 60 * 1000,
    );
    return () => window.clearInterval(id);
  }, [refreshMeta]);

  const refreshStatus = useCallback(async () => {
    try {
      const status = await getStatus();
      const arrs: ArrInfo[] = Array.isArray(status.arrs) ? status.arrs : [];
      const qbitInstances = status.qbitInstances ?? {};
      const nextInstances: Partial<Record<Tab, string[]>> = {
        qbittorrent: Object.keys(qbitInstances).sort(),
      };
      for (const kind of ["radarr", "sonarr", "lidarr", "readarr"] as const)
        nextInstances[kind] = arrs
          .filter((arr) => arr.type === kind)
          .map((arr) => arr.name)
          .sort();
      setNavInstances((previous) =>
        JSON.stringify(previous) === JSON.stringify(nextInstances)
          ? previous
          : nextInstances,
      );
      setInstanceSelections((previous) => {
        const next = { ...previous };
        for (const [tab, names] of Object.entries(nextInstances)) {
          const key = tab as Tab;
          if (
            !next[key] ||
            (next[key] !== "aggregate" && !names.includes(next[key]!))
          )
            next[key] = names.length === 1 ? names[0] : "aggregate";
        }
        return JSON.stringify(next) === JSON.stringify(previous)
          ? previous
          : next;
      });
      const nextTabs = {
        radarr: arrs.some((arr) => arr.type === "radarr"),
        sonarr: arrs.some((arr) => arr.type === "sonarr"),
        lidarr: arrs.some((arr) => arr.type === "lidarr"),
        readarr: arrs.some((arr) => arr.type === "readarr"),
        qbittorrent: Object.keys(qbitInstances).length > 0,
      };
      setConfiguredTabs((prev) =>
        prev.radarr === nextTabs.radarr &&
        prev.sonarr === nextTabs.sonarr &&
        prev.lidarr === nextTabs.lidarr &&
        prev.readarr === nextTabs.readarr &&
        prev.qbittorrent === nextTabs.qbittorrent
          ? prev
          : nextTabs,
      );
    } catch {
      // Silently fail - status is not critical
    }
  }, []);

  useEffect(() => {
    const handleVisibilityChange = () => {
      if (document.visibilityState === "visible") {
        // Soft-refresh only: keep-alive already preserves tab state. Avoid remount
        // loads and non-silent meta that toast transient network failures.
        void refreshMeta({ force: true, silent: true });
        void refreshStatus();
      }
    };

    const onConfigSaved = () => {
      void refreshStatus();
    };
    window.addEventListener("torrentarr:config-saved", onConfigSaved);
    document.addEventListener("visibilitychange", handleVisibilityChange);
    return () => {
      window.removeEventListener("torrentarr:config-saved", onConfigSaved);
      document.removeEventListener("visibilitychange", handleVisibilityChange);
    };
  }, [refreshMeta, refreshStatus]);

  useEffect(() => {
    const initialId = window.setTimeout(() => {
      void refreshStatus();
    }, 0);
    const id = window.setInterval(() => {
      void refreshStatus();
    }, 15 * 1000); // Refresh Arr/qBit tab visibility; client TTL covers status overlap
    return () => {
      window.clearTimeout(initialId);
      window.clearInterval(id);
    };
  }, [refreshStatus]);

  useEffect(() => {
    if (!meta?.update_state?.in_progress && !backendRestarting) {
      restartPollCount.current = 0;
      return;
    }
    const id = window.setInterval(async () => {
      try {
        const data = await getMeta({ force: true });
        setMeta(data);
        if (backendRestarting) {
          // Backend came back after restart
          window.location.reload();
        }
        restartPollCount.current = 0;
      } catch {
        restartPollCount.current += 1;
        if (restartPollCount.current > 20) {
          // 60 seconds
          setBackendRestarting(false);
          restartPollCount.current = 0;
          push(
            "Update completed but backend restart timed out. Please refresh the page manually.",
            "warning",
          );
          return;
        }
        if (meta?.update_state?.in_progress) {
          // Failed while update in progress, likely restarting
          setBackendRestarting(true);
        }
      }
    }, 3000);
    return () => window.clearInterval(id);
  }, [meta?.update_state?.in_progress, backendRestarting, meta, push]);

  useEffect(() => {
    const state = meta?.update_state;
    if (!state) {
      prevUpdateResult.current = null;
      return;
    }
    const result = state.last_result ?? null;
    if (result && result !== prevUpdateResult.current) {
      if (result === "success") {
        push("Update completed successfully. Restarting...", "success");
        window.setTimeout(() => {
          setBackendRestarting(true);
        }, 0);
        restartPollCount.current = 0;
      } else if (result === "error") {
        push(state.last_error || "Update failed.", "error");
      }
    }
    prevUpdateResult.current = result;
  }, [meta?.update_state, push]);

  useEffect(() => {
    let cancelled = false;
    let attempts = 0;

    const schedule = (delay: number) => {
      if (backendTimerRef.current !== null) {
        window.clearTimeout(backendTimerRef.current);
      }
      backendTimerRef.current = window.setTimeout(() => {
        void poll();
      }, delay);
    };

    const poll = async () => {
      if (cancelled || backendReadyRef.current) {
        return;
      }
      attempts += 1;
      try {
        const status = await getStatus();
        if (cancelled) {
          return;
        }
        const readyHint =
          status.ready ??
          (Array.isArray(status.arrs) && status.arrs.length > 0);
        if (readyHint) {
          backendReadyRef.current = true;
          return;
        }
        if (
          status.ready === false &&
          attempts >= 3 &&
          !backendWarnedRef.current
        ) {
          backendWarnedRef.current = true;
          push(
            "Torrentarr backend is still initialising. Check the logs if this persists.",
            "warning",
          );
        }
      } catch (error) {
        if (!backendWarnedRef.current && attempts >= 3) {
          backendWarnedRef.current = true;
          const detail =
            error instanceof Error ? error.message : "Unknown backend error";
          push(
            `Unable to confirm Torrentarr readiness (${detail}). Please inspect the logs.`,
            "warning",
          );
        }
      } finally {
        if (!cancelled && !backendReadyRef.current) {
          const delay = attempts < 3 ? 3000 : 10000;
          schedule(delay);
        }
      }
    };

    schedule(0);

    return () => {
      cancelled = true;
      if (backendTimerRef.current !== null) {
        window.clearTimeout(backendTimerRef.current);
        backendTimerRef.current = null;
      }
    };
  }, [push]);

  const tabs = useMemo<NavTab[]>(() => {
    const nextTabs: NavTab[] = [
      { id: "processes", label: "Processes", icon: ProcessesIcon },
      { id: "issues", label: "Issues", icon: IssuesIcon },
      { id: "logs", label: "Logs", icon: LogsIcon },
    ];
    if (configuredTabs.radarr) {
      nextTabs.push({
        id: "radarr",
        label: arrTypeLabel("radarr"),
        icon: RadarrIcon,
      });
    }
    if (configuredTabs.sonarr) {
      nextTabs.push({
        id: "sonarr",
        label: arrTypeLabel("sonarr"),
        icon: SonarrIcon,
      });
    }
    if (configuredTabs.lidarr) {
      nextTabs.push({
        id: "lidarr",
        label: arrTypeLabel("lidarr"),
        icon: LidarrIcon,
      });
    }
    if (configuredTabs.readarr) {
      nextTabs.push({
        id: "readarr",
        label: arrTypeLabel("readarr"),
        icon: ReadarrIcon,
      });
    }
    if (configuredTabs.qbittorrent) {
      nextTabs.push({
        id: "qbittorrent",
        label: "Torrent Clients",
        icon: QbitIcon,
      });
    }
    nextTabs.push({
      id: "maintenance",
      label: "Manage",
      icon: ConfigIcon,
    });
    nextTabs.push({ id: "config", label: "Config", icon: ConfigIcon });
    return nextTabs;
  }, [configuredTabs]);
  const visibleTabIds = useMemo(
    () => new Set<Tab>(tabs.map((tab) => tab.id)),
    [tabs],
  );

  const repositoryUrl =
    meta?.repository_url ?? "https://github.com/Feramance/Torrentarr";
  const displayVersion = meta?.current_version
    ? formatVersionLabel(meta.current_version)
    : "...";
  const latestVersion = meta?.latest_version ?? null;
  const updateAvailable = Boolean(meta?.update_available);
  const updateState = meta?.update_state;
  const changelogUrl = meta?.changelog_url ?? repositoryUrl;

  const versionTitleParts: string[] = [];
  if (meta?.last_checked) {
    versionTitleParts.push(
      `Last checked ${new Date(meta.last_checked).toLocaleString()}`,
    );
  }
  if (meta?.error) {
    versionTitleParts.push(`Update check failed: ${meta.error}`);
  }
  const versionTitle = versionTitleParts.length
    ? versionTitleParts.join(" • ")
    : undefined;

  // Redirect to processes if active tab is no longer available
  useEffect(() => {
    const tabExists = tabs.some((tab) => tab.id === activeTab);
    if (!tabExists && tabs.length > 0) {
      const id = window.setTimeout(() => {
        startTransition(() => {
          setActiveTab("processes");
          markTabVisited("processes");
        });
      }, 0);
      return () => window.clearTimeout(id);
    }
  }, [tabs, activeTab, markTabVisited]);

  const handleCheckUpdates = useCallback(async () => {
    const data = await refreshMeta({ force: true });
    if (data) {
      if (data.update_available) {
        setShowChangelog(true);
      } else {
        setShowAlreadyUpToDateModal(true);
      }
    }
  }, [refreshMeta]);

  const handleOpenChangelog = useCallback(() => {
    setShowChangelog(true);
    if (!meta?.changelog) {
      void refreshMeta({ force: true, silent: true });
    }
  }, [meta?.changelog, refreshMeta]);

  const handleCloseChangelog = useCallback(() => {
    setShowChangelog(false);
  }, []);

  const handleCloseAlreadyUpToDateModal = useCallback(() => {
    setShowAlreadyUpToDateModal(false);
  }, []);

  const handleCloseWelcomeChangelog = () => {
    setShowWelcomeChangelog(false);
    // Mark this version as seen
    if (meta?.current_version) {
      try {
        localStorage.setItem("lastSeenVersion", meta.current_version);
      } catch {
        /* Version history is optional. */
      }
    }
  };

  const handleTriggerUpdate = useCallback(async () => {
    setUpdateBusy(true);
    setBackendRestarting(false);
    restartPollCount.current = 0;
    try {
      await triggerUpdate();
      push("Update started in the background.", "info");
      await refreshMeta({ force: true, silent: true });
    } catch (error) {
      const message =
        error instanceof Error ? error.message : "Failed to start update";
      push(message, "error");
    } finally {
      setUpdateBusy(false);
    }
  }, [push, refreshMeta]);

  const wikiIcon = (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      aria-hidden="true"
    >
      <path d="M12 5v15M3 4c4-1 6 0 9 2 3-2 5-3 9-2v15c-4-1-6 0-9 2-3-2-5-3-9-2Z" />
    </svg>
  );

  const resourceLinks = (
    <div
      className="sidebar__resources"
      role="group"
      aria-label="Project resources"
    >
      <a
        href="https://feramance.github.io/Torrentarr/"
        target="_blank"
        rel="noopener noreferrer"
        title="Wiki"
        aria-label="Wiki (opens in a new tab)"
      >
        {wikiIcon}
      </a>
      <a
        href={repositoryUrl}
        target="_blank"
        rel="noopener noreferrer"
        title="GitHub"
        aria-label="GitHub (opens in a new tab)"
      >
        <IconImage src={GitHubIcon} />
      </a>
      <a
        href={webPath("/web/docs")}
        target="_blank"
        rel="noopener noreferrer"
        title="OpenAPI docs"
        aria-label="OpenAPI docs (opens in a new tab)"
      >
        <IconImage src={LogsIcon} />
      </a>
    </div>
  );
  const burgerIcon = (
    <svg
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      aria-hidden="true"
    >
      <path d="M4 7h16M4 12h16M4 17h16" />
    </svg>
  );
  const themeIcon = (
    <svg viewBox="0 0 24 24" fill="none" aria-hidden="true">
      <circle cx="12" cy="12" r="9" stroke="currentColor" strokeWidth="1.7" />
      <path d="M12 3a9 9 0 0 0 0 18Z" fill="currentColor" />
    </svg>
  );
  const navFooter = (scope: string) => (
    <div className="sidebar__footer">
      <button
        type="button"
        className="btn ghost sidebar__display"
        aria-label="Display"
        title="Display"
        aria-haspopup="dialog"
        aria-expanded={displayOpen}
        onClick={() => {
          if (scope === "drawer") setNavigationOpen(false);
          setDisplayOpen(true);
        }}
      >
        {themeIcon}
      </button>
      {resourceLinks}
    </div>
  );
  const navigation = (scope: string) => (
    <nav className="nav" aria-label="Main navigation">
      {[
        { label: "Monitor", ids: ["processes", "issues", "logs"] },
        {
          label: "Libraries & clients",
          ids: ["radarr", "sonarr", "lidarr", "readarr", "qbittorrent"],
        },
        { label: "Administration", ids: ["maintenance", "config"] },
      ].map((group) => {
        const items = tabs.filter((tab) => group.ids.includes(tab.id));
        return items.length ? (
          <div className="nav-group" key={group.label}>
            <div className="nav-group__label">{group.label}</div>
            {items.map((tab) => {
              const instances = navInstances[tab.id];
              const hasChoices = Boolean(instances && instances.length > 0);
              return (
                <div className="nav-entry" key={tab.id}>
                  <div className="nav-group__heading">
                    <button
                      type="button"
                      className={activeTab === tab.id ? "active" : ""}
                      title={tab.label}
                      onClick={safeClick(() => {
                        if (instances) {
                          if (scope === "drawer" && hasChoices) {
                            setExpandedNav((value) =>
                              value[tab.id] ? {} : { [tab.id]: true },
                            );
                            return;
                          }
                          if (
                            scope === "sidebar" &&
                            hasChoices &&
                            instances.length > 1 &&
                            (navCollapsed || railSelectionPending)
                          ) {
                            setNavCollapsed(false);
                            setRailSelectionPending(true);
                            setExpandedNav({ [tab.id]: true });
                            return;
                          }
                          selectInstance(
                            tab.id,
                            instances.length === 1
                              ? instances[0]!
                              : "aggregate",
                          );
                          if (hasChoices) setExpandedNav({ [tab.id]: true });
                        }
                        switchTab(tab.id);
                      })}
                      onMouseEnter={() => prefetchTab(tab.id)}
                      onFocus={() => prefetchTab(tab.id)}
                    >
                      <IconImage
                        src={tab.icon}
                        className={instances ? undefined : "nav-utility-icon"}
                      />
                      <span className="nav-label">{tab.label}</span>
                      {tab.id === "issues" && (
                        <span
                          className="issue-count"
                          aria-label={
                            issueCount == null
                              ? "Issue count unavailable"
                              : `${issueCount} active issue groups`
                          }
                        >
                          {issueCount ?? "?"}
                        </span>
                      )}
                    </button>
                    {hasChoices && (
                      <button
                        type="button"
                        className="nav-disclosure"
                        aria-label={`Expand ${tab.label}`}
                        aria-expanded={!!expandedNav[tab.id]}
                        aria-controls={`${scope}-nav-${tab.id}`}
                        onClick={() =>
                          setExpandedNav((value) =>
                            value[tab.id] ? {} : { [tab.id]: true },
                          )
                        }
                      >
                        {expandedNav[tab.id] ? "▾" : "▸"}
                      </button>
                    )}
                  </div>
                  {hasChoices && expandedNav[tab.id] && (
                    <div
                      className="nav-instances"
                      id={`${scope}-nav-${tab.id}`}
                    >
                      {(instances?.length === 1
                        ? instances
                        : ["aggregate", ...(instances ?? [])]
                      ).map((instance) => (
                        <button
                          key={instance}
                          type="button"
                          className={
                            activeTab === tab.id &&
                            instanceSelections[tab.id] === instance
                              ? "active"
                              : ""
                          }
                          aria-current={
                            activeTab === tab.id &&
                            instanceSelections[tab.id] === instance
                              ? "page"
                              : undefined
                          }
                          onClick={(event) => {
                            if (scope === "sidebar" && railSelectionPending)
                              event.currentTarget
                                .closest(".nav-entry")
                                ?.querySelector<HTMLButtonElement>(
                                  ".nav-group__heading > button",
                                )
                                ?.focus();
                            selectInstance(tab.id, instance);
                            switchTab(tab.id);
                          }}
                        >
                          {instance === "aggregate"
                            ? `All ${tab.label}`
                            : instance}
                        </button>
                      ))}
                    </div>
                  )}
                </div>
              );
            })}
          </div>
        ) : null;
      })}
    </nav>
  );

  const selectedTabInstance = instanceSelections[activeTab];
  const activeTabLabel = tabs.find((tab) => tab.id === activeTab)?.label;
  const pageTitle =
    selectedTabInstance && selectedTabInstance !== "aggregate"
      ? selectedTabInstance
      : activeTabLabel && (navInstances[activeTab]?.length ?? 0) > 1
        ? `All ${activeTabLabel}`
        : activeTabLabel;
  return (
    <div
      data-density={viewDensity}
      data-nav={navCollapsed ? "collapsed" : "expanded"}
      className="console-shell"
    >
      <aside className="console-sidebar" id="app-navigation">
        <div className="sidebar__brand">
          <IconImage src={logoUrl} alt="Torrentarr Logo" />
          <strong>Torrentarr</strong>
        </div>
        {navigation("sidebar")}
        {navFooter("sidebar")}
      </aside>
      {navigationOpen && (
        <ShellDialog
          title="Navigation"
          className="navigation-drawer"
          onClose={() => setNavigationOpen(false)}
        >
          {navigation("drawer")}
          {navFooter("drawer")}
        </ShellDialog>
      )}
      {displayOpen && (
        <ShellDialog title="Display" onClose={() => setDisplayOpen(false)}>
          <fieldset>
            <legend>Theme</legend>
            <div className="view-density-toggle">
              {THEME_OPTIONS.map((option) => (
                <button
                  key={option.value}
                  type="button"
                  aria-pressed={theme === option.value}
                  className={theme === option.value ? "active" : ""}
                  onClick={() => setTheme(option.value)}
                >
                  {option.label}
                </button>
              ))}
            </div>
          </fieldset>
          <fieldset>
            <legend>View density</legend>
            <div className="view-density-toggle">
              <button
                type="button"
                aria-pressed={viewDensity === "comfortable"}
                className={viewDensity === "comfortable" ? "active" : ""}
                onClick={() => setViewDensity("comfortable")}
              >
                Comfortable
              </button>
              <button
                type="button"
                aria-pressed={viewDensity === "compact"}
                className={viewDensity === "compact" ? "active" : ""}
                onClick={() => setViewDensity("compact")}
              >
                Compact
              </button>
            </div>
          </fieldset>
        </ShellDialog>
      )}
      <header className="appbar">
        <div className="appbar__inner">
          <div className="appbar__title">
            <button
              type="button"
              className="btn ghost nav-rail-trigger appbar__icon-button"
              aria-label={
                navCollapsed ? "Expand navigation" : "Collapse navigation"
              }
              title={navCollapsed ? "Expand navigation" : "Collapse navigation"}
              aria-controls="app-navigation"
              aria-expanded={!navCollapsed}
              onClick={() => {
                setRailSelectionPending(false);
                setNavCollapsed((value) => !value);
              }}
            >
              {burgerIcon}
            </button>
            <button
              type="button"
              className="btn ghost navigation-trigger appbar__icon-button"
              aria-label="Menu"
              title="Menu"
              aria-haspopup="dialog"
              aria-expanded={navigationOpen}
              onClick={() => setNavigationOpen(true)}
            >
              {burgerIcon}
            </button>
            <h1>{pageTitle}</h1>
            <span className="appbar__version" title={versionTitle}>
              {displayVersion}
            </span>
            {metaLoading ? (
              <span className="spinner" aria-hidden="true" />
            ) : null}
            {updateState?.in_progress ? (
              <span className="appbar__status text-info">Updating...</span>
            ) : null}
            {updateAvailable ? (
              <button
                type="button"
                className="btn small primary appbar__update"
                onClick={handleOpenChangelog}
                disabled={updateBusy || Boolean(updateState?.in_progress)}
                aria-label="Update available"
              >
                <span className="appbar__update-indicator" aria-hidden="true" />
                <IconImage src={UpdateIcon} />
                <span className="btn__label">Update available</span>
              </button>
            ) : null}
          </div>
          <div className="appbar__actions">
            {!isOnline && (
              <span
                className="badge appbar__offline"
                style={{
                  background: "rgba(239, 68, 68, 0.15)",
                  borderColor: "rgba(239, 68, 68, 0.3)",
                  color: "var(--danger)",
                }}
              >
                Offline
              </span>
            )}
            <div className="live-switch">
              <span className="live-switch__label" id="live-switch-label">
                Live
              </span>
              <button
                type="button"
                className={`live-switch__control${liveArr ? " live-switch__control--on" : ""}`}
                role="switch"
                aria-checked={liveArr}
                aria-labelledby="live-switch-label"
                title="Live queries to Arr services; local statistics refresh automatically"
                aria-label="Live queries to Arr services; local statistics refresh automatically"
                onClick={() => setLiveArr(!liveArr)}
              >
                <span className="live-switch__track" aria-hidden="true">
                  <span className="live-switch__thumb" />
                </span>
              </button>
            </div>
            <button
              type="button"
              className="btn small ghost appbar__icon-button"
              onClick={() => void handleCheckUpdates()}
              disabled={metaLoading}
              aria-label="Check Updates"
              title="Check Updates"
            >
              <IconImage src={RefreshIcon} />
            </button>
            {authRequired && (
              <button
                type="button"
                className="btn small ghost"
                onClick={() => void onSignOut()}
              >
                Sign Out
              </button>
            )}
          </div>
        </div>
      </header>
      <main className="container">
        <Suspense fallback={<div className="loading">Loading...</div>}>
          <div className="view-transition">
            {visitedTabs.has("issues") && (
              <div hidden={activeTab !== "issues"}>
                <IssuesView
                  active={activeTab === "issues"}
                  selection={issueSelection}
                />
              </div>
            )}
            {visitedTabs.has("processes") ? (
              <div hidden={activeTab !== "processes"}>
                <ProcessesView active={activeTab === "processes"} />
              </div>
            ) : null}
            {visitedTabs.has("logs") ? (
              <div hidden={activeTab !== "logs"}>
                <LogsView active={activeTab === "logs"} />
              </div>
            ) : null}
            {visitedTabs.has("radarr") && visibleTabIds.has("radarr") ? (
              <div hidden={activeTab !== "radarr"}>
                <ArrCatalogView
                  kind="radarr"
                  active={activeTab === "radarr"}
                  selectedInstance={instanceSelections.radarr}
                  onSelectInstance={(instance) =>
                    selectInstance("radarr", instance)
                  }
                />
              </div>
            ) : null}
            {visitedTabs.has("sonarr") && visibleTabIds.has("sonarr") ? (
              <div hidden={activeTab !== "sonarr"}>
                <ArrCatalogView
                  kind="sonarr"
                  active={activeTab === "sonarr"}
                  selectedInstance={instanceSelections.sonarr}
                  onSelectInstance={(instance) =>
                    selectInstance("sonarr", instance)
                  }
                />
              </div>
            ) : null}
            {visitedTabs.has("lidarr") && visibleTabIds.has("lidarr") ? (
              <div hidden={activeTab !== "lidarr"}>
                <ArrCatalogView
                  kind="lidarr"
                  active={activeTab === "lidarr"}
                  selectedInstance={instanceSelections.lidarr}
                  onSelectInstance={(instance) =>
                    selectInstance("lidarr", instance)
                  }
                />
              </div>
            ) : null}
            {visitedTabs.has("readarr") && visibleTabIds.has("readarr") ? (
              <div hidden={activeTab !== "readarr"}>
                <ArrCatalogView
                  kind="readarr"
                  active={activeTab === "readarr"}
                  selectedInstance={instanceSelections.readarr}
                  onSelectInstance={(instance) =>
                    selectInstance("readarr", instance)
                  }
                />
              </div>
            ) : null}
            {visitedTabs.has("qbittorrent") &&
            visibleTabIds.has("qbittorrent") ? (
              <div hidden={activeTab !== "qbittorrent"}>
                <QbitCategoriesView
                  active={activeTab === "qbittorrent"}
                  selectedInstance={instanceSelections.qbittorrent}
                  onSelectInstance={(instance) =>
                    selectInstance("qbittorrent", instance)
                  }
                />
              </div>
            ) : null}
            {visitedTabs.has("maintenance") ? (
              <div hidden={activeTab !== "maintenance"}>
                <MaintenanceView
                  key={manageKey}
                  active={activeTab === "maintenance"}
                  requestedClientId={manageClientId}
                  onDirtyChange={setManageDirty}
                />
              </div>
            ) : null}
            {visitedTabs.has("config") ? (
              <div hidden={activeTab !== "config"}>
                <ConfigView
                  active={activeTab === "config"}
                  key={`config-${configKey}`}
                  onDirtyChange={setConfigDirty}
                />
              </div>
            ) : null}
          </div>
        </Suspense>
      </main>
      <Suspense fallback={null}>
        {showChangelog && meta ? (
          <ChangelogModal
            variant="updateAvailable"
            currentVersion={meta.current_version}
            latestVersion={latestVersion}
            changelog={meta.changelog}
            changelogUrl={changelogUrl}
            repositoryUrl={repositoryUrl}
            updateState={updateState}
            updating={updateBusy}
            installationType={meta.installation_type}
            updateChannel={meta.update_channel}
            autoUpdateSupported={meta.auto_update_supported}
            binaryDownloadUrl={meta.binary_download_url}
            binaryDownloadName={meta.binary_download_name}
            binaryDownloadSize={meta.binary_download_size}
            binaryDownloadError={meta.binary_download_error}
            onClose={handleCloseChangelog}
            onUpdate={handleTriggerUpdate}
          />
        ) : null}
        {showWelcomeChangelog && meta ? (
          <ChangelogModal
            variant="welcome"
            currentVersion={meta.current_version}
            changelog={meta.current_version_changelog || meta.changelog}
            changelogUrl={changelogUrl}
            repositoryUrl={repositoryUrl}
            onClose={handleCloseWelcomeChangelog}
          />
        ) : null}
        {showAlreadyUpToDateModal && meta ? (
          <ChangelogModal
            variant="upToDate"
            currentVersion={meta.current_version}
            changelog={meta.current_version_changelog || meta.changelog}
            changelogUrl={changelogUrl}
            repositoryUrl={repositoryUrl}
            updateChannel={meta.update_channel}
            onClose={handleCloseAlreadyUpToDateModal}
          />
        ) : null}
      </Suspense>
    </div>
  );
}

export { AppShell };
