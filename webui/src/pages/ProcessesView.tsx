import { ArrActivityCharts } from "../components/ArrActivityCharts";
import { useStatisticsUpdates } from "../hooks/useStatisticsUpdates";
import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type JSX,
} from "react";
import {
  getProcesses,
  getFreeSpaceStatistics,
  getQbitCategories,
  getStatus,
  rebuildArrs,
  restartAllProcesses,
  restartProcess,
} from "../api/client";
import type {
  ClientObservation,
  FreeSpaceStatistics,
  ProcessInfo,
  QbitCategory,
  StatusResponse,
} from "../api/types";
import { useToast } from "../context/ToastContext";
import { ARR_TYPES, arrTypeLabel } from "../config/arrSections";
import { useInterval } from "../hooks/useInterval";
import { ConfirmDialog } from "../components/ConfirmDialog";
import { MetricStrip } from "../components/Statistics";
import { requestNavigateTab } from "../utils/navigateTab";

const QUALITY_TOKEN_REGEX =
  /\b(480p|576p|720p|1080p|2160p|4k|8k|web[-_. ]?(?:dl|rip)|hdrip|hdtv|bluray|bd(?:rip)?|brrip|webrip|remux|x264|x265|hevc|dts|truehd|atmos|proper|repack|dvdrip|hdr|amzn|nf)\b/i;
const EPISODE_TOKEN_REGEX = /\bS\d{1,3}E\d{1,3}\b/i;
const SEASON_TOKEN_REGEX = /\bSeason\s+\d+\b/i;

function sanitizeSearchSummary(raw: string): string {
  const trimmed = raw.trim();
  if (!trimmed) return "";

  // Keep "X queued items" messages as-is (don't filter them out)
  if (/^\d+\s+queued item/i.test(trimmed)) {
    return trimmed;
  }

  const normalized = trimmed.replace(/\s+/g, " ");
  const releaseMatch = normalized.match(
    /^(?<title>.+?)\s+(?<year>(?:19|20)\d{2})(?:\s+(?<rest>.*))?$/,
  );

  if (releaseMatch) {
    const rest = releaseMatch.groups?.rest ?? "";
    const looksLikeEpisode =
      EPISODE_TOKEN_REGEX.test(rest) || SEASON_TOKEN_REGEX.test(rest);
    if (rest && !looksLikeEpisode && QUALITY_TOKEN_REGEX.test(rest)) {
      const rawTitle = releaseMatch.groups?.title ?? "";
      const cleanedTitle = rawTitle
        .replace(/[-_.]/g, " ")
        .replace(/\s{2,}/g, " ")
        .trim();
      const year = releaseMatch.groups?.year ?? "";
      if (cleanedTitle) {
        return year ? `${cleanedTitle} (${year})` : cleanedTitle;
      }
    }
  }

  return normalized;
}

function isProcessEqual(a: ProcessInfo, b: ProcessInfo): boolean {
  return (
    a.searchesTriggered === b.searchesTriggered &&
    a.itemsSearched === b.itemsSearched &&
    a.downloadingCount === b.downloadingCount &&
    a.seedingCount === b.seedingCount &&
    a.observedAt === b.observedAt &&
    a.observationError === b.observationError &&
    a.pausedLastPass === b.pausedLastPass &&
    a.resumedLastPass === b.resumedLastPass &&
    a.handledLastPass === b.handledLastPass &&
    a.monitoredCategoryCount === b.monitoredCategoryCount &&
    a.category === b.category &&
    a.name === b.name &&
    a.kind === b.kind &&
    a.pid === b.pid &&
    a.alive === b.alive &&
    (a.rebuilding ?? false) === (b.rebuilding ?? false) &&
    (a.searchSummary ?? "") === (b.searchSummary ?? "") &&
    (a.searchTimestamp ?? "") === (b.searchTimestamp ?? "") &&
    (a.queueCount ?? null) === (b.queueCount ?? null) &&
    (a.categoryCount ?? null) === (b.categoryCount ?? null) &&
    (a.freeSpacePaused ?? null) === (b.freeSpacePaused ?? null) &&
    (a.metricType ?? "") === (b.metricType ?? "") &&
    a.status === b.status &&
    a.restartKey === b.restartKey &&
    a.restartable === b.restartable &&
    JSON.stringify(a.torrent) === JSON.stringify(b.torrent) &&
    JSON.stringify(a.search) === JSON.stringify(b.search)
  );
}

function areProcessListsEqual(a: ProcessInfo[], b: ProcessInfo[]): boolean {
  if (a === b) return true;
  if (a.length !== b.length) return false;
  for (let index = 0; index < a.length; index += 1) {
    if (!isProcessEqual(a[index], b[index])) {
      return false;
    }
  }
  return true;
}

function getRefreshDelay(active: boolean): number | null {
  if (!active) return null;
  return 2000;
}

function formatKind(kind: string): string {
  return kind ? kind.charAt(0).toUpperCase() + kind.slice(1) : kind;
}

function formatRelativeTime(iso: string): string | null {
  const ts = Date.parse(iso);
  if (Number.isNaN(ts)) return null;
  const deltaSec = Math.max(0, Math.round((Date.now() - ts) / 1000));
  if (deltaSec < 45) return "just now";
  if (deltaSec < 3600) {
    const mins = Math.max(1, Math.round(deltaSec / 60));
    return `${mins}m ago`;
  }
  if (deltaSec < 86400) {
    const hours = Math.max(1, Math.round(deltaSec / 3600));
    return `${hours}h ago`;
  }
  const days = Math.max(1, Math.round(deltaSec / 86400));
  return `${days}d ago`;
}

type StatusTone = "ok" | "bad" | "partial" | "neutral" | "";

function statusToneClass(tone: StatusTone): string {
  if (tone === "ok") return "process-status process-status--ok";
  if (tone === "bad") return "process-status process-status--bad";
  if (tone === "partial") return "process-status process-status--partial";
  return "process-status process-status--neutral";
}

function formatStorage(bytes: number | null): string {
  if (bytes == null) return "—";
  return `${(bytes / 1024 ** 3).toLocaleString(undefined, { maximumFractionDigits: 2 })} GiB`;
}

function Count({
  label,
  value,
}: {
  label: string;
  value?: number | null;
}): JSX.Element {
  return (
    <span className="process-count">
      <span>{label}</span>{" "}
      <strong>{value == null ? "—" : value.toLocaleString()}</strong>
    </span>
  );
}

function ActivityRow({
  item,
  kind,
  onOpen,
}: {
  item: ProcessInfo;
  kind: "search" | "torrent";
  onOpen?: () => void;
}): JSX.Element {
  const loop = item[kind];
  const state = item.alive ? loop?.phase ?? "Running" : "Stopped";
  const timestamp = kind === "search" ? item.searchTimestamp : item.observedAt;
  const summary = kind === "search" ? item.searchSummary?.trim() : null;
  const showSummary = summary && !/^\d+ searches triggered/.test(summary);
  const Element = kind === "torrent" && onOpen ? "button" : "div";
  return (
    <Element
      className="process-activity"
      type={Element === "button" ? "button" : undefined}
      onClick={Element === "button" ? onOpen : undefined}
      aria-label={
        Element === "button" ? `View torrents for ${item.name}` : undefined
      }
    >
      <div className="process-activity__heading">
        <span
          className={`process-status__dot process-status__dot--${item.alive && !/error|timed out/i.test(state) ? "ok" : "bad"}`}
          aria-label={state}
          title={state}
        />
        <strong>{kind === "search" ? "Search" : "Torrents"}</strong>
        <span className="hint">{state}</span>
      </div>
      <div className="process-activity__counts">
        {kind === "search" ? (
          <>
            <Count label="Searches" value={item.searchesTriggered} />
            <Count label="Items" value={item.itemsSearched} />
          </>
        ) : (
          <>
            <Count label="Managed" value={item.categoryCount} />
            <Count label="Downloading" value={item.downloadingCount} />
            <Count label="Seeding" value={item.seedingCount} />
            <Count label="Arr queue" value={item.queueCount} />
          </>
        )}
      </div>
      <div className="process-activity__detail">
        {showSummary ? (
          <span>{summary}</span>
        ) : kind === "search" && item.searchesTriggered == null ? (
          <span>No active search</span>
        ) : null}
        {timestamp ? (
          <time
            dateTime={timestamp}
            title={new Date(timestamp).toLocaleString()}
          >
            {kind === "search" ? "Last pass" : "Scanned"}{" "}
            {formatRelativeTime(timestamp)}
          </time>
        ) : (
          <span>
            {kind === "search"
              ? "Awaiting first completed pass"
              : "Awaiting first successful scan"}
          </span>
        )}
        {item.alive && loop?.wakeAt ? (
          <span>Next {new Date(loop.wakeAt).toLocaleTimeString()}</span>
        ) : null}
      </div>
    </Element>
  );
}

function ProcessActivity({
  item,
  onOpen,
}: {
  item: ProcessInfo;
  onOpen?: () => void;
}): JSX.Element {
  return (
    <>
      {item.metricType === "free-space" ? (
        <div className="process-activity__counts">
          <Count label="Paused for free space" value={item.freeSpacePaused} />
          <Count label="Paused last pass" value={item.pausedLastPass} />
          <Count label="Resumed last pass" value={item.resumedLastPass} />
        </div>
      ) : item.kind === "category" ? (
        <div className="process-activity__counts">
          <Count label="Handled last pass" value={item.handledLastPass} />
          <Count label="Torrents in category" value={item.categoryCount} />
          <Count
            label="Categories monitored"
            value={item.monitoredCategoryCount}
          />
        </div>
      ) : (
        <>
          {item.kind !== "torrent" && <ActivityRow item={item} kind="search" />}
          {item.kind !== "search" && (
            <ActivityRow item={item} kind="torrent" onOpen={onOpen} />
          )}
        </>
      )}
      {item.observationError ? (
        <p className="observation-stale">{item.observationError}</p>
      ) : null}
    </>
  );
}

function QbitCategoryChip({
  cat,
  instanceAlive,
  onOpen,
}: {
  cat: QbitCategory;
  instanceAlive: boolean;
  onOpen: () => void;
}): JSX.Element {
  const aliveLabel = instanceAlive ? "Online" : "Offline";
  const parts = [`${cat.torrentCount.toLocaleString()} torrents`];
  if (cat.seedingCount > 0) {
    parts.push(`${cat.seedingCount.toLocaleString()} seeding`);
  }
  const summary = parts.join(" · ");
  return (
    <button
      type="button"
      className="process-chip process-chip--info process-chip--link"
      title={`Open ${cat.category} in qBittorrent`}
      onClick={onOpen}
    >
      <div className="process-chip__row">
        <span
          className={`process-status__dot ${
            instanceAlive
              ? "process-status__dot--ok"
              : "process-status__dot--bad"
          }`}
          title={aliveLabel}
          aria-label={aliveLabel}
        />
        <span className="process-chip__name">{cat.category}</span>
      </div>
      <div className="process-chip__summary" title={summary}>
        {summary}
      </div>
    </button>
  );
}

interface ProcessesViewProps {
  active: boolean;
}

export function ProcessesView({ active }: ProcessesViewProps): JSX.Element {
  const [clientObservations, setClientObservations] = useState<
    Record<string, ClientObservation>
  >({});
  const [storage, setStorage] = useState<FreeSpaceStatistics[]>([]);
  const [processes, setProcesses] = useState<ProcessInfo[]>([]);
  const [loading, setLoading] = useState(false);
  const [hasLoaded, setHasLoaded] = useState(false);
  const [hasSnapshot, setHasSnapshot] = useState(false);
  const [restartingAll, setRestartingAll] = useState(false);
  const [rebuildingArrs, setRebuildingArrs] = useState(false);
  const [statusData, setStatusData] = useState<StatusResponse | null>(null);
  const [qbitCategories, setQbitCategories] = useState<QbitCategory[]>([]);
  const [confirmAction, setConfirmAction] = useState<{
    title: string;
    message: string;
    onConfirm: () => void;
    danger?: boolean;
  } | null>(null);
  const { push } = useToast();
  const isFetching = useRef(false);

  const load = useCallback(
    async (showLoading = true) => {
      if (isFetching.current) {
        return;
      }
      isFetching.current = true;
      if (showLoading) {
        setLoading(true);
      }
      try {
        const [processData, status, categoriesData, storageData] =
          await Promise.all([
            getProcesses(),
            getStatus(),
            getQbitCategories().catch(() => null),
            getFreeSpaceStatistics().catch(() => null),
          ]);
        const next = (processData.processes ?? []).map((process) => {
          if (typeof process.searchSummary === "string") {
            const sanitized = sanitizeSearchSummary(process.searchSummary);
            return {
              ...process,
              searchSummary: sanitized,
            };
          }
          return process;
        });
        setProcesses((prev) =>
          areProcessListsEqual(prev, next) ? prev : next,
        );
        setHasSnapshot(true);
        setStatusData(status);
        if (storageData) setStorage(storageData);
        if (categoriesData?.categories) {
          setQbitCategories(categoriesData.categories);
          setClientObservations(categoriesData.observations ?? {});
        }
      } catch (error) {
        // Background polls must stay silent — transient network blips spam toasts otherwise.
        if (showLoading) {
          push(
            error instanceof Error
              ? error.message
              : "Failed to load processes list",
            "error",
          );
        }
      } finally {
        isFetching.current = false;
        setHasLoaded(true);
        if (showLoading) {
          setLoading(false);
        }
      }
    },
    [push],
  );

  useEffect(() => {
    const id = window.setTimeout(() => {
      void load();
    }, 0);
    return () => window.clearTimeout(id);
  }, [load]);

  useEffect(() => {
    if (active) {
      const id = window.setTimeout(() => {
        void load();
      }, 0);
      return () => window.clearTimeout(id);
    }
  }, [active, load]);

  useStatisticsUpdates(active, () => {
    void load(false);
  });

  const refreshDelay = useMemo(() => getRefreshDelay(active), [active]);

  useInterval(() => {
    void load(false);
  }, refreshDelay);

  const handleRestart = useCallback(
    async (category: string, kind: string) => {
      try {
        await restartProcess(category, kind);
        push(`Restarted ${category}:${kind}`, "success");
        void load();
      } catch (error) {
        push(
          error instanceof Error
            ? error.message
            : `Failed to restart ${category}:${kind}`,
          "error",
        );
      }
    },
    [load, push],
  );

  const handleRestartAll = useCallback(() => {
    setConfirmAction({
      title: "Restart All Processes",
      message:
        "Are you sure you want to restart all processes? This will temporarily interrupt all operations.",
      danger: true,
      onConfirm: async () => {
        setConfirmAction(null);
        setRestartingAll(true);
        try {
          await restartAllProcesses();
          push("Restarted all processes", "success");
          void load();
        } catch (error) {
          push(
            error instanceof Error ? error.message : "Failed to restart all",
            "error",
          );
        } finally {
          setRestartingAll(false);
        }
      },
    });
  }, [load, push]);

  const handleRebuildArrs = useCallback(() => {
    setConfirmAction({
      title: "Rebuild Arrs",
      message:
        "Are you sure you want to rebuild all Arr instances? This will refresh all connections and may take some time.",
      danger: true,
      onConfirm: async () => {
        setConfirmAction(null);
        setRebuildingArrs(true);
        try {
          await rebuildArrs();
          push("Requested Arr rebuild", "success");
          void load();
        } catch (error) {
          push(
            error instanceof Error ? error.message : "Failed to rebuild Arrs",
            "error",
          );
        } finally {
          setRebuildingArrs(false);
        }
      },
    });
  }, [load, push]);

  const handleRestartGroup = useCallback(
    (items: ProcessInfo[]) => {
      const label = items[0]?.name ?? "group";
      setConfirmAction({
        title: "Restart All Processes",
        message: `Restart all processes for ${label}?`,
        danger: true,
        onConfirm: async () => {
          setConfirmAction(null);
          try {
            await Promise.all(
              items
                .filter((item) => item.restartable ?? item.kind === "worker")
                .map((item) =>
                  restartProcess(item.restartKey ?? item.category, item.kind),
                ),
            );
            push(`Restarted ${label}`, "success");
            void load();
          } catch (error) {
            push(
              error instanceof Error
                ? error.message
                : "Failed to restart process group",
              "error",
            );
          }
        },
      });
    },
    [load, push],
  );

  const groupedProcesses = useMemo(() => {
    interface Instance {
      name: string;
      items: ProcessInfo[];
    }
    interface AppGroup {
      app: string;
      instances: Instance[];
    }
    const appBuckets = new Map<string, Map<string, ProcessInfo[]>>();
    const arrs = statusData?.arrs ?? [];
    const clients = Object.keys(statusData?.qbitInstances ?? {});
    const clientBucket = new Map<string, ProcessInfo[]>(
      clients.map((name) => [name, []]),
    );
    for (const proc of processes) {
      if (clientBucket.has(proc.name)) {
        if (proc.pid != null) clientBucket.get(proc.name)!.push(proc);
        continue;
      }
      if (
        proc.kind.toLowerCase() === "category" &&
        qbitCategories.some(
          (cat) =>
            cat.managedBy === "qbit" &&
            cat.category === proc.category &&
            proc.name === `qbit-${cat.category}` &&
            clients.includes(cat.instance),
        )
      )
        continue;
      const arr = arrs.find((arr) => arr.name === proc.name);
      if (!arr && /^(radarr|sonarr|lidarr|readarr)/i.test(proc.name)) continue;
      const app = arr ? arrTypeLabel(arr.type) : "Other";
      if (!appBuckets.has(app)) appBuckets.set(app, new Map());
      const instances = appBuckets.get(app)!;
      const name = proc.name || proc.category;
      if (!instances.has(name)) instances.set(name, []);
      instances.get(name)!.push(proc);
    }
    if (clients.length) appBuckets.set("Torrent Clients", clientBucket);
    const appOrder = [
      ...ARR_TYPES.map((type) => arrTypeLabel(type)),
      "Torrent Clients",
      "Other",
    ];

    const result: AppGroup[] = Array.from(appBuckets.entries())
      .map(([app, instances]) => {
        const sortedInstances = Array.from(instances.entries())
          .map(([name, items]) => ({
            name,
            items: items.sort((a, b) => a.kind.localeCompare(b.kind)),
          }))
          .sort((a, b) => a.name.localeCompare(b.name));
        return { app, instances: sortedInstances };
      })
      .filter((group) => group.instances.length);

    result.sort((a, b) => {
      const order = (label: string) => {
        const index = appOrder.indexOf(label);
        return index === -1 ? Number.MAX_SAFE_INTEGER : index;
      };
      return order(a.app) - order(b.app) || a.app.localeCompare(b.app);
    });

    return result;
  }, [processes, statusData, qbitCategories]);

  const cardsByApp = groupedProcesses.map(({ app, instances }) => {
    const instanceCount = instances.length;
    const processCount = instances.reduce(
      (sum, instance) => sum + instance.items.length,
      0,
    );
    const cards = instances.map(({ name: instanceName, items }) => {
      const instanceCategories =
        app === "Torrent Clients"
          ? qbitCategories.filter(
              (cat) =>
                cat.instance === instanceName && cat.managedBy === "qbit",
            )
          : [];
      const name = instanceName;
      const arr = statusData?.arrs.find((entry) => entry.name === name);
      const category =
        arr?.category ||
        items.find((item) => item.kind === "worker" || item.kind === "torrent")
          ?.category;
      const matchingClients = [
        ...new Set(
          qbitCategories
            .filter(
              (cat) => cat.category.toLowerCase() === category?.toLowerCase(),
            )
            .map((cat) => cat.instance),
        ),
      ];
      const openTorrents =
        arr && category
          ? () =>
              requestNavigateTab({
                tab: "qbittorrent",
                qbitCategory: category,
                qbitInstance:
                  matchingClients.length === 1 ? matchingClients[0] : undefined,
              })
          : undefined;
      const runningCount = items.filter((item) => item.alive).length;
      const totalCount = items.length;
      const qbitInstanceKey = app === "Torrent Clients" ? instanceName : null;
      const qbitInstanceAlive =
        qbitInstanceKey != null
          ? statusData?.qbitInstances?.[qbitInstanceKey]?.alive ?? false
          : null;

      const tone: StatusTone =
        totalCount === 0 && qbitInstanceAlive !== null
          ? qbitInstanceAlive
            ? "ok"
            : "bad"
          : totalCount === 0
            ? "neutral"
            : runningCount === totalCount
              ? "ok"
              : runningCount === 0
                ? "bad"
                : "partial";

      const statusLabel =
        totalCount === 0 && qbitInstanceAlive !== null
          ? qbitInstanceAlive
            ? "Instance running"
            : "Instance stopped"
          : totalCount === 0
            ? "No processes"
            : runningCount === totalCount
              ? "All running"
              : runningCount === 0
                ? "Stopped"
                : `${runningCount}/${totalCount} running`;
      const displayName =
        name === "FreeSpaceManager"
          ? "Free Space Manager"
          : name === "TorrentPolicyManager"
            ? "Torrent Policy Manager"
            : name;
      const uniqueKinds = Array.from(new Set(items.map((item) => item.kind)));
      const filteredKinds = uniqueKinds.filter((kind) => {
        const lower = kind.toLowerCase();
        return !["search", "torrent", "worker", "category"].includes(lower);
      });

      return (
        <div className="process-card" key={name}>
          <div className="process-card__header">
            <div className="process-card__title">
              <div className="process-card__name">{displayName}</div>
              {app !== "Torrent Clients" && filteredKinds.length ? (
                <div className="process-card__badges">
                  {filteredKinds.map((kind) => (
                    <span
                      key={`${name}:${kind}:badge`}
                      className="process-card__badge"
                    >
                      {formatKind(kind)}
                    </span>
                  ))}
                </div>
              ) : null}
            </div>
            <div className="process-card__header-actions">
              <button
                type="button"
                className="btn small ghost"
                aria-label={`View issues for ${displayName}`}
                onClick={() =>
                  requestNavigateTab({
                    tab: "issues",
                    issueInstance: app === "Other" ? undefined : name,
                    issueCategory: "system",
                  })
                }
              >
                Issues
              </button>
              <div
                className={statusToneClass(tone)}
                title={statusLabel}
                aria-label={statusLabel}
              >
                <span className="process-status__dot" aria-hidden="true" />
              </div>
              {items.some(
                (item) => item.restartable ?? item.kind === "worker",
              ) && (
                <details className="process-actions">
                  <summary className="btn small ghost">Actions</summary>
                  <div className="process-actions__menu">
                    {" "}
                    {items.length === 1 &&
                    items[0] != null &&
                    (items[0].restartable ?? items[0].kind === "worker") ? (
                      <button
                        className="btn small ghost process-action-btn"
                        type="button"
                        onClick={() =>
                          void handleRestart(
                            items[0]!.restartKey ?? items[0]!.name,
                            items[0]!.kind,
                          )
                        }
                      >
                        Restart {formatKind(items[0]!.kind).toLowerCase()}
                      </button>
                    ) : null}
                    {items.filter(
                      (item) => item.restartable ?? item.kind === "worker",
                    ).length > 1 ? (
                      <button
                        className="btn small ghost process-action-btn"
                        type="button"
                        onClick={() => {
                          handleRestartGroup(items);
                        }}
                      >
                        Restart All
                      </button>
                    ) : null}
                  </div>
                </details>
              )}
            </div>
          </div>
          <div
            className="process-activities"
            aria-label={`${displayName} activity`}
          >
            {app === "Torrent Clients"
              ? (() => {
                  const rows = qbitCategories.filter(
                    (category) => category.instance === instanceName,
                  );
                  const observation = clientObservations[instanceName];
                  if (observation && !observation.observedAt)
                    return (
                      <span className="observation-stale">
                        {observation.error ??
                          "Awaiting first successful client scan."}
                      </span>
                    );
                  return rows.length ? (
                    <>
                      <Count
                        label="Managed torrents"
                        value={rows.reduce(
                          (sum, row) => sum + row.torrentCount,
                          0,
                        )}
                      />
                      <Count
                        label="Downloading"
                        value={
                          rows.every((row) => row.downloadingCount != null)
                            ? rows.reduce(
                                (sum, row) => sum + row.downloadingCount!,
                                0,
                              )
                            : null
                        }
                      />
                      <Count
                        label="Seeding"
                        value={rows.reduce(
                          (sum, row) => sum + row.seedingCount,
                          0,
                        )}
                      />
                      {(rows.some((row) => row.observationError) ||
                        observation?.error) && (
                        <p className="observation-stale">
                          {observation?.error ??
                            "Showing last successful client scan."}
                        </p>
                      )}
                      {observation?.observedAt && (
                        <time className="hint">
                          Client scan{" "}
                          {new Date(observation.observedAt).toLocaleString()}
                        </time>
                      )}
                    </>
                  ) : (
                    <span className="hint">Awaiting client statistics</span>
                  );
                })()
              : items.map((item) => (
                  <ProcessActivity
                    key={`${item.name}:${item.kind}`}
                    item={item}
                    onOpen={openTorrents}
                  />
                ))}
          </div>
          {name === "FreeSpaceManager" &&
            storage.map((disk) => (
              <div
                className="storage-allocation"
                key={`${disk.clientInstanceId}:${disk.path}`}
              >
                <strong>
                  {disk.clientInstanceId} · {disk.path}
                </strong>
                <dl>
                  <dt>Free</dt>
                  <dd>{formatStorage(disk.freeBytes)}</dd>
                  <dt>Reserve</dt>
                  <dd>{formatStorage(disk.thresholdBytes)}</dd>
                  <dt>Committed downloads</dt>
                  <dd>{formatStorage(disk.committedBytes)}</dd>
                  <dt>Remaining allocation</dt>
                  <dd>{formatStorage(disk.remainingAllocationBytes)}</dd>
                </dl>
                {disk.error || disk.blockedReason ? (
                  <p className="observation-stale">
                    {disk.error ?? disk.blockedReason}
                  </p>
                ) : null}
                <span className="hint">
                  {disk.lastCompletedPass
                    ? `Last pass ${new Date(disk.lastCompletedPass).toLocaleString()}`
                    : "Awaiting first allocation pass"}
                </span>
              </div>
            ))}
          <div className="process-card__list">
            {instanceCategories.map((cat) => (
              <QbitCategoryChip
                key={`cat:${cat.instance}:${cat.category}`}
                cat={cat}
                instanceAlive={
                  statusData?.qbitInstances?.[cat.instance]?.alive ?? false
                }
                onOpen={() =>
                  requestNavigateTab({
                    tab: "qbittorrent",
                    qbitCategory: cat.category,
                    qbitInstance: cat.instance,
                  })
                }
              />
            ))}
          </div>
        </div>
      );
    });
    return { app, cards, instanceCount, processCount };
  });

  const byOwner = new Map<string, { queue: number; paused: number }>();
  for (const process of processes) {
    const key = process.restartKey ?? process.name;
    const previous = byOwner.get(key) ?? { queue: 0, paused: 0 };
    byOwner.set(key, {
      queue: Math.max(previous.queue, process.queueCount ?? 0),
      paused: Math.max(previous.paused, process.freeSpacePaused ?? 0),
    });
  }
  const totals = [...byOwner.values()];
  const workerStates = new Map<string, boolean>();
  for (const process of processes) {
    if (process.restartable === false) continue;
    const key = process.restartKey ?? process.name;
    workerStates.set(key, (workerStates.get(key) ?? false) || process.alive);
  }
  return (
    <>
      <section className="card" aria-label="Processes">
        <div className="card-body stack">
          <MetricStrip
            label="Process statistics"
            items={[
              {
                label: "Workers online",
                value: hasSnapshot
                  ? `${[...workerStates.values()].filter(Boolean).length} / ${workerStates.size}`
                  : "—",
              },
              {
                label: "Queued items",
                value: hasSnapshot
                  ? totals
                      .reduce((sum, item) => sum + item.queue, 0)
                      .toLocaleString()
                  : "—",
              },
              {
                label: "Paused for space",
                value: hasSnapshot
                  ? totals
                      .reduce((sum, item) => sum + item.paused, 0)
                      .toLocaleString()
                  : "—",
              },
            ]}
          />
          <div className="row">
            <div className="col inline">
              <button
                className="btn ghost"
                type="button"
                onClick={() => {
                  void load();
                  window.dispatchEvent(
                    new Event("torrentarr:statistics-refresh"),
                  );
                }}
                disabled={loading}
              >
                {loading && <span className="spinner" />}
                {loading ? "Refreshing..." : "Refresh"}
              </button>
              <button
                className="btn ghost"
                type="button"
                onClick={() => void handleRestartAll()}
                disabled={restartingAll}
              >
                {restartingAll && <span className="spinner" />}
                {restartingAll ? "Restarting..." : "Restart All"}
              </button>
              <button
                className="btn ghost"
                type="button"
                onClick={() => void handleRebuildArrs()}
                disabled={rebuildingArrs}
              >
                {rebuildingArrs && <span className="spinner" />}
                {rebuildingArrs ? "Rebuilding..." : "Rebuild Arrs"}
              </button>
            </div>
          </div>
          <ArrActivityCharts active={active} />
          {!hasLoaded && loading ? (
            <div className="loading">
              <span className="spinner" /> Loading processes…
            </div>
          ) : cardsByApp.length ? (
            cardsByApp.map(({ app, cards, instanceCount, processCount }) => (
              <div className="process-section" key={app}>
                <div className="process-section__title">
                  <span className="process-section__name">{app}</span>
                  <span className="process-section__meta">
                    {instanceCount}{" "}
                    {instanceCount === 1 ? "instance" : "instances"} ·{" "}
                    {processCount}{" "}
                    {processCount === 1 ? "process" : "processes"}
                  </span>
                </div>
                <div className="process-grid">{cards}</div>
              </div>
            ))
          ) : (
            <div className="empty-state">No processes available.</div>
          )}
        </div>
      </section>
      {confirmAction && (
        <ConfirmDialog
          title={confirmAction.title}
          message={confirmAction.message}
          confirmLabel="Confirm"
          cancelLabel="Cancel"
          danger={confirmAction.danger ?? true}
          onConfirm={confirmAction.onConfirm}
          onCancel={() => setConfirmAction(null)}
        />
      )}
    </>
  );
}
