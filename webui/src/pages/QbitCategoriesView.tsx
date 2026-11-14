import { BreakdownBar } from "../components/Statistics";
import type { QbitActivitySummary } from "../api/types";
import { TransferHistory } from "../components/TransferHistory";
import {
  useInstanceSelection,
  type InstanceSelectionProps,
} from "../hooks/useInstanceSelection";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
} from "react";
import { getQbitOverview, getStatus } from "../api/client";
import type { QbitOverviewCategory, QbitTorrentOverview } from "../api/types";
import { QbitTorrentListRow } from "../components/QbitTorrentListRow";
import { useToast } from "../context/ToastContext";
import { useInterval } from "../hooks/useInterval";
import { isSeedingState } from "../utils/qbitTorrentDisplay";
import { TorrentAutomationIssues } from "../components/TorrentAutomationIssues";
import { ArrCatalogBodyChrome } from "./arrCatalog/ArrCatalogBodyChrome";

function formatBytes(bytes: number): string {
  if (bytes === 0) return "0 B";
  const k = 1024;
  const sizes = ["B", "KB", "MB", "GB", "TB"];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / k ** i).toFixed(2)} ${sizes[i]}`;
}

function formatTime(seconds: number): string {
  const totalSeconds = Math.round(seconds);
  if (totalSeconds === 0) return "0s";

  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const secs = totalSeconds % 60;

  const parts: string[] = [];
  if (days > 0) parts.push(`${days}d`);
  if (hours > 0) parts.push(`${hours}h`);
  if (minutes > 0) parts.push(`${minutes}m`);
  if (secs > 0 || parts.length === 0) parts.push(`${secs}s`);

  return parts.join(" ");
}

function getRemoveModeText(mode: number): string {
  switch (mode) {
    case -1:
      return "Never";
    case 1:
      return "On Ratio";
    case 2:
      return "On Time";
    case 3:
      return "Ratio OR Time";
    case 4:
      return "Ratio AND Time";
    default:
      return "Unknown";
  }
}

function reconcileQbitSelection(
  instances: string[],
  current: string | "aggregate" | "",
): string | "aggregate" {
  if (!instances.length) {
    return "aggregate";
  }
  if (instances.length === 1) {
    return instances[0]!;
  }
  if (current === "" || current === "aggregate") {
    return "aggregate";
  }
  if (!instances.includes(current)) {
    return "aggregate";
  }
  return current;
}

function categorySectionKey(cat: QbitOverviewCategory): string {
  const arrPart = cat.arrName ?? "";
  return `${cat.qbitInstance}:${cat.category}:${cat.managedBy}:${arrPart}`;
}

function seedingSummary(cat: QbitOverviewCategory): string {
  if (!cat.seedingConfig) return "Mixed seeding policies";
  const { maxRatio, maxTime, removeMode } = cat.seedingConfig;
  const ratio =
    maxRatio === -1 ? "ratio off" : `max ratio ${maxRatio.toFixed(2)}`;
  const time = maxTime === -1 ? "time off" : `max time ${formatTime(maxTime)}`;
  return `${ratio} · ${time} · ${getRemoveModeText(removeMode)}`;
}

function torrentMatchesQuery(torrent: QbitTorrentOverview, q: string): boolean {
  if (torrent.name.toLowerCase().includes(q)) {
    return true;
  }
  return torrent.tags.some((tag) => tag.toLowerCase().includes(q));
}

interface FilteredCategory extends QbitOverviewCategory {
  /** Torrents after search filter (same as torrents when no search). */
  visibleTorrents: QbitTorrentOverview[];
}

function filterCategories(
  categories: QbitOverviewCategory[],
  search: string,
): FilteredCategory[] {
  const q = search.trim().toLowerCase();
  const sorted = [...categories].sort((a, b) => {
    const inst = a.qbitInstance.localeCompare(b.qbitInstance);
    if (inst !== 0) return inst;
    return a.category.localeCompare(b.category);
  });

  if (!q) {
    return sorted.map((cat) => ({ ...cat, visibleTorrents: cat.torrents }));
  }

  const filtered: FilteredCategory[] = [];
  for (const cat of sorted) {
    const categoryHit =
      cat.category.toLowerCase().includes(q) ||
      (cat.arrName?.toLowerCase().includes(q) ?? false) ||
      cat.qbitInstance.toLowerCase().includes(q);
    const matchingTorrents = cat.torrents.filter((t) =>
      torrentMatchesQuery(t, q),
    );
    if (!categoryHit && matchingTorrents.length === 0) {
      continue;
    }
    const visibleTorrents = categoryHit ? cat.torrents : matchingTorrents;
    const seedingCount = visibleTorrents.filter((t) =>
      isSeedingState(t.state),
    ).length;
    const totalSize = visibleTorrents.reduce((sum, t) => sum + t.size, 0);
    filtered.push({
      ...cat,
      torrentCount: categoryHit ? cat.torrentCount : visibleTorrents.length,
      seedingCount: categoryHit ? cat.seedingCount : seedingCount,
      totalSize: categoryHit ? cat.totalSize : totalSize,
      visibleTorrents,
    });
  }
  return filtered;
}

interface QbitCategoriesViewProps extends InstanceSelectionProps {
  active: boolean;
}

export function QbitCategoriesView({
  active,
  selectedInstance,
  onSelectInstance,
}: QbitCategoriesViewProps): JSX.Element {
  const [instances, setInstances] = useState<string[]>([]);
  const [selection, setSelection] = useInstanceSelection({
    selectedInstance,
    onSelectInstance,
  });
  const [activity, setActivity] = useState<QbitActivitySummary[]>([]);
  const [categories, setCategories] = useState<QbitOverviewCategory[]>([]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [search, setSearch] = useState("");
  const [loading, setLoading] = useState(false);
  const [instancesLoaded, setInstancesLoaded] = useState(false);
  const [overviewLoaded, setOverviewLoaded] = useState(false);
  const { push } = useToast();
  const [scanInterval, setScanInterval] = useState(30);
  const [observations, setObservations] = useState<
    Record<
      string,
      {
        observedAt: string | null;
        error: string | null;
        scanIntervalSeconds: number;
      }
    >
  >({});
  const isFetching = useRef<string | null>(null);
  const requestId = useRef(0);
  const selectionRef = useRef(selection);
  selectionRef.current = selection;
  const [loadedSelection, setLoadedSelection] = useState("");

  const isAggregate = selection === "aggregate";

  const loadInstances = useCallback(async () => {
    try {
      const status = await getStatus();
      const names = Object.keys(status.qbitInstances ?? {}).sort((a, b) =>
        a.localeCompare(b),
      );
      setInstances(names);
      setSelection((prev) => reconcileQbitSelection(names, prev));
    } catch (error) {
      push(
        error instanceof Error
          ? error.message
          : "Failed to load qBit instances",
        "error",
      );
    } finally {
      setInstancesLoaded(true);
    }
  }, [push, setSelection]);

  const loadOverview = useCallback(
    async (showLoading = true) => {
      if (isFetching.current === selection || !selection) {
        return;
      }
      isFetching.current = selection;
      const id = ++requestId.current;
      if (showLoading) {
        setLoading(true);
      }
      try {
        const instanceArg = selection === "aggregate" ? undefined : selection;
        const data = await getQbitOverview(instanceArg);
        if (selectionRef.current !== selection || requestId.current !== id)
          return;
        setLoadedSelection(selection);
        setCategories(data.categories);
        setActivity(data.summaries ?? []);
        setObservations(data.observations ?? {});
        const intervals = Object.values(data.observations ?? {}).map(
          (value) => value.scanIntervalSeconds,
        );
        if (intervals.length) setScanInterval(Math.min(...intervals));
        if (data.instances.length) {
          setInstances((prev) => {
            const merged = Array.from(
              new Set([...prev, ...data.instances]),
            ).sort((a, b) => a.localeCompare(b));
            return merged.length === prev.length &&
              merged.every((name, i) => name === prev[i])
              ? prev
              : merged;
          });
        }
      } catch (error) {
        // Background Live polls must stay silent — avoid network-error toast spam.
        if (
          showLoading &&
          selectionRef.current === selection &&
          requestId.current === id
        ) {
          push(
            error instanceof Error
              ? error.message
              : "Failed to load qBit overview",
            "error",
          );
        }
      } finally {
        if (requestId.current === id) {
          isFetching.current = null;
          if (selectionRef.current === selection) {
            setOverviewLoaded(true);
            setLoading(false);
          }
        }
      }
    },
    [push, selection],
  );

  useEffect(() => {
    const id = window.setTimeout(() => {
      setOverviewLoaded(false);
    }, 0);
    return () => window.clearTimeout(id);
  }, [selection]);

  useEffect(() => {
    if (!active) {
      return;
    }
    const id = window.setTimeout(() => {
      void loadInstances();
    }, 0);
    return () => window.clearTimeout(id);
  }, [active, loadInstances]);

  useEffect(() => {
    if (!active || !selection) {
      return;
    }
    const id = window.setTimeout(() => {
      void loadOverview();
    }, 0);
    return () => window.clearTimeout(id);
  }, [active, selection, loadOverview]);

  useInterval(
    () => {
      void loadOverview(false);
    },
    active && selection ? scanInterval * 1000 : null,
  );

  const handleRefresh = useCallback(() => {
    void loadInstances();
    void loadOverview();
  }, [loadInstances, loadOverview]);

  const toggleSection = useCallback((key: string) => {
    setExpanded((prev) => ({ ...prev, [key]: !prev[key] }));
  }, []);

  const filteredCategories = useMemo(
    () => filterCategories(categories, search),
    [categories, search],
  );

  // Honor Processes deep-link focus (expand that category only).
  useEffect(() => {
    if (!active) {
      return;
    }
    let focusCategory: string | null;
    let focusInstance: string | null;
    try {
      focusCategory = sessionStorage.getItem("qbitrr:focusQbitCategory");
      focusInstance = sessionStorage.getItem("torrentarr:focusQbitInstance");
    } catch {
      return;
    }
    if (!focusCategory) return;
    if (search) {
      setSearch("");
      return;
    }
    if (!focusInstance && selection !== "aggregate" && instances.length > 1) {
      setSelection("aggregate");
      return;
    }
    if (focusInstance && selection !== focusInstance) {
      setSelection(focusInstance);
      return;
    }
    if (
      loadedSelection !== selection ||
      !filteredCategories.some(
        (cat) =>
          cat.category === focusCategory &&
          (!focusInstance || cat.qbitInstance === focusInstance),
      )
    )
      return;
    const id = window.setTimeout(() => {
      try {
        sessionStorage.removeItem("qbitrr:focusQbitCategory");
        sessionStorage.removeItem("torrentarr:focusQbitInstance");
      } catch {
        /* Still expand the requested category. */
      }
      setExpanded((prev) => {
        let changed = false;
        const next = { ...prev };
        for (const cat of filteredCategories) {
          const key = categorySectionKey(cat);
          if (
            cat.category === focusCategory &&
            (!focusInstance || cat.qbitInstance === focusInstance) &&
            next[key] !== true
          ) {
            next[key] = true;
            changed = true;
          }
        }
        return changed ? next : prev;
      });
    }, 0);
    return () => window.clearTimeout(id);
  }, [
    active,
    filteredCategories,
    selection,
    loadedSelection,
    setSelection,
    search,
    instances.length,
  ]);

  const summary = useMemo(() => {
    const matches = new Map(
      filteredCategories.flatMap((cat) =>
        cat.visibleTorrents.map(
          (torrent) =>
            [`${cat.qbitInstance}:${torrent.hash}`, torrent] as const,
        ),
      ),
    );
    const rows = [...matches.values()];
    const useFullTotals = !search.trim() && activity.length > 0;
    const totalTorrents = useFullTotals
      ? activity.reduce((sum, item) => sum + item.torrentCount, 0)
      : rows.length;
    const totalSeeding = useFullTotals
      ? activity.reduce((sum, item) => sum + item.seeding, 0)
      : rows.filter((t) => isSeedingState(t.state)).length;
    const totalSize = useFullTotals
      ? activity.reduce((sum, item) => sum + item.totalSize, 0)
      : rows.reduce((sum, t) => sum + t.size, 0);
    const qbitCount = filteredCategories.filter(
      (cat) => cat.managedBy === "qbit",
    ).length;
    const arrCount = filteredCategories.filter(
      (cat) => cat.managedBy === "arr",
    ).length;

    return {
      totalTorrents,
      totalSeeding,
      totalSize,
      qbitCount,
      arrCount,
      categoryCount: filteredCategories.length,
    };
  }, [filteredCategories, activity, search]);

  const incomplete = Object.values(observations).some(
    (observation) => observation.observedAt == null,
  );
  const summaryLine = incomplete ? (
    <span className="observation-stale">
      Awaiting complete client statistics
    </span>
  ) : (
    <>
      <div>
        Monitored-category torrent overview
        {search.trim() ? " · Search of loaded torrents" : ""}
        {selection && selection !== "aggregate"
          ? ` · ${selection}`
          : instances.length > 1
            ? " · All Torrent Clients"
            : ""}
      </div>
      <div className="qbit-summary-stats">
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">Categories</span>
          <span className="qbit-summary-stat__value">
            {summary.categoryCount}
          </span>
        </div>
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">qBit-managed</span>
          <span className="qbit-summary-stat__value">{summary.qbitCount}</span>
        </div>
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">Arr-managed</span>
          <span className="qbit-summary-stat__value">{summary.arrCount}</span>
        </div>
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">Torrents</span>
          <span className="qbit-summary-stat__value">
            {summary.totalTorrents.toLocaleString()}
          </span>
        </div>
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">Seeding</span>
          <span className="qbit-summary-stat__value">
            {summary.totalSeeding.toLocaleString()}
          </span>
        </div>
        <div className="qbit-summary-stat">
          <span className="qbit-summary-stat__label">Total size</span>
          <span className="qbit-summary-stat__value">
            {formatBytes(summary.totalSize)}
          </span>
        </div>
      </div>
    </>
  );

  const showInitialLoading =
    !instancesLoaded ||
    (Boolean(selection) && !overviewLoaded && categories.length === 0) ||
    (loading && categories.length === 0);

  let body: JSX.Element;
  if (showInitialLoading) {
    body = <></>;
  } else if (!instances.length) {
    body = (
      <div className="hint">
        No qBittorrent instances found. Configure a `[qBit]` section to use this
        view.
      </div>
    );
  } else if (categories.length === 0) {
    body = (
      <div className="hint">
        No monitored categories found. Configure ManagedCategories in your qBit
        config sections or add Arr instances.
      </div>
    );
  } else if (filteredCategories.length === 0) {
    body = (
      <div className="hint">No categories or torrents match your search.</div>
    );
  } else {
    body = (
      <div className="qbit-category-sections">
        {filteredCategories.map((cat) => {
          const key = categorySectionKey(cat);
          const isOpen = Boolean(expanded[key]);
          return (
            <section key={key} className="qbit-category-section">
              <button
                type="button"
                className="qbit-category-section__header"
                aria-expanded={isOpen}
                onClick={() => toggleSection(key)}
              >
                <span
                  className={`qbit-category-section__chevron${
                    isOpen ? " is-open" : ""
                  }`}
                  aria-hidden
                />
                <span className="qbit-category-section__title">
                  <span className="qbit-category-section__name">
                    {cat.category}
                  </span>
                  {cat.managedBy === "qbit" ? (
                    <span className="badge badge-qbit">qBit</span>
                  ) : (
                    <span className="badge badge-arr">
                      Arr
                      {cat.arrName ? ` · ${cat.arrName}` : ""}
                    </span>
                  )}
                  {isAggregate && (
                    <span className="badge">{cat.qbitInstance}</span>
                  )}
                </span>
                <span className="qbit-category-section__meta">
                  {cat.torrentCount.toLocaleString()} torrents ·{" "}
                  {cat.seedingCount.toLocaleString()} seeding ·{" "}
                  {formatBytes(cat.totalSize)}
                </span>
                <span className="qbit-category-section__policy hint">
                  {seedingSummary(cat)}
                </span>
              </button>
              {isOpen && (
                <div className="qbit-category-section__body">
                  <div className="hint">
                    Average ratio {cat.avgRatio.toFixed(2)} · Average seed time{" "}
                    {formatTime(cat.avgSeedingTime)}
                  </div>
                  {cat.visibleTorrents.length === 0 ? (
                    <div className="hint">
                      No torrents in this category on {cat.qbitInstance}.
                    </div>
                  ) : (
                    <div className="qbit-torrent-list">
                      {cat.torrentsTruncated ? (
                        <div className="hint">
                          Showing first{" "}
                          {cat.visibleTorrents.length.toLocaleString()} of{" "}
                          {cat.torrentCount.toLocaleString()} torrents.
                        </div>
                      ) : null}
                      {cat.visibleTorrents.map((torrent) => (
                        <QbitTorrentListRow
                          key={torrent.hash || torrent.name}
                          torrent={torrent}
                        />
                      ))}
                    </div>
                  )}
                </div>
              )}
            </section>
          );
        })}
      </div>
    );
  }

  const cardTitle =
    selection && selection !== "aggregate" ? selection : "Torrent Clients";
  return (
    <section className="card" aria-label={cardTitle}>
      <div className="card-body">
        <div className="catalog-workspace">
          <div className="pane">
            <div
              className="row"
              style={{
                alignItems: "flex-end",
                gap: "12px",
                flexWrap: "wrap",
              }}
            >
              <div className="col field" style={{ flex: "1 1 200px" }}>
                <label htmlFor="qbit-search">Search</label>
                <input
                  id="qbit-search"
                  placeholder="Filter categories or torrents…"
                  value={search}
                  onChange={(event) => setSearch(event.target.value)}
                  disabled={!instances.length}
                />
              </div>
            </div>

            {Object.entries(observations).map(([id, value]) =>
              value.error ? (
                <p key={id} role="status" className="observation-stale">
                  {id}: {value.error}{" "}
                  {value.observedAt
                    ? `Last updated ${new Date(value.observedAt).toLocaleString()}`
                    : "Awaiting first successful scan."}
                </p>
              ) : null,
            )}
            <TransferHistory
              active={active}
              instance={
                selection === "aggregate" ? undefined : selection || undefined
              }
            />
            {loadedSelection === selection && activity.length > 0 && (
              <div className="activity-summary">
                <h3>Monitored categories</h3>
                <BreakdownBar
                  active={active}
                  label="Monitored torrent activity, deduplicated across categories"
                  values={[
                    {
                      label: "Downloading",
                      value: activity.reduce(
                        (sum, item) => sum + item.downloading,
                        0,
                      ),
                      tone: "teal",
                    },
                    {
                      label: "Seeding",
                      value: activity.reduce(
                        (sum, item) => sum + item.seeding,
                        0,
                      ),
                      tone: "orange",
                    },
                    {
                      label: "Stopped / paused",
                      value: activity.reduce(
                        (sum, item) => sum + item.stopped,
                        0,
                      ),
                      tone: "muted",
                    },
                    {
                      label: "Other",
                      value: activity.reduce(
                        (sum, item) => sum + item.other,
                        0,
                      ),
                      tone: "subtle",
                    },
                  ]}
                />
                <p className="hint">
                  {activity
                    .reduce((sum, item) => sum + item.torrentCount, 0)
                    .toLocaleString()}{" "}
                  unique torrents · independent of category search
                  {activity.length <
                  (selection === "aggregate" ? instances.length : 1)
                    ? " · Some clients unavailable; showing partial coverage"
                    : ""}
                </p>
              </div>
            )}
            <TorrentAutomationIssues clientId={selection} />
            <ArrCatalogBodyChrome
              summaryLine={summaryLine}
              onRefresh={handleRefresh}
              loading={showInitialLoading}
              loadingHint="Loading overview…"
              hasRows={categories.length > 0}
            >
              {body}
            </ArrCatalogBodyChrome>
          </div>
        </div>
      </div>
    </section>
  );
}
