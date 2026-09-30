import { useCallback, useEffect, useState, type JSX } from "react";
import {
  applyMaintenance,
  armMaintenance,
  getMaintenanceHistory,
  getMaintenanceStatus,
  getTorrentClients,
  maintenanceCommand,
  previewMaintenance,
} from "../api/client";
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
  "SharePolicy",
  "RecycleRetention",
];

export function MaintenanceView(): JSX.Element {
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
    setSelected((current) =>
      current.length ? current : nextClients.map((client) => client.id),
    );
  }, []);
  useEffect(() => {
    void refresh().catch((e) => setError(String(e)));
  }, [refresh]);

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
    <section aria-labelledby="maintenance-title">
      <div className="page-header">
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
              onChange={() =>
                setSelected((v) =>
                  v.includes(client.id)
                    ? v.filter((id) => id !== client.id)
                    : [...v, client.id],
                )
              }
            />{" "}
            {client.id} · {client.type} ·{" "}
            {client.connected
              ? `connected ${client.version ?? ""}`
              : "disconnected"}
            {status?.armed[client.id] ? " · armed" : " · disarmed"}
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
                onChange={() =>
                  setSelectedOps((v) =>
                    v.includes(operation)
                      ? v.filter((x) => x !== operation)
                      : [...v, operation],
                  )
                }
              />{" "}
              {operation}
            </label>
          ))}
        </div>
        <div className="actions" style={{ marginTop: "1rem" }}>
          <button
            className="btn primary"
            disabled={busy || selected.length === 0}
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
            disabled={busy || !plan || plan.criticalErrors.length > 0}
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
            disabled={busy || status?.running}
            onClick={() => void act(() => maintenanceCommand("run", selected))}
          >
            Run now
          </button>
          <button
            className="btn danger"
            disabled={busy || !status?.running}
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
            blocked
          </p>
          {plan.criticalErrors.map((message) => (
            <div className="alert error" key={message}>
              {message}
            </div>
          ))}
          {plan.warnings.map((message) => (
            <div className="alert warning" key={message}>
              {message}
            </div>
          ))}
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
              plan.candidateCount === 0
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
      <div className="card">
        <h3>History</h3>
        {history.length === 0 ? (
          <p className="muted">No completed maintenance runs.</p>
        ) : (
          <ul>
            {history.map((run) => (
              <li key={run.runId}>
                {new Date(run.startedAt).toLocaleString()} · {run.trigger} ·{" "}
                {run.result} · {run.applied}/{run.planned} applied
              </li>
            ))}
          </ul>
        )}
      </div>
    </section>
  );
}
