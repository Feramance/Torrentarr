import { Fragment, useCallback, useEffect, useRef, useState } from "react";
import { useInterval } from "../hooks/useInterval";
import { useStatisticsUpdates } from "../hooks/useStatisticsUpdates";
import { getWorkerHistory } from "../api/client";
import type { WorkerHistoryEvent, WorkerHistoryResponse } from "../api/types";
import { SearchDecisionDetails } from "../components/SearchDecisionDetails";
import { requestNavigateTab } from "../utils/navigateTab";

const labels: Record<string, string> = {
  search: "Search",
  "torrent-removal": "Torrent removal",
  import: "Import",
  add: "Item added",
  update: "Item updated",
  delete: "Item deleted",
  "temp-profile": "Temporary profile change",
  "queue-removal": "Queue removal / blocklist",
  "torrent-add": "Torrent added",
  "torrent-update": "Torrent settings",
  pause: "Pause",
  resume: "Resume",
  tags: "Tags",
  trackers: "Trackers",
  category: "Category",
  recheck: "Recheck",
  command: "Commands",
};

const actionTones: Record<string, string> = {
  search: "search",
  import: "success",
  add: "success",
  "torrent-add": "success",
  resume: "success",
  delete: "danger",
  "torrent-removal": "danger",
  "queue-removal": "danger",
  "temp-profile": "warning",
  pause: "warning",
  update: "change",
  "torrent-update": "change",
  tags: "change",
  trackers: "change",
  category: "change",
  recheck: "search",
  command: "neutral",
};
const shortLabels: Record<string, string> = {
  "torrent-removal": "Remove torrent",
  "torrent-add": "Add torrent",
  "torrent-update": "Torrent settings",
  "queue-removal": "Queue / blocklist",
  "temp-profile": "Temp profile",
  add: "Add",
  update: "Update",
  delete: "Delete",
};

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}

function fieldLabel(key: string): string {
  const names: Record<string, string> = {
    httpStatus: "HTTP status",
    responseStatus: "Response status",
    movieIds: "Movie IDs",
    episodeIds: "Episode IDs",
    seriesId: "Series ID",
    albumIds: "Album IDs",
    artistId: "Artist ID",
    authorId: "Author ID",
    bookIds: "Book IDs",
    downloadId: "Download ID",
    downloadClientId: "Download client ID",
    importMode: "Import mode",
    qualityProfileId: "Quality profile",
    previousProfileId: "Previous profile",
  };
  const words = key.replace(/([a-z\d])([A-Z])/g, "$1 $2").replace(/[-_]/g, " ");
  return (
    names[key] ?? words.charAt(0).toUpperCase() + words.slice(1).toLowerCase()
  );
}

function valueLabel(value: unknown): string {
  if (value == null || value === "") return "—";
  if (typeof value === "boolean") return value ? "Yes" : "No";
  return String(value);
}

function DetailValue({ value }: { value: unknown }) {
  if (Array.isArray(value))
    return value.length ? (
      <ul className="history-value-list">
        {value.map((item, index) => (
          <li key={index}>
            <DetailValue value={item} />
          </li>
        ))}
      </ul>
    ) : (
      <>None</>
    );
  if (isRecord(value))
    return (
      <dl className="history-detail-list">
        {Object.entries(value).map(([key, item]) => (
          <Fragment key={key}>
            <dt>{fieldLabel(key)}</dt>
            <dd>
              <DetailValue value={item} />
            </dd>
          </Fragment>
        ))}
      </dl>
    );
  return <>{valueLabel(value)}</>;
}

function actionLabel(entry: WorkerHistoryEvent): string {
  if (entry.type !== "command")
    return (
      shortLabels[entry.type] ?? labels[entry.type] ?? fieldLabel(entry.type)
    );
  const command = entry.summary.split(" · ")[0];
  return (
    (
      {
        RssSync: "RSS sync",
        RefreshMonitoredDownloads: "Refresh downloads",
      } as Record<string, string>
    )[command] ?? (command ? fieldLabel(command) : "Command")
  );
}

function itemLabel(entry: WorkerHistoryEvent): string {
  if (entry.type === "command") {
    const activities: Record<string, string> = {
      RssSync: "RSS feed refresh",
      RefreshMonitoredDownloads: "Monitored download refresh",
    };
    return activities[entry.summary] ?? fieldLabel(entry.summary);
  }
  const prefix = `${entry.subjectType} ${entry.type}: `;
  if (
    entry.source === "observed" &&
    entry.summary.toLowerCase().startsWith(prefix.toLowerCase())
  )
    return entry.summary.slice(prefix.length);
  const separator = entry.summary.indexOf(" · ");
  const name =
    separator >= 0 ? entry.summary.slice(separator + 3) : entry.summary;
  if (entry.subjectType === "torrent" && name === entry.subjectId)
    return `Torrent ${entry.subjectId.slice(0, 12)}…`;
  return name;
}

function HistoryEntry({ entry }: { entry: WorkerHistoryEvent }) {
  const [expanded, setExpanded] = useState(false);
  let parsed: unknown;
  try {
    parsed = JSON.parse(entry.details);
  } catch {
    parsed = entry.details;
  }
  const details = isRecord(parsed) ? parsed : {};
  const changes = Object.entries(details)
    .filter(
      ([, value]) => isRecord(value) && "before" in value && "after" in value,
    )
    .map(([key, value]) => ({
      field: fieldLabel(key),
      before: (value as Record<string, unknown>).before,
      after: (value as Record<string, unknown>).after,
    }));
  if (details.qualityProfileId != null && details.previousProfileId != null)
    changes.push({
      field: "Quality profile",
      before: details.previousProfileId,
      after: details.qualityProfileId,
    });
  const parameters = Object.entries(details).filter(
    ([key, value]) =>
      !(isRecord(value) && "before" in value && "after" in value) &&
      !(
        changes.some((change) => change.field === "Quality profile") &&
        ["previousProfileId", "qualityProfileId"].includes(key)
      ),
  );
  const subject =
    entry.subjectType && entry.subjectType !== "command"
      ? `${fieldLabel(entry.subjectType)}${entry.subjectId ? ` #${entry.subjectType === "torrent" && entry.subjectId.length > 12 ? entry.subjectId.slice(0, 12) + "…" : entry.subjectId}` : ""}`
      : "";
  const context = [
    subject,
    typeof details.reason === "string" ? details.reason : "",
    entry.commandId != null ? `Command #${entry.commandId}` : "",
  ].filter(Boolean);
  const panelId = `history-details-${entry.id}`;
  const buttonId = `history-details-button-${entry.id}`;
  return (
    <Fragment>
      <tr
        className={`history-row history-row--${actionTones[entry.type] ?? "neutral"}`}
        data-outcome={entry.outcome}
      >
        <td className="history-time">
          <time
            dateTime={entry.timestamp}
            title={new Date(entry.timestamp).toLocaleString()}
          >
            {new Date(entry.timestamp).toLocaleTimeString(undefined, {
              hour: "2-digit",
              minute: "2-digit",
              second: "2-digit",
            })}
            <span className="history-secondary">
              {new Date(entry.timestamp).toLocaleDateString(undefined, {
                month: "short",
                day: "numeric",
              })}
            </span>
          </time>
        </td>
        <td className="history-worker">
          <span className="history-worker-name" title={entry.instance}>
            {entry.instance}
          </span>
          <span className="history-secondary">
            {fieldLabel(entry.loop)} loop
          </span>
        </td>
        <td>
          <span
            className="history-action-badge"
            title={
              entry.type === "command" ? entry.summary : labels[entry.type]
            }
          >
            {actionLabel(entry)}
          </span>
        </td>
        <td className="history-item">
          <span className="history-summary-text" title={entry.summary}>
            {itemLabel(entry)}
          </span>
          {context.length > 0 && (
            <span className="history-secondary" title={context.join(" · ")}>
              {context.join(" · ")}
            </span>
          )}
        </td>
        <td className="history-context">
          {entry.clientInstance && (
            <span title={entry.clientInstance}>{entry.clientInstance}</span>
          )}
          <span className={entry.clientInstance ? "history-secondary" : ""}>
            {entry.source === "observed" ? "Catalog change" : "Worker action"}
          </span>
        </td>
        <td>
          <span className={`history-outcome history-outcome--${entry.outcome}`}>
            {entry.outcome}
          </span>
        </td>
        <td className="history-row-actions">
          <button
            id={buttonId}
            className="btn small ghost history-details-toggle"
            type="button"
            aria-expanded={expanded}
            aria-controls={panelId}
            onClick={() => setExpanded((value) => !value)}
          >
            Details<span aria-hidden="true">{expanded ? "▴" : "▾"}</span>
          </button>
        </td>
      </tr>
      {expanded && (
        <tr className="history-expanded-row">
          <td colSpan={7}>
            <section
              id={panelId}
              aria-label={`Details for ${entry.summary}`}
              className="history-expanded"
            >
              <div className="history-detail-heading">
                <h3>Action details</h3>
                <button
                  type="button"
                  className="btn small ghost"
                  onClick={() =>
                    requestNavigateTab({
                      tab: "logs",
                      relatedLogs: {
                        stream: entry.logStream ?? `worker-${entry.instance}`,
                        startedAt: entry.timestamp,
                        ...(entry.runId
                          ? {
                              runId: entry.runId,
                              operation: "SearchSubmission" as const,
                            }
                          : {}),
                      },
                    })
                  }
                >
                  View related logs
                </button>
              </div>
              <div className="history-detail-grid">
                <div>
                  <h4>Event</h4>
                  <dl className="history-detail-list">
                    <dt>Time</dt>
                    <dd>{new Date(entry.timestamp).toLocaleString()}</dd>
                    <dt>Worker</dt>
                    <dd>
                      {entry.instance} · {fieldLabel(entry.loop)} loop
                    </dd>
                    <dt>Action</dt>
                    <dd>{actionLabel(entry)}</dd>
                    <dt>Outcome</dt>
                    <dd>{fieldLabel(entry.outcome)}</dd>
                    <dt>Source</dt>
                    <dd>
                      {entry.source === "observed"
                        ? "Observed catalog change"
                        : "Worker initiated"}
                    </dd>
                    {entry.completedAt && (
                      <>
                        <dt>Confirmed</dt>
                        <dd>{new Date(entry.completedAt).toLocaleString()}</dd>
                      </>
                    )}
                  </dl>
                </div>
                <div>
                  <h4>Subject &amp; identifiers</h4>
                  <dl className="history-detail-list">
                    <dt>Summary</dt>
                    <dd>{entry.summary}</dd>
                    {entry.subjectType && (
                      <>
                        <dt>Subject type</dt>
                        <dd>{fieldLabel(entry.subjectType)}</dd>
                      </>
                    )}
                    {entry.subjectId && (
                      <>
                        <dt>Subject ID</dt>
                        <dd className="history-identifier">
                          {entry.subjectId}
                        </dd>
                      </>
                    )}
                    {entry.clientInstance && (
                      <>
                        <dt>Torrent client</dt>
                        <dd>{entry.clientInstance}</dd>
                      </>
                    )}
                    {entry.commandId != null && (
                      <>
                        <dt>Arr command</dt>
                        <dd>{entry.commandId}</dd>
                      </>
                    )}
                    {entry.runId && (
                      <>
                        <dt>Run ID</dt>
                        <dd className="history-identifier">{entry.runId}</dd>
                      </>
                    )}
                    <dt>Event ID</dt>
                    <dd>{entry.id}</dd>
                  </dl>
                </div>
                {parameters.length > 0 && (
                  <div className="history-parameters">
                    <h4>Parameters</h4>
                    <dl className="history-detail-list">
                      {parameters.map(([key, value]) => (
                        <Fragment key={key}>
                          <dt>{fieldLabel(key)}</dt>
                          <dd>
                            <DetailValue value={value} />
                          </dd>
                        </Fragment>
                      ))}
                    </dl>
                  </div>
                )}
                {changes.length > 0 && (
                  <div className="history-changes">
                    <h4>Changes</h4>
                    <table className="history-changes-table">
                      <thead>
                        <tr>
                          <th>Field</th>
                          <th>Previous value</th>
                          <th>New value</th>
                        </tr>
                      </thead>
                      <tbody>
                        {changes.map((change, index) => (
                          <tr key={index}>
                            <th scope="row">{change.field}</th>
                            <td>
                              <DetailValue value={change.before} />
                            </td>
                            <td>
                              <DetailValue value={change.after} />
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {!isRecord(parsed) && parsed != null && parsed !== "" && (
                  <div>
                    <h4>Additional information</h4>
                    <DetailValue value={parsed} />
                  </div>
                )}
              </div>
            </section>
          </td>
        </tr>
      )}
    </Fragment>
  );
}

export function HistoryView({
  active,
  selection,
}: {
  active: boolean;
  selection?: { worker: string };
}) {
  const [worker, setWorker] = useState(selection?.worker ?? "");
  const [type, setType] = useState("");
  const [result, setResult] = useState<WorkerHistoryResponse>();
  const [error, setError] = useState(false);
  const [loading, setLoading] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const request = useRef({ cancelled: false, busy: false });
  const latestResult = useRef<WorkerHistoryResponse | undefined>(undefined);
  const [live, setLive] = useState(true);
  const [liveError, setLiveError] = useState(false);
  const [updatedAt, setUpdatedAt] = useState<Date>();
  const [newActions, setNewActions] = useState(0);
  const previousSelection = useRef(selection);
  const [availableWorkers, setAvailableWorkers] = useState<string[]>([]);

  useEffect(() => {
    if (previousSelection.current === selection) return;
    previousSelection.current = selection;
    setWorker(selection?.worker ?? "");
    setType("");
    setRefresh((value) => value + 1);
  }, [selection]);

  useEffect(() => {
    const token = { cancelled: false, busy: true };
    request.current = token;
    if (!active) {
      token.cancelled = true;
      return;
    }
    setLoading(true);
    setError(false);
    setLiveError(false);
    setNewActions(0);
    setResult(undefined);
    latestResult.current = undefined;
    void getWorkerHistory(worker, type)
      .then((data) => {
        if (!token.cancelled) {
          latestResult.current = data;
          setResult(data);
          setAvailableWorkers(data.workers);
          setUpdatedAt(new Date());
        }
      })
      .catch(() => {
        if (!token.cancelled) setError(true);
      })
      .finally(() => {
        token.busy = false;
        if (!token.cancelled) setLoading(false);
      });
    return () => {
      token.cancelled = true;
    };
  }, [active, worker, type, refresh]);

  const loadLatest = useCallback(async () => {
    const token = request.current;
    const previous = latestResult.current;
    if (token.cancelled || token.busy || document.hidden) return;
    token.busy = true;
    try {
      const newestId = previous?.entries[0]?.id ?? 0;
      let page = await getWorkerHistory(worker, type);
      const workers = page.workers;
      const incoming = [...page.entries];
      // Drain a burst across pages before advancing the live boundary, so no actions are skipped.
      while (
        previous &&
        !token.cancelled &&
        page.nextCursor != null &&
        page.entries.length > 0 &&
        page.entries[page.entries.length - 1].id > newestId
      ) {
        if (document.hidden) return;
        page = await getWorkerHistory(worker, type, page.nextCursor);
        incoming.push(...page.entries);
      }
      if (token.cancelled || document.hidden) return;
      const entries = new Map(
        (previous?.entries ?? []).map((entry) => [entry.id, entry]),
      );
      for (const entry of incoming) entries.set(entry.id, entry);
      const merged = {
        ...(previous ?? page),
        workers,
        entries: [...entries.values()].sort((a, b) => b.id - a.id),
      };
      latestResult.current = merged;
      setResult(merged);
      setAvailableWorkers(workers);
      setNewActions(
        previous ? incoming.filter((entry) => entry.id > newestId).length : 0,
      );
      if (!previous) setError(false);
      setUpdatedAt(new Date());
      setLiveError(false);
    } catch {
      if (!token.cancelled) setLiveError(true);
    } finally {
      token.busy = false;
    }
  }, [worker, type]);

  useInterval(
    () => {
      void loadLatest();
    },
    active && live ? 3000 : null,
  );
  useStatisticsUpdates(active && live, loadLatest);

  async function loadMore() {
    const previous = latestResult.current;
    const token = request.current;
    if (previous?.nextCursor == null || token.cancelled || token.busy) return;
    token.busy = true;
    setLoading(true);
    setError(false);
    try {
      const page = await getWorkerHistory(worker, type, previous.nextCursor);
      if (!token.cancelled) {
        const merged = {
          ...page,
          entries: [
            ...new Map(
              [...previous.entries, ...page.entries].map((entry) => [
                entry.id,
                entry,
              ]),
            ).values(),
          ].sort((a, b) => b.id - a.id),
        };
        latestResult.current = merged;
        setResult(merged);
      }
    } catch {
      if (!token.cancelled) setError(true);
    } finally {
      token.busy = false;
      if (!token.cancelled) setLoading(false);
    }
  }

  const workers = Array.from(
    new Set([...availableWorkers, ...(worker ? [worker] : [])]),
  );
  return (
    <section className="card history-view" aria-label="History">
      <div className="card-body">
        <div className="history-toolbar">
          <h2>History</h2>
          <label className="field">
            Worker
            <select
              aria-label="Worker"
              value={worker}
              onChange={(event) => setWorker(event.target.value)}
            >
              <option value="">All workers</option>
              {workers.map((name) => (
                <option key={name} value={name}>
                  {name}
                </option>
              ))}
            </select>
          </label>
          <label className="field">
            Action type
            <select
              aria-label="Action type"
              value={type}
              onChange={(event) => setType(event.target.value)}
            >
              <option value="">All actions</option>
              {Object.entries(labels).map(([value, label]) => (
                <option key={value} value={value}>
                  {label}
                </option>
              ))}
            </select>
          </label>
          <label className="history-live-toggle">
            <input
              type="checkbox"
              checked={live}
              onChange={(event) => setLive(event.target.checked)}
            />
            Live
          </label>
          <button
            type="button"
            className="btn ghost"
            disabled={loading}
            onClick={() =>
              result ? void loadLatest() : setRefresh((value) => value + 1)
            }
          >
            Refresh
          </button>
        </div>
        <div className="history-meta">
          <span
            className={`history-live-status${liveError ? " history-live-status--error" : live ? " history-live-status--on" : ""}`}
          >
            <span aria-hidden="true" />
            {liveError
              ? live
                ? "Live updates interrupted · retrying"
                : "Refresh failed"
              : live
                ? "Live · every 3s"
                : "Live paused"}
          </span>
          <span>{result?.entries.length ?? 0} loaded · newest first</span>
          {updatedAt && (
            <time dateTime={updatedAt.toISOString()}>
              Updated {updatedAt.toLocaleTimeString()}
            </time>
          )}
          <span role="status">
            {newActions > 0
              ? `${newActions} new ${newActions === 1 ? "action" : "actions"}`
              : ""}
          </span>
        </div>
        {error && (
          <p role="alert">
            History could not be loaded.{" "}
            <button
              type="button"
              className="btn small ghost"
              onClick={() =>
                result?.nextCursor != null
                  ? void loadMore()
                  : setRefresh((value) => value + 1)
              }
            >
              Retry
            </button>
          </p>
        )}
        {loading && <p role="status">Loading history…</p>}
        {result && result.entries.length === 0 && (
          <p>
            {worker || type
              ? "No history matches these filters."
              : "No worker history has been recorded yet."}
          </p>
        )}
        {result && result.entries.length > 0 && (
          <div className="history-table-wrap">
            <table className="history-table">
              <colgroup>
                <col className="history-col-time" />
                <col className="history-col-worker" />
                <col className="history-col-action" />
                <col />
                <col className="history-col-context" />
                <col className="history-col-outcome" />
                <col className="history-col-controls" />
              </colgroup>
              <thead>
                <tr>
                  <th scope="col">Time</th>
                  <th scope="col">Worker</th>
                  <th scope="col">Action</th>
                  <th scope="col">Item / activity</th>
                  <th scope="col">Context</th>
                  <th scope="col">Outcome</th>
                  <th
                    scope="col"
                    className="history-row-actions"
                    aria-label="Row actions"
                  ></th>
                </tr>
              </thead>
              <tbody>
                {result.entries.map((entry) => (
                  <HistoryEntry key={entry.id} entry={entry} />
                ))}
              </tbody>
            </table>
          </div>
        )}
        {result?.nextCursor != null && (
          <button
            type="button"
            className="btn ghost"
            disabled={loading}
            onClick={() => void loadMore()}
          >
            Load older entries
          </button>
        )}
        {worker && (!type || type === "search") && (
          <div className="history-search-details">
            <SearchDecisionDetails
              key={worker}
              instance={worker}
              category={worker}
              label="Search details"
            />
          </div>
        )}
      </div>
    </section>
  );
}
