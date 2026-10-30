import { useCallback, useEffect, useRef, useState } from "react";
import { getIssueDetails, getIssues, getIssuesSummary } from "../api/client";
import type {
  IssuesResponse,
  OperationalIssue,
  OperationalIssueGroup,
} from "../api/types";
import { PersistentDetails } from "../components/PersistentDetails";
import { requestNavigateTab, type NavigableTab } from "../utils/navigateTab";

function IssueDetails({ group }: { group: OperationalIssueGroup }) {
  const [items, setItems] = useState<OperationalIssue[]>(group.examples);
  const [page, setPage] = useState(0);
  const [total, setTotal] = useState(group.affected);
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(false);
  const [loaded, setLoaded] = useState(false);
  const request = useRef(0);
  useEffect(() => {
    if (!loaded && page === 0) {
      setItems(group.examples);
      setTotal(group.affected);
    }
  }, [group.examples, group.affected, page, loaded]);
  const load = useCallback(
    async (next: number) => {
      const revision = ++request.current;
      setLoading(true);
      try {
        const result = await getIssueDetails(group.id, next);
        if (request.current !== revision) return;
        const lastPage = Math.max(0, Math.ceil(result.total / 50) - 1);
        if (next > lastPage) {
          setPage(lastPage);
          return;
        }
        setLoaded(true);
        setItems(result.items);
        setTotal(result.total);
        setPage(next);
        setError(undefined);
      } catch {
        if (request.current !== revision) return;
        setError(
          "Affected subjects could not be loaded. The last confirmed examples are shown.",
        );
      } finally {
        if (request.current === revision) setLoading(false);
      }
    },
    [group.id],
  );
  useEffect(
    () => () => {
      request.current++;
    },
    [load],
  );
  useEffect(() => {
    if (loaded) void load(page);
  }, [loaded, page, load, group]);
  const target = (
    [
      "config",
      "logs",
      "processes",
      "radarr",
      "sonarr",
      "lidarr",
      "readarr",
      "qbittorrent",
      "maintenance",
    ].includes(group.target)
      ? group.target
      : "logs"
  ) as NavigableTab;
  return (
    <div className="issue-details">
      <p>
        <strong>Impact:</strong> {group.impact}
      </p>
      <p>
        <strong>How to fix:</strong> {group.remediation}
      </p>
      <p className="muted">
        First observed {new Date(group.firstObserved).toLocaleString()} ·{" "}
        {group.occurrences} observations
      </p>
      <button
        type="button"
        className="btn small"
        onClick={() =>
          requestNavigateTab({
            tab: target,
            manageClientId: group.clientInstance || undefined,
          })
        }
      >
        Open {target === "maintenance" ? "Manage" : target}
      </button>
      {error && <p role="alert">{error}</p>}
      <ul>
        {items.map((item) => (
          <li key={item.id}>
            <strong>{item.subjectType}</strong>
            {item.subjectId ? `: ${item.subjectId}` : ""} —{" "}
            {item.resolvedAt ? item.resolution ?? "Recovered" : "Active"}
          </li>
        ))}
      </ul>
      {(!loaded || total > 50) && (
        <div className="actions">
          <button
            className="btn small ghost"
            disabled={loading || page === 0}
            onClick={() => void load(page - 1)}
          >
            Previous subjects
          </button>
          <button
            className="btn small ghost"
            disabled={loading || (loaded && page * 50 + items.length >= total)}
            onClick={() => void load(!loaded ? 0 : page + 1)}
          >
            {!loaded ? "View affected subjects and history" : "Next subjects"}
          </button>
        </div>
      )}
    </div>
  );
}

export function IssuesView({
  active = true,
  selection,
}: {
  active?: boolean;
  selection?: { instance: string; category: "automation" | "system" };
}) {
  const [category, setCategory] = useState("automation");
  const [state, setState] = useState("active");
  const [severity, setSeverity] = useState("");
  const [instance, setInstance] = useState("");
  const [search, setSearch] = useState("");
  const [page, setPage] = useState(0);
  const [data, setData] = useState<IssuesResponse>();
  const [instances, setInstances] = useState<string[]>([]);
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const request = useRef(0);
  useEffect(() => {
    if (!selection) return;
    setInstance(selection.instance);
    setCategory(selection.category);
    setPage(0);
    setData(undefined);
    setLoading(true);
    if (selection.instance)
      setInstances((previous) =>
        Array.from(new Set([...previous, selection.instance])).sort(),
      );
  }, [selection]);
  const load = useCallback(async () => {
    const revision = ++request.current;
    try {
      const result = await getIssues({
        category,
        state,
        page,
        ...(severity ? { severity } : {}),
        ...(instance ? { instance } : {}),
        ...(search ? { q: search } : {}),
      });
      if (request.current !== revision) return;
      setData(result);
      setError(undefined);
      setLoading(false);
    } catch {
      if (request.current === revision) {
        setError(
          "Issues could not be refreshed. Previously loaded information may be stale.",
        );
        setLoading(false);
      }
    }
  }, [category, state, severity, instance, search, page]);
  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let busy = false;
    const refresh = async () => {
      if (document.hidden || busy || stopped) return;
      busy = true;
      try {
        await load();
        const summary = await getIssuesSummary();
        if (!stopped)
          setInstances((previous) =>
            Array.from(
              new Set([
                ...previous,
                ...(summary.instances ?? []),
                ...summary.counts
                  .flatMap((c) => [c.arrInstance, c.clientInstance])
                  .filter(Boolean),
              ]),
            ).sort(),
          );
      } catch {
        /* The list has its own error state; summary is optional here. */
      } finally {
        busy = false;
      }
    };
    void refresh();
    const interval = window.setInterval(() => void refresh(), 5000);
    const visible = () => {
      if (!document.hidden) void refresh();
    };
    document.addEventListener("visibilitychange", visible);
    return () => {
      stopped = true;
      request.current++;
      window.clearInterval(interval);
      document.removeEventListener("visibilitychange", visible);
    };
  }, [active, load]);
  const change = (set: (value: string) => void, value: string) => {
    set(value);
    setPage(0);
    setLoading(true);
    setData(undefined);
  };
  return (
    <section className="issues-view" aria-labelledby="issues-heading">
      <h2 id="issues-heading">Issues</h2>
      <p className="muted">
        Problems that need attention, grouped by cause and instance. Recovery
        closes an issue after a successful check.
      </p>
      <div className="instance-tabs" role="tablist" aria-label="Issue category">
        {[
          ["automation", "Automation & safety"],
          ["system", "System & services"],
        ].map(([value, label]) => (
          <button
            type="button"
            key={value}
            role="tab"
            id={`issue-tab-${value}`}
            aria-controls="issue-results"
            tabIndex={category === value ? 0 : -1}
            data-category={value}
            onKeyDown={(event) => {
              if (
                event.key !== "ArrowLeft" &&
                event.key !== "ArrowRight" &&
                event.key !== "Home" &&
                event.key !== "End"
              )
                return;
              event.preventDefault();
              const next =
                event.key === "Home"
                  ? "automation"
                  : event.key === "End"
                    ? "system"
                    : value === "automation"
                      ? "system"
                      : "automation";
              change(setCategory, next);
              event.currentTarget.parentElement
                ?.querySelector<HTMLButtonElement>(`[data-category="${next}"]`)
                ?.focus();
            }}
            aria-selected={category === value}
            className={`btn small ${category === value ? "" : "ghost"}`}
            onClick={() => change(setCategory, value)}
          >
            {label}
          </button>
        ))}
      </div>
      <div className="issue-filters actions">
        <label>
          Status{" "}
          <select
            value={state}
            onChange={(e) => change(setState, e.target.value)}
          >
            <option value="active">Active</option>
            <option value="resolved">Resolved · last 30 days</option>
          </select>
        </label>
        <label>
          Severity{" "}
          <select
            value={severity}
            onChange={(e) => change(setSeverity, e.target.value)}
          >
            <option value="">All severities</option>
            <option value="error">Error</option>
            <option value="warning">Warning</option>
          </select>
        </label>
        <label>
          Instance{" "}
          <select
            value={instance}
            onChange={(e) => change(setInstance, e.target.value)}
          >
            <option value="">All instances</option>
            {instances.map((i) => (
              <option key={i}>{i}</option>
            ))}
          </select>
        </label>
        <label>
          Search{" "}
          <input
            type="search"
            value={search}
            onChange={(e) => change(setSearch, e.target.value)}
            placeholder="Title, impact or subject"
          />
        </label>
        <button
          type="button"
          className="btn small ghost"
          onClick={() => void load()}
        >
          Refresh
        </button>
      </div>
      <div
        id="issue-results"
        role="tabpanel"
        aria-labelledby={`issue-tab-${category}`}
      >
        {error && (
          <p role="alert" className="issue-unavailable">
            {error}
          </p>
        )}
        {data && !data.available && (
          <p role="alert" className="issue-unavailable">
            Issue history unavailable. This does not mean the system is healthy.
            Pending reports are shown below; check database health and Logs.
          </p>
        )}
        {data?.pending
          .filter(
            (p) =>
              !p.resolved &&
              p.report.category === category &&
              (!instance ||
                p.report.arrInstance === instance ||
                p.report.clientInstance === instance),
          )
          .map((p, i) => (
            <div className="issue-row" key={`${p.report.code}-${i}`}>
              <strong>{p.report.title}</strong>
              <p>{p.report.impact}</p>
              <p>{p.report.remediation}</p>
              <span className="muted">Pending persistence</span>
            </div>
          ))}
        {loading && <p role="status">Loading issues…</p>}
        {!loading && data?.available && !data.items.length && (
          <p role="status">
            {state === "active"
              ? "No active issues match these filters."
              : "No resolved issues match these filters."}
          </p>
        )}
        {data?.items.map((group) => (
          <PersistentDetails
            className="issue-row"
            key={JSON.stringify([
              group.code,
              group.arrInstance,
              group.clientInstance,
            ])}
          >
            <summary>
              <span
                className={`issue-severity issue-severity--${group.severity}`}
              >
                {group.severity === "error" ? "Error" : "Warning"}
              </span>
              <strong>{group.title}</strong>
              <span>
                {group.arrInstance || group.clientInstance || "Application"} ·{" "}
                {group.affected} affected
              </span>
              <time dateTime={group.lastObserved}>
                {new Date(group.lastObserved).toLocaleString()}
              </time>
            </summary>
            <IssueDetails group={group} />
          </PersistentDetails>
        ))}
        {data?.available && (
          <div className="actions issue-pagination">
            <button
              type="button"
              className="btn small ghost"
              disabled={page === 0}
              onClick={() => setPage((p) => p - 1)}
            >
              Previous
            </button>
            <span>
              Page {page + 1} · {data.total} groups
            </span>
            <button
              type="button"
              className="btn small ghost"
              disabled={(page + 1) * data.pageSize >= (data.total ?? 0)}
              onClick={() => setPage((p) => p + 1)}
            >
              Next
            </button>
          </div>
        )}
      </div>
    </section>
  );
}
