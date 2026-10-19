import { useCallback, useEffect, useRef, useState, type JSX } from "react";
import {
  applyMaintenance,
  armMaintenance,
  getMaintenanceHistory,
  getMaintenanceStatus,
  getTorrentClients,
  maintenanceCommand,
  previewMaintenance,
} from "../api/client";
import { useInterval } from "../hooks/useInterval";
import type {
  MaintenanceOperation,
  MaintenancePlan,
  MaintenanceRunSummary,
  MaintenanceStatus,
  TorrentClientInfo,
} from "../api/types";

const operations: MaintenanceOperation[] = [
  "OrphanScan",
  "HardlinkAudit",
  "UnregisteredCleanup",
  "CategoryReconcile",
  "AutomaticManagement",
  "PrivateTagging",
  "TrackerErrorTagging",
  "RepairPaused",
  "RecycleRetention",
];

function groupMessages(messages: string[]): [string, number][] {
  const counts = new Map<string, number>();
  for (const message of messages) {
    counts.set(message, (counts.get(message) ?? 0) + 1);
  }
  return [...counts];
}

export function MaintenanceView({
  onOpenConfig,
}: {
  onOpenConfig?: () => void;
}): JSX.Element {
  const initialized = useRef(false);
  const [clients, setClients] = useState<TorrentClientInfo[]>([]);
  const [selected, setSelected] = useState<string[]>([]);
  const [selectedOps, setSelectedOps] =
    useState<MaintenanceOperation[]>(operations);
  const [status, setStatus] = useState<MaintenanceStatus>();
  const [history, setHistory] = useState<MaintenanceRunSummary[]>([]);
  const [plan, setPlan] = useState<MaintenancePlan>();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");

  const refresh = useCallback(async () => {
    const [nextClients, nextStatus, nextHistory] = await Promise.all([
      getTorrentClients(),
      getMaintenanceStatus(),
      getMaintenanceHistory(),
    ]);
    setClients(nextClients);
    setStatus(nextStatus);
    setHistory(nextHistory);
    if (!initialized.current) {
      initialized.current = true;
      setSelected(nextClients.map((client) => client.id));
    }
  }, []);
  useEffect(() => {
    void refresh().catch((e) => setError(String(e)));
  }, [refresh]);

  useInterval(
    () => {
      void refresh().catch((e) => setError(String(e)));
    },
    busy || status?.running ? 2000 : null,
  );

  const act = async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError("");
    try {
      await action();
      await refresh();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const destructive =
    plan?.actions.filter((a) => a.destructive && !a.blockedReason) ?? [];

  return (
    <section
      className="card maintenance-view"
      aria-labelledby="maintenance-title"
    >
      <div className="card-header">
        <div>
          <h2 id="maintenance-title">Maintenance</h2>
          <p className="muted">
            Preview client-neutral cleanup and lifecycle actions before enabling
            mutations.
          </p>
        </div>
      </div>
      {error && (
        <div className="alert error" role="alert">
          {error}
        </div>
      )}
      <div className="card">
        <h3>Torrent clients</h3>
        {clients.map((client) => (
          <label
            key={client.id}
            style={{ display: "block", margin: ".5rem 0" }}
          >
            <input
              type="checkbox"
              checked={selected.includes(client.id)}
              disabled={busy || status?.running}
              onChange={() => {
                setPlan(undefined);
                setSelected((v) =>
                  v.includes(client.id)
                    ? v.filter((id) => id !== client.id)
                    : [...v, client.id],
                );
              }}
            />{" "}
            {client.id} · {client.type} ·{" "}
            {client.connected
              ? `connected ${client.version ?? ""}`
              : "disconnected"}
            {status?.armed[client.id]
              ? ` · armed: ${(status.armedOperations?.[client.id] ?? []).join(", ")}`
              : " · disarmed"}
          </label>
        ))}
      </div>
      <div className="card">
        <h3>Operations</h3>
        <div className="chip-list">
          {operations.map((operation) => (
            <label key={operation} className="chip">
              <input
                type="checkbox"
                checked={selectedOps.includes(operation)}
                disabled={busy || status?.running}
                onChange={() => {
                  setPlan(undefined);
                  setSelectedOps((v) =>
                    v.includes(operation)
                      ? v.filter((x) => x !== operation)
                      : [...v, operation],
                  );
                }}
              />{" "}
              {operation.replace(/([a-z])([A-Z])/g, "$1 $2")}
            </label>
          ))}
        </div>
        <div className="actions" style={{ marginTop: "1rem" }}>
          <button
            className="btn primary"
            disabled={busy || selected.length === 0 || selectedOps.length === 0}
            onClick={() =>
              void act(async () =>
                setPlan(await previewMaintenance(selected, selectedOps)),
              )
            }
          >
            Preview
          </button>
          <button
            className="btn"
            disabled={
              busy ||
              !plan ||
              plan.criticalErrors.length > 0 ||
              (plan.operationResults?.length > 0 &&
                !plan.operationResults.some(
                  (r) => r.state === "ready" || r.state === "partial",
                ))
            }
            onClick={() =>
              void act(() => armMaintenance(plan!.planId, selected))
            }
          >
            Arm
          </button>
          <button
            className="btn"
            disabled={busy}
            onClick={() =>
              void act(() => maintenanceCommand("disarm", selected))
            }
          >
            Disarm
          </button>
          <button
            className="btn"
            disabled={
              busy ||
              status?.running ||
              selected.length === 0 ||
              selectedOps.length === 0 ||
              !selected.some((id) => status?.armed[id])
            }
            onClick={() => {
              if (
                window.confirm(
                  "Run selected operations that have been reviewed and armed? Unarmed operations will be skipped.",
                )
              )
                void act(() =>
                  maintenanceCommand(
                    "run",
                    selected.filter((id) => status?.armed[id]),
                    selectedOps,
                  ),
                );
            }}
          >
            Run now
          </button>
          <button
            className="btn danger"
            disabled={!status?.running}
            onClick={() => void act(() => maintenanceCommand("cancel"))}
          >
            Cancel
          </button>
        </div>
      </div>
      {plan && (
        <div className="card">
          <h3>Preview</h3>
          <p>
            <strong>{plan.candidateCount}</strong> actions ·{" "}
            <strong>{destructive.length}</strong> destructive ·{" "}
            {plan.candidateBytes.toLocaleString()} bytes · {plan.blockedCount}{" "}
            blocked · {plan.diagnostics?.length ?? plan.warnings.length}{" "}
            diagnostic issues
          </p>
          {groupMessages(plan.criticalErrors).map(([message, count]) => (
            <div className="alert error" key={message}>
              {message}
              {count > 1 && ` (${count} occurrences)`}
            </div>
          ))}
          {plan.operationResults?.length > 0 && (
            <table>
              <caption>Operation readiness and inspection coverage</caption>
              <thead>
                <tr>
                  <th>Client / operation</th>
                  <th>Status</th>
                  <th>Examined</th>
                  <th>Excluded</th>
                  <th>Uninspectable</th>
                  <th>Actions</th>
                  <th>Blocked actions</th>
                </tr>
              </thead>
              <tbody>
                {plan.operationResults.map((result) => (
                  <tr key={`${result.clientInstanceId}-${result.operation}`}>
                    <th scope="row">
                      {result.clientInstanceId} / {result.operation}
                    </th>
                    <td>
                      {result.state}
                      {result.reason && <p>{result.reason}</p>}
                      {result.state === "ready" && result.actionable === 0 && (
                        <p>Nothing needs changing.</p>
                      )}
                    </td>
                    <td>{result.examined}</td>
                    <td>{result.excluded}</td>
                    <td>{result.uninspectable}</td>
                    <td>{result.actionable}</td>
                    <td>{result.blockedActions}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
          {(plan.diagnostics?.length > 0 || plan.warnings.length > 0) && (
            <details className="alert warning">
              <summary>
                {plan.diagnostics?.length ?? plan.warnings.length} diagnostic
                issues — view details
              </summary>
              <p>
                Inspection issues are separate from proposed actions. Blocked
                inspections do not mean that no changes are needed.
              </p>
              {onOpenConfig && (
                <button className="btn" onClick={onOpenConfig}>
                  Open Config
                </button>
              )}
              <ul>
                {plan.diagnostics
                  ? plan.diagnostics.map((diagnostic) => (
                      <li
                        key={`${diagnostic.clientInstanceId}-${diagnostic.code}`}
                      >
                        <strong>
                          [{diagnostic.clientInstanceId}] {diagnostic.message}
                        </strong>
                        <p>
                          {diagnostic.affectedFiles} files ·{" "}
                          {diagnostic.affectedTorrents} torrents ·{" "}
                          {diagnostic.operations.join(", ")}
                        </p>
                        <p>{diagnostic.remediation}</p>
                        <ul>
                          {diagnostic.examples.map((example, index) => (
                            <li key={index}>
                              {example.torrentName ?? example.torrentHash}{" "}
                              {example.clientPath && (
                                <code>{example.clientPath}</code>
                              )}{" "}
                              {example.localPath && (
                                <>
                                  {" "}
                                  → <code>{example.localPath}</code>
                                </>
                              )}
                            </li>
                          ))}
                        </ul>
                      </li>
                    ))
                  : groupMessages(plan.warnings).map(([message, count]) => (
                      <li key={message}>
                        {message}
                        {count > 1 && ` (${count} occurrences)`}
                      </li>
                    ))}
              </ul>
            </details>
          )}
          <ul>
            {plan.actions.map((action) => (
              <li key={action.id}>
                <strong>{action.kind}</strong>{" "}
                {action.torrentName ?? action.path} —{" "}
                {action.blockedReason ?? action.reason}
              </li>
            ))}
          </ul>
          <button
            className="btn danger"
            disabled={
              busy ||
              plan.criticalErrors.length > 0 ||
              plan.candidateCount === 0 ||
              plan.actions.some(
                (action) =>
                  !action.blockedReason &&
                  !status?.armed[action.clientInstanceId],
              )
            }
            onClick={() => {
              if (
                window.confirm(
                  `Apply ${plan.candidateCount} actions, including ${destructive.length} destructive content actions?`,
                )
              )
                void act(async () => {
                  await applyMaintenance(plan.planId);
                  setPlan(undefined);
                });
            }}
          >
            Apply preview
          </button>
        </div>
      )}
      {status?.currentRun && (
        <div className="card" role="status">
          <h3>Running</h3>
          <p>
            {status.currentRun.applied} applied · {status.currentRun.skipped}{" "}
            skipped · {status.currentRun.failed} failed ·{" "}
            {status.currentRun.pending} pending · {status.currentRun.blocked}{" "}
            blocked actions
          </p>
        </div>
      )}
      <div className="card">
        <h3>History</h3>
        {history.length === 0 ? (
          <p className="muted">No completed maintenance runs.</p>
        ) : (
          <ul>
            {history.map((run) => (
              <li key={run.runId}>
                {new Date(run.startedAt).toLocaleString()} · {run.trigger} ·{" "}
                {run.result} · {run.applied}/{run.planned} applied ·{" "}
                {run.skipped} skipped · {run.failed} failed · {run.blocked ?? 0}{" "}
                blocked actions
                {run.operationResults
                  ?.filter(
                    (r) => r.state === "blocked" || r.state === "partial",
                  )
                  .map((result) => (
                    <p key={`${result.clientInstanceId}-${result.operation}`}>
                      {result.clientInstanceId} / {result.operation}:{" "}
                      {result.state} — {result.reason}
                    </p>
                  ))}
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
