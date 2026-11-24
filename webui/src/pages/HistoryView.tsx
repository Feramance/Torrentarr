import { useEffect, useRef, useState } from "react";
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
  command: "Other command",
};

function Details({ entry }: { entry: WorkerHistoryEvent }) {
  let details: string;
  try {
    details = JSON.stringify(JSON.parse(entry.details), null, 2);
  } catch {
    details = entry.details;
  }
  return (
    <details>
      <summary>{entry.summary}</summary>
      <dl className="history-details">
        <dt>Source</dt>
        <dd>
          {entry.source === "observed"
            ? "Observed catalog change"
            : "Worker initiated"}
        </dd>
        <dt>Subject</dt>
        <dd>
          {entry.subjectType} {entry.subjectId}
        </dd>
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
        {entry.completedAt && (
          <>
            <dt>Confirmed</dt>
            <dd>{new Date(entry.completedAt).toLocaleString()}</dd>
          </>
        )}
      </dl>
      {details !== "{}" && <pre>{details}</pre>}
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
                ? { runId: entry.runId, operation: "SearchSubmission" as const }
                : {}),
            },
          })
        }
      >
        View related logs
      </button>
    </details>
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
  const request = useRef({ cancelled: false });
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
    const token = { cancelled: false };
    request.current = token;
    if (!active) {
      token.cancelled = true;
      return;
    }
    setLoading(true);
    setError(false);
    setResult(undefined);
    void getWorkerHistory(worker, type)
      .then((data) => {
        if (!token.cancelled) {
          setResult(data);
          setAvailableWorkers(data.workers);
        }
      })
      .catch(() => {
        if (!token.cancelled) setError(true);
      })
      .finally(() => {
        if (!token.cancelled) setLoading(false);
      });
    return () => {
      token.cancelled = true;
    };
  }, [active, worker, type, refresh]);

  async function loadMore() {
    if (result?.nextCursor == null || loading) return;
    const token = request.current;
    if (token.cancelled) return;
    setLoading(true);
    setError(false);
    try {
      const page = await getWorkerHistory(worker, type, result.nextCursor);
      if (!token.cancelled)
        setResult({ ...page, entries: [...result.entries, ...page.entries] });
    } catch {
      if (!token.cancelled) setError(true);
    } finally {
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
          <button
            type="button"
            className="btn ghost"
            disabled={loading}
            onClick={() => setRefresh((value) => value + 1)}
          >
            Refresh
          </button>
        </div>
        <p className="hint">
          Worker actions and observed Arr item changes, newest first.
        </p>
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
              <thead>
                <tr>
                  <th>Time</th>
                  <th>Worker</th>
                  <th>Action</th>
                  <th>Outcome</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                {result.entries.map((entry) => (
                  <tr key={entry.id}>
                    <td>
                      <time dateTime={entry.timestamp}>
                        {new Date(entry.timestamp).toLocaleString()}
                      </time>
                    </td>
                    <td>{entry.instance}</td>
                    <td>
                      {labels[entry.type] ?? entry.type}
                      {entry.source === "observed" && (
                        <span className="hint"> · observed</span>
                      )}
                    </td>
                    <td>{entry.outcome}</td>
                    <td>
                      <Details entry={entry} />
                    </td>
                  </tr>
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
