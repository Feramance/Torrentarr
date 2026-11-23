import { STATISTICS_UPDATED } from "../hooks/useStatisticsUpdates";
import { useCallback, useEffect, useRef, useState } from "react";
import { getIssueDetails, getIssues, getIssuesSummary } from "../api/client";
import type {
  IssueEvidence,
  IssuesResponse,
  OperationalIssue,
  OperationalIssueGroup,
} from "../api/types";
import { PersistentDetails } from "../components/PersistentDetails";
import { issueGuidance } from "./issueGuidance";
import { CopyButton } from "../components/CopyButton";
import {
  requestNavigateTab,
  type ConfigDestination,
  type NavigableTab,
} from "../utils/navigateTab";

const issueTitle = (issue: Pick<OperationalIssue, "code" | "title">) =>
  issueGuidance(issue.code)?.[0] ?? issue.title;

const countLabel = (count: number, noun: string) =>
  `${count} ${noun}${count === 1 ? "" : "s"}`;

function subjectNoun(group: OperationalIssueGroup) {
  if (group.code === "storage.reserve") return "storage monitor";
  if (group.code.startsWith("maintenance.preview."))
    return "maintenance preview";
  if (
    group.examples.length > 0 &&
    group.examples.length === group.affected &&
    group.examples.every((item) => item.subjectType === "torrent")
  )
    return "torrent";
  return "item";
}

function subjectName(item: OperationalIssue) {
  switch (item.subjectType) {
    case "preview":
      return "Maintenance preview";
    case "torrent":
      return item.subjectName || item.subjectId || "Torrent name unavailable";
    default:
      return item.subjectId || "Application";
  }
}

function IssueTechnicalDetails({
  item,
  summary,
}: {
  item: OperationalIssue;
  summary?: string;
}) {
  const evidence = item.evidence;
  const hasPaths = evidence?.files?.some(
    (file) => file.clientPath || file.localPath,
  );
  const operations = evidence?.operations?.map((operation) =>
    operation.replace(/([a-z])([A-Z])/g, "$1 $2"),
  );
  const filesByTorrent = new Map<string, NonNullable<IssueEvidence["files"]>>();
  for (const file of evidence?.files ?? []) {
    const key = file.torrentHash || file.torrentName || "Torrent";
    const files = filesByTorrent.get(key) ?? [];
    files.push(file);
    filesByTorrent.set(key, files);
  }
  const copyText = [
    `Issue ${item.id} · ${item.code}`,
    issueTitle(item),
    `${item.resolvedAt ? "Resolved" : "Active"} · ${item.lastObserved}`,
    item.subjectId,
    item.impact,
    item.remediation,
    evidence?.detail,
    operations?.join(", "),
    ...Array.from(filesByTorrent, ([torrent, files]) =>
      [
        torrent,
        ...files.map((file) =>
          [file.clientPath, file.localPath].filter(Boolean).join(" → "),
        ),
      ].join("\n"),
    ),
  ]
    .filter(Boolean)
    .join("\n");

  return (
    <>
      {!!operations?.length && (
        <p>
          <strong>Affected operations</strong> {operations.join(", ")}
        </p>
      )}
      {(!!evidence?.affectedFiles || !!evidence?.affectedTorrents) && (
        <p>
          {[
            evidence.affectedFiles
              ? countLabel(evidence.affectedFiles, "file")
              : "",
            evidence.affectedTorrents
              ? countLabel(evidence.affectedTorrents, "torrent")
              : "",
          ]
            .filter(Boolean)
            .join(" · ")}
        </p>
      )}
      {filesByTorrent.size > 0 && (
        <div className="issue-file-examples">
          <strong>{hasPaths ? "File examples" : "Torrent examples"}</strong>
          {Array.from(filesByTorrent, ([torrent, files]) => (
            <div key={torrent}>
              <p>{files[0].torrentName || torrent}</p>
              {files.some((file) => file.clientPath || file.localPath) && (
                <ul>
                  {files
                    .filter((file) => file.clientPath || file.localPath)
                    .map((file, index) => (
                      <li key={index}>
                        <details>
                          <summary>
                            {(file.clientPath || file.localPath)
                              ?.split(/[/\\]/)
                              .pop() || "File path unavailable"}
                          </summary>
                          <dl className="issue-paths">
                            <dt>Client path</dt>
                            <dd>{file.clientPath || "Unavailable"}</dd>
                            <dt>Path Torrentarr checked</dt>
                            <dd>{file.localPath || "Unavailable"}</dd>
                          </dl>
                        </details>
                      </li>
                    ))}
                </ul>
              )}
            </div>
          ))}
        </div>
      )}
      <details className="issue-technical">
        <summary>Technical details</summary>
        {item.impact && item.impact !== summary && <p>{item.impact}</p>}
        {evidence?.detail && <p>{evidence.detail}</p>}
        {!issueGuidance(item.code) && item.remediation && (
          <p>{item.remediation}</p>
        )}
        <p className="muted">
          Issue {item.id} · {item.code}
          {item.subjectId ? ` · ${item.subjectId}` : ""}
        </p>
        <CopyButton label="Copy details" text={copyText} />
      </details>
    </>
  );
}

function IssueDetails({
  group,
  state,
  severity,
  instance,
  search,
  refresh,
}: {
  group: OperationalIssueGroup;
  state: string;
  severity: string;
  instance: string;
  search: string;
  refresh: () => Promise<void>;
}) {
  type ItemPage = NonNullable<Awaited<ReturnType<typeof getIssueDetails>>>;
  const [current, setCurrent] = useState<{ state: string; result: ItemPage }>();
  const [failed, setFailed] = useState<{ state: string; page: number }>();
  const [loading, setLoading] = useState(false);
  const [changed, setChanged] = useState(false);
  const request = useRef(0);
  const loaded =
    current && (current.state === state || group.hasResolvedItems)
      ? current
      : undefined;
  const items = loaded?.result.items ?? group.examples;
  const total = loaded?.result.total ?? group.affected;
  const page = loaded?.result.page ?? 0;
  const pageSize = loaded?.result.pageSize ?? items.length;
  const showingResolved = state === "active" && loaded?.state === "resolved";
  const load = useCallback(
    async (itemState: string, next: number) => {
      const revision = ++request.current;
      const filters = {
        state: itemState,
        category: group.category,
        severity,
        instance,
        q: search,
      };
      setLoading(true);
      setFailed(undefined);
      try {
        let result = await getIssueDetails(group.id, next, filters);
        if (request.current !== revision) return;
        if (result && next > 0 && !result.items.length) {
          result = await getIssueDetails(
            group.id,
            Math.max(0, Math.ceil(result.total / result.pageSize) - 1),
            filters,
          );
        }
        if (request.current !== revision) return;
        if (result && !result.available)
          throw new Error("Issue items unavailable");
        if (!result || !result.total) {
          setChanged(true);
          setCurrent(undefined);
          await refresh();
          return;
        }
        setChanged(false);
        setCurrent({ state: itemState, result });
      } catch {
        if (request.current === revision)
          setFailed({ state: itemState, page: next });
      } finally {
        if (request.current === revision) setLoading(false);
      }
    },
    [group.id, group.category, severity, instance, search, refresh],
  );

  useEffect(
    () => () => {
      request.current++;
    },
    [],
  );

  const snapshot = JSON.stringify([
    group.id,
    group.lastObserved,
    group.affected,
    group.hasResolvedItems,
  ]);
  const previousSnapshot = useRef(snapshot);
  useEffect(() => {
    if (loading || snapshot === previousSnapshot.current) return;
    previousSnapshot.current = snapshot;
    if (loaded) void load(loaded.state, loaded.result.page);
  }, [loading, snapshot, loaded, load]);

  const guidance = issueGuidance(group.code);
  const description = guidance?.[1] ?? group.impact;
  const checks =
    guidance?.[2] ?? (group.remediation ? [group.remediation] : []);
  const configAction = (issue: OperationalIssue, exactSubject: boolean) => {
    const destination: ConfigDestination = {
      client: issue.clientInstance || undefined,
      arr: issue.arrInstance || undefined,
    };
    let label = "Open Config";
    switch (issue.code) {
      case "storage.reserve":
        destination.field = "FreeSpace";
        destination.monitorPath = exactSubject ? issue.subjectId : undefined;
        label = destination.monitorPath
          ? "Edit free-space monitor"
          : "Open free-space monitors";
        break;
      case "config.storage":
        destination.field = "Maintenance.PathMappings";
        label = "Edit path mappings";
        break;
      case "config.migration-action-required":
      case "config.migration":
        destination.field = "DownloadPath";
        label = "Check download location";
        break;
      case "client.connection":
      case "config.client":
        destination.field = "Host";
        label = "Check client connection settings";
        break;
      case "import.ownership":
        destination.field = "ManagedCategories";
        label = "Check managed categories";
        break;
      case "profile.prepare":
      case "profile.restore":
        destination.field = "EntrySearch.QualityProfileMappings";
        label = "Check quality profile mappings";
        break;
      case "media.validation-unavailable":
        destination.field = "FFprobeAutoUpdate";
        label = "Check FFprobe settings";
        break;
    }
    return (
      <button
        type="button"
        className="btn small"
        onClick={() =>
          requestNavigateTab({
            tab: "config",
            configDestination: destination,
            preserveConfigDraft: true,
          })
        }
      >
        {label}
      </button>
    );
  };
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
      <section>
        <h4>What happened</h4>
        {description && <p>{description}</p>}
        {!!checks.length && (
          <>
            <h4>What to check</h4>
            <ol>
              {checks.map((step) => (
                <li key={step}>{step}</li>
              ))}
            </ol>
          </>
        )}
        {guidance && (
          <>
            <h4>How to verify</h4>
            <p>{guidance[3]}</p>
          </>
        )}
        <div className="actions issue-actions">
          {target === "config" ? (
            configAction(
              group.examples[0] && group.affected === 1
                ? group.examples[0]
                : group,
              group.affected === 1,
            )
          ) : (
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
              {target === "maintenance"
                ? `Open Manage${group.clientInstance ? ` for ${group.clientInstance}` : ""}`
                : target === "processes"
                  ? "Check worker in Processes"
                  : target === "logs"
                    ? "Browse application logs"
                    : `Open ${target}`}
            </button>
          )}
          {group.code === "maintenance.preview.missing-file" &&
            group.clientInstance &&
            configAction({ ...group, code: "config.storage" }, false)}
        </div>
      </section>
      <section className="issue-items" aria-busy={loading}>
        <h4>
          {showingResolved || state === "resolved"
            ? "Resolved items"
            : "Affected items"}
        </h4>
        {loading && <p role="status">Loading issue items…</p>}
        {failed && (
          <p role="alert">
            Issue items could not be loaded.{" "}
            <button
              type="button"
              className="btn small"
              onClick={() => void load(failed.state, failed.page)}
            >
              Retry
            </button>
          </p>
        )}
        {changed && (
          <p role="status">
            The matching items changed. The list has been refreshed.
          </p>
        )}
        {!items.length && !loading && <p>No matching items.</p>}
        {items.length > 0 && total > items.length && (
          <p className="muted">
            {loaded
              ? `Showing ${page * pageSize + 1}–${page * pageSize + items.length} of ${total}`
              : `Showing ${items.length} examples of ${total}`}
          </p>
        )}
        <ul className="issue-item-list">
          {items.map((item) => (
            <li key={item.id}>
              <strong>{subjectName(item)}</strong>
              {item.subjectType === "torrent" && (
                <span>
                  {" "}
                  · Category:{" "}
                  {item.subjectCategory === ""
                    ? "Uncategorized"
                    : item.subjectCategory ?? "unavailable"}
                </span>
              )}
              <p className="muted">
                {item.resolvedAt
                  ? `Resolved ${new Date(item.resolvedAt).toLocaleString()} · ${item.resolution ?? "Recovered"}`
                  : `First observed ${new Date(item.firstObserved).toLocaleString()} · Last observed ${new Date(item.lastObserved).toLocaleString()}`}
              </p>
              <IssueTechnicalDetails item={item} summary={description} />
              <div className="actions issue-actions">
                {item.relatedLogStream && (
                  <button
                    type="button"
                    className="btn ghost small"
                    onClick={() =>
                      requestNavigateTab({
                        tab: "logs",
                        relatedLogs: { stream: item.relatedLogStream! },
                      })
                    }
                  >
                    Browse worker logs
                  </button>
                )}
                {target === "config" &&
                  group.affected > 1 &&
                  item.code === "storage.reserve" &&
                  configAction(item, true)}
              </div>
            </li>
          ))}
        </ul>
        <div className="actions issue-pagination">
          {!loaded && items.length < total && (
            <button
              type="button"
              className="btn small"
              disabled={loading}
              onClick={() => void load(state, 0)}
            >
              Show all {state === "resolved" ? "resolved" : "affected"} items
            </button>
          )}
          {loaded && page > 0 && (
            <button
              type="button"
              className="btn small"
              disabled={loading}
              onClick={() => void load(loaded.state, page - 1)}
            >
              Previous page
            </button>
          )}
          {loaded && page * pageSize + items.length < total && (
            <button
              type="button"
              className="btn small"
              disabled={loading}
              onClick={() => void load(loaded.state, page + 1)}
            >
              Next page
            </button>
          )}
          {showingResolved ? (
            <button
              type="button"
              className="btn small"
              disabled={loading}
              onClick={() => {
                setCurrent(undefined);
                setFailed(undefined);
              }}
            >
              Back to affected items
            </button>
          ) : (
            state === "active" &&
            group.hasResolvedItems && (
              <button
                type="button"
                className="btn small"
                disabled={loading}
                onClick={() => void load("resolved", 0)}
              >
                View resolved items
              </button>
            )
          )}
        </div>
      </section>
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
  const invalidateRequest = useCallback(() => {
    request.current++;
  }, []);
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
      if (result.available && result.total !== null) {
        const lastPage = Math.max(
          0,
          Math.ceil(result.total / result.pageSize) - 1,
        );
        if (page > lastPage) {
          setPage(lastPage);
          return;
        }
      }
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
        if (stopped) return;
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
    window.addEventListener(STATISTICS_UPDATED, visible);
    return () => {
      stopped = true;
      invalidateRequest();
      window.clearInterval(interval);
      document.removeEventListener("visibilitychange", visible);
      window.removeEventListener(STATISTICS_UPDATED, visible);
    };
  }, [active, invalidateRequest, load]);
  const change = (set: (value: string) => void, value: string) => {
    set(value);
    setPage(0);
    setLoading(true);
    setData(undefined);
  };
  return (
    <section className="card issues-view" aria-label="Issues">
      <div className="card-body">
        <p className="hint">
          Problems that need attention, grouped by cause and instance. Recovery
          closes an issue after a successful check.
        </p>
        <div
          className="instance-tabs"
          role="tablist"
          aria-label="Issue category"
        >
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
                  ?.querySelector<HTMLButtonElement>(
                    `[data-category="${next}"]`,
                  )
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
              Issue history unavailable. This does not mean the system is
              healthy. Pending reports are shown below; check database health
              and Logs.
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
                <strong>{issueTitle(p.report)}</strong>
                <p>{issueGuidance(p.report.code)?.[1] ?? p.report.impact}</p>
                <ol>
                  {(issueGuidance(p.report.code)?.[2] ?? [p.report.remediation])
                    .filter(Boolean)
                    .map((step) => (
                      <li key={step}>{step}</li>
                    ))}
                </ol>
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
                <strong>{issueTitle(group)}</strong>
                <span>
                  {[group.arrInstance, group.clientInstance]
                    .filter(Boolean)
                    .join(" · ") || "Application"}{" "}
                  · {countLabel(group.affected, subjectNoun(group))} ·{" "}
                  {state === "resolved" ? "Resolved" : "Active"}
                </span>
                <time dateTime={group.lastObserved}>
                  {new Date(group.lastObserved).toLocaleString()}
                </time>
              </summary>
              <IssueDetails
                key={JSON.stringify([state, severity, instance, search])}
                group={group}
                state={state}
                severity={severity}
                instance={instance}
                search={search}
                refresh={load}
              />
            </PersistentDetails>
          ))}
          {data?.available &&
            (page > 0 || (data.total ?? 0) > data.pageSize) && (
              <div className="actions issue-pagination">
                {page > 0 && (
                  <button
                    type="button"
                    className="btn small ghost"
                    onClick={() => setPage((p) => p - 1)}
                  >
                    Previous group page
                  </button>
                )}
                <span>
                  Page {data.page + 1} · {data.total} groups
                </span>
                {(page + 1) * data.pageSize < (data.total ?? 0) && (
                  <button
                    type="button"
                    className="btn small ghost"
                    onClick={() => setPage((p) => p + 1)}
                  >
                    Next group page
                  </button>
                )}
              </div>
            )}
        </div>
      </div>
    </section>
  );
}
