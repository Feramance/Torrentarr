import { useCallback, useEffect, useRef, useState, type JSX } from "react";
import equal from "fast-deep-equal";
import { get, set } from "lodash-es";
import {
  ConfigApiError,
  applyMaintenance,
  getConfig,
  getConfigSchema,
  updateConfig,
  getMaintenanceHistory,
  getMaintenanceStatus,
  getTorrentClients,
  maintenanceCommand,
  previewMaintenance,
} from "../api/client";
import type {
  ConfigDocument,
  ConfigurationApplicationStatus,
  MaintenanceOperation,
  MaintenancePlan,
  MaintenanceRunSummary,
  MaintenanceStatus,
  TorrentClientInfo,
} from "../api/types";
import {
  maintenanceActionTitle,
  maintenanceCategories,
  maintenanceOperations,
  maintenanceToolForPath,
} from "../config/maintenanceOperations";
import { useInterval } from "../hooks/useInterval";
import { FieldGroup } from "./config/configFieldComponents";
import { configureConfigFields, getClientFields } from "./config/configFields";
import {
  buildSectionChanges,
  getValue,
  parseFieldValue,
  setValue,
  validationErrorsFromApi,
} from "./config/configDocumentUtils";
import { validateFieldGroup } from "./config/configValidation";
import { formatConfigSaveMessage } from "./config/configSaveResult";
import type { FieldDefinition, ValidationError } from "./config/configTypes";

const bytes = (value: number) =>
  value >= 1024 ** 3
    ? `${(value / 1024 ** 3).toFixed(1)} GiB`
    : value >= 1024 ** 2
      ? `${(value / 1024 ** 2).toFixed(1)} MiB`
      : `${value.toLocaleString()} bytes`;
const time = (value?: string | null) =>
  value
    ? new Date(value).toLocaleString(undefined, { timeZoneName: "short" })
    : "—";

export function MaintenanceView({
  active = true,
  requestedClientId,
  onDirtyChange,
}: {
  active?: boolean;
  requestedClientId?: string;
  onDirtyChange?: (dirty: boolean) => void;
}): JSX.Element {
  const [clients, setClients] = useState<TorrentClientInfo[]>([]);
  const [clientId, setClientId] = useState("");
  const [config, setConfig] = useState<ConfigDocument>({});
  const [draft, setDraft] = useState<ConfigDocument>({});
  const [fields, setFields] = useState<FieldDefinition[]>([]);
  const [loaded, setLoaded] = useState(false);
  const [status, setStatus] = useState<MaintenanceStatus>();
  const [history, setHistory] = useState<MaintenanceRunSummary[]>([]);
  const [plan, setPlan] = useState<MaintenancePlan>();
  const [expanded, setExpanded] = useState<MaintenanceOperation>();
  const [sharedOpen, setSharedOpen] = useState(false);
  const [errors, setErrors] = useState<ValidationError[]>([]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const [application, setApplication] =
    useState<ConfigurationApplicationStatus>();
  const [notice, setNotice] = useState("");
  const [page, setPage] = useState(0);
  const [now, setNow] = useState(Date.now);
  const resultsRef = useRef<HTMLDetailsElement>(null);
  const historyRef = useRef<HTMLDetailsElement>(null);
  const sharedRef = useRef<HTMLDetailsElement>(null);
  const pendingClient = useRef(requestedClientId);
  useEffect(() => {
    const receive = (event: Event) => {
      const next = (event as CustomEvent<ConfigurationApplicationStatus>)
        .detail;
      setApplication((current) =>
        !current || next.acceptedRevision >= current.acceptedRevision
          ? next
          : current,
      );
    };
    window.addEventListener("torrentarr:configuration", receive);
    return () =>
      window.removeEventListener("torrentarr:configuration", receive);
  }, []);

  const dirty = !equal(
    draft.Maintenance,
    (get(config, clientPath(clientId), {}) as ConfigDocument).Maintenance,
  );
  const dirtyRef = useRef(false);
  dirtyRef.current = loaded && dirty;
  useEffect(() => {
    onDirtyChange?.(loaded && dirty);
  }, [dirty, loaded, onDirtyChange]);
  function clientPath(id: string): string[] {
    return Object.hasOwn(config, id) ? [id] : ["TorrentClient", id];
  }
  function selectClient(id: string, source = config) {
    if (
      dirtyRef.current &&
      !window.confirm("Discard unsaved Manage settings and switch clients?")
    )
      return;
    const path = Object.hasOwn(source, id) ? [id] : ["TorrentClient", id];
    setClientId(id);
    setDraft(structuredClone(get(source, path, {}) as ConfigDocument));
    setPlan(undefined);
    setErrors([]);
    setPage(0);
    setNotice("");
  }
  const refresh = useCallback(async () => {
    const [nextClients, nextStatus, nextHistory] = await Promise.all([
      getTorrentClients(),
      getMaintenanceStatus(),
      getMaintenanceHistory(),
    ]);
    setClients(nextClients);
    setStatus(nextStatus);
    setHistory(nextHistory);
    setNow(Date.now());
    setPlan((current) =>
      current &&
      nextStatus.configurationFingerprint &&
      current.configurationFingerprint !== nextStatus.configurationFingerprint
        ? undefined
        : current,
    );
  }, []);
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    void Promise.all([getConfig(), getConfigSchema()])
      .then(([document, schema]) => {
        if (cancelled || dirtyRef.current) return;
        configureConfigFields(schema);
        setFields(
          getClientFields().filter(
            (field) => field.path?.[0] === "Maintenance",
          ),
        );
        setConfig(document);
        setLoaded(true);
        const ids = [
          ...Object.keys(document).filter((key) => /^qBit(-.*)?$/i.test(key)),
          ...Object.keys((document.TorrentClient as ConfigDocument) ?? {}),
        ];
        const selected =
          pendingClient.current && ids.includes(pendingClient.current)
            ? pendingClient.current
            : ids.includes(clientId)
              ? clientId
              : ids[0] ?? "";
        const path = Object.hasOwn(document, selected)
          ? [selected]
          : ["TorrentClient", selected];
        setClientId(selected);
        setDraft(structuredClone(get(document, path, {}) as ConfigDocument));
      })
      .catch((e) => setError(String(e)));
    void refresh().catch((e) => setError(String(e)));
    return () => {
      cancelled = true;
    };
    // Reload saved settings when returning, while preserving a pending draft.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [active, refresh]);
  useEffect(() => {
    pendingClient.current = requestedClientId;
    if (loaded && requestedClientId && requestedClientId !== clientId)
      selectClient(requestedClientId);
    // The requested identity changes only on explicit navigation.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [requestedClientId]);
  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (dirtyRef.current) {
        event.preventDefault();
        event.returnValue = "";
      }
    };
    window.addEventListener("beforeunload", beforeUnload);
    return () => window.removeEventListener("beforeunload", beforeUnload);
  }, []);
  useInterval(
    () => {
      if (active) void refresh().catch((e) => setError(String(e)));
    },
    status?.running || busy ? 1000 : 10000,
  );
  const act = async (work: () => Promise<void>) => {
    setBusy(true);
    setError("");
    setNotice("");
    try {
      await work();
      await refresh();
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  };
  const revealErrors = (next: ValidationError[]) => {
    setErrors(next);
    const fieldPath = next[0]?.path.slice(clientPath(clientId).length) ?? [];
    const operation = maintenanceToolForPath(fieldPath);
    if (operation) setExpanded(operation);
    else setSharedOpen(true);
  };
  const save = async () => {
    const path = clientPath(clientId);
    const validation: ValidationError[] = [];
    validateFieldGroup(validation, fields, draft, path, {
      root: config,
      section: draft,
      sectionKey: path.join("."),
    });
    if (validation.length) {
      revealErrors(validation);
      return;
    }
    setBusy(true);
    setError("");
    setNotice("");
    try {
      const next = structuredClone(config);
      set(next, path, draft);
      const changes = buildSectionChanges(
        next,
        config,
        [...path, "Maintenance"].join("."),
        new Map(),
      );
      if (!Object.keys(changes).length) return;
      const result = await updateConfig({ changes });
      setApplication(result.application);
      const saved = await getConfig();
      setConfig(saved);
      setDraft(structuredClone(get(saved, path, {}) as ConfigDocument));
      setErrors([]);
      setPlan(undefined);
      setNotice(
        formatConfigSaveMessage(
          result.reloadType,
          result.configReloaded,
          result.affectedInstances,
          Object.keys(changes),
          result.application,
        ),
      );
      window.dispatchEvent(
        new CustomEvent("torrentarr:manage-saved", {
          detail: { path, maintenance: get(saved, [...path, "Maintenance"]) },
        }),
      );
      await refresh();
    } catch (e) {
      if (e instanceof ConfigApiError && e.validationErrors.length)
        revealErrors(validationErrorsFromApi(e.validationErrors));
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };
  const edit = (path: string[], field: FieldDefinition, value: unknown) => {
    setDraft((previous) => {
      const next = structuredClone(previous);
      setValue(next, path, parseFieldValue(field, value));
      return next;
    });
    setPlan(undefined);
    setErrors([]);
    setNotice("");
  };
  const renderFields = (
    selected: FieldDefinition[],
    title: string | null,
    defaultOpen = true,
  ) =>
    selected.length ? (
      <FieldGroup
        title={title}
        fields={selected}
        state={draft}
        basePath={[]}
        sectionKey={clientPath(clientId).join(".")}
        onChange={edit}
        defaultOpen={defaultOpen}
        validationErrors={errors}
      />
    ) : null;
  const shared = fields.filter(
    (field) => !maintenanceToolForPath(field.path ?? []),
  );
  const expired = !!plan && new Date(plan.expiresAt).getTime() <= now;
  const candidateActions = plan?.actions.filter((a) => !a.blockedReason) ?? [];
  const toRecycle = candidateActions.filter(
    (a) => a.kind === "QuarantineFile" || a.kind === "RecycleContent",
  );
  const toPurge = candidateActions.filter(
    (a) => a.kind === "DeleteExpiredRecycleItem",
  );
  const selectedHistory = history.filter((run) =>
    run.clientInstanceIds.includes(clientId),
  );
  const running = status?.running || false;
  const ready =
    !!plan &&
    !expired &&
    !plan.isDraft &&
    !plan.criticalErrors.length &&
    candidateActions.length > 0 &&
    !dirty;
  const preview = (operation: MaintenanceOperation) =>
    void act(async () => {
      setPlan(undefined);
      setPage(0);
      setPlan(await previewMaintenance([clientId], [operation]));
      if (resultsRef.current) resultsRef.current.open = true;
    });
  return (
    <section
      aria-labelledby="manage-title"
      className="card maintenance-workspace"
    >
      <div className="card-header">
        <h2 id="manage-title">Manage</h2>
      </div>
      <div className="card-body">
        <p className="hint">
          Configure and run recovery, organization, and storage tools for each
          torrent client.
        </p>
        <div className="actions manage-toolbar">
          <div className="field">
            <label htmlFor="manage-client">Torrent client</label>
            <select
              id="manage-client"
              aria-label="Torrent client"
              value={clientId}
              disabled={busy}
              onChange={(event) => selectClient(event.target.value)}
            >
              {clients.map((client) => (
                <option key={client.id} value={client.id}>
                  {client.id}
                </option>
              ))}
            </select>
          </div>
          <button
            className="btn primary"
            disabled={!loaded || !clientId || !dirty || busy || running}
            onClick={() => void save()}
          >
            Save settings
          </button>
          <button
            className="btn"
            disabled={!dirty || busy}
            onClick={() => {
              setDraft(
                structuredClone(
                  get(config, clientPath(clientId), {}) as ConfigDocument,
                ),
              );
              setErrors([]);
              setNotice("");
            }}
          >
            Discard changes
          </button>
        </div>
        {dirty && (
          <p role="status">
            Unsaved settings. Save before checking or running tools.
          </p>
        )}
        {!loaded && !error && <p>Loading settings…</p>}
        {loaded && !clientId && <p>No torrent clients configured.</p>}
        {error && <p role="alert">{error}</p>}
        {notice && <p role="status">{notice}</p>}
        {application && (
          <p role="status">
            {application.error ??
              (application.state === "applied"
                ? "Configuration applied live"
                : application.state === "pending"
                  ? "Configuration saved; waiting for active operations to finish"
                  : "Configuration could not be applied completely")}
            {Object.entries(application.components ?? {})
              .filter(([, component]) => component.error)
              .map(([name, component]) => (
                <span key={name}>
                  {" "}
                  {name}: {component.error}
                </span>
              ))}
          </p>
        )}
        {errors.length > 0 && (
          <ul role="alert">
            {errors.map(({ path, message }) => (
              <li key={path.join(".")}>
                {message}{" "}
                <button
                  className="btn ghost"
                  onClick={() => revealErrors([{ path, message }])}
                >
                  Review setting
                </button>
              </li>
            ))}
          </ul>
        )}
        {clientId && loaded && (
          <>
            <details
              ref={sharedRef}
              open={sharedOpen}
              onToggle={(event) => setSharedOpen(event.currentTarget.open)}
              className="config-section"
            >
              <summary>
                Shared settings ·{" "}
                {getValue(draft, ["Maintenance", "Enabled"])
                  ? "enabled"
                  : "disabled"}
              </summary>
              <div className="config-section__body">
                <p>
                  Scope, paths, recycling, and safeguards shared by these tools.
                  Manual applies a reviewed preview once. Automatic runs on the
                  saved schedule, including after restart. Saving enables future
                  scheduled runs and never executes a tool immediately.
                </p>
                {renderFields(
                  shared.filter((field) =>
                    ["Enabled", "Scope", "PathMappings"].includes(
                      field.path?.[1] ?? "",
                    ),
                  ),
                  "Shared configuration",
                )}
                {renderFields(
                  shared.filter((field) => field.path?.[1] === "RecycleBin"),
                  "Recycle bin",
                )}
                {renderFields(
                  shared.filter(
                    (field) =>
                      ![
                        "Enabled",
                        "Scope",
                        "PathMappings",
                        "RecycleBin",
                      ].includes(field.path?.[1] ?? ""),
                  ),
                  "Advanced",
                  false,
                )}
              </div>
            </details>
            {maintenanceCategories.map((category) => (
              <div className="manage-group" key={category}>
                <h3>{category}</h3>
                {maintenanceOperations
                  .filter((tool) => tool.category === category)
                  .map((tool) => {
                    const toolFields = fields.filter(
                      (field) =>
                        maintenanceToolForPath(field.path ?? []) ===
                        tool.operation,
                    );
                    const policyPath = [
                      "Maintenance",
                      "Operations",
                      tool.operation,
                    ];
                    const mode =
                      getValue(draft, [...policyPath, "ExecutionMode"]) ??
                      "manual";
                    const schedule = String(
                      getValue(draft, [...policyPath, "Schedule"]) ||
                        getValue(draft, ["Maintenance", "Schedule"]) ||
                        "Not configured",
                    );
                    const visibleFields = toolFields.filter(
                      (field) =>
                        field.path?.at(-1) !== "Schedule" ||
                        mode === "automatic" ||
                        errors.some((error) =>
                          equal(error.path, [
                            ...clientPath(clientId),
                            ...(field.path ?? []),
                          ]),
                        ),
                    );
                    const essential = visibleFields.filter((field) => {
                      const key = field.path?.slice(1).join(".") ?? "";
                      return (
                        tool.essential.includes(key) ||
                        key === `Operations.${tool.operation}.Enabled` ||
                        key === `Operations.${tool.operation}.ExecutionMode` ||
                        key === `Operations.${tool.operation}.Schedule`
                      );
                    });
                    const advanced = visibleFields.filter(
                      (field) => !essential.includes(field),
                    );
                    const operationStatus = status?.operations?.find(
                      (item) =>
                        item.clientInstanceId === clientId &&
                        item.operation === tool.operation,
                    );
                    const latest = selectedHistory.find((run) =>
                      run.operationResults?.some(
                        (result) => result.operation === tool.operation,
                      ),
                    );
                    return (
                      <details
                        className="config-section manage-tool"
                        key={tool.operation}
                        open={expanded === tool.operation}
                        onToggle={(event) => {
                          if (event.currentTarget.open)
                            setExpanded(tool.operation);
                          else
                            setExpanded((current) =>
                              current === tool.operation ? undefined : current,
                            );
                        }}
                      >
                        <summary>
                          <strong>{tool.title}</strong>
                          <span className="manage-tool-status hint">
                            {operationStatus?.enabled ? "Enabled" : "Disabled"}{" "}
                            ·{" "}
                            {operationStatus?.executionMode === "automatic"
                              ? "Automatic"
                              : "Manual"}{" "}
                            · {latest?.result ?? "Not run"}
                            {dirty ? " · unsaved settings" : ""}
                          </span>
                          <span className="manage-tool-purpose hint">
                            {tool.description}
                          </span>
                        </summary>
                        <div className="config-section__body">
                          {renderFields(essential, null)}
                          {mode === "automatic" && (
                            <p className="hint">
                              Effective schedule (UTC): {schedule}. Save to
                              activate future scheduled runs.
                            </p>
                          )}
                          {renderFields(advanced, "Advanced", false)}
                          <button
                            type="button"
                            className="btn ghost"
                            onClick={() => {
                              setSharedOpen(true);
                              sharedRef.current?.scrollIntoView?.({
                                block: "nearest",
                              });
                            }}
                          >
                            Shared settings
                          </button>
                          <p>Next run: {time(operationStatus?.nextRun)}</p>
                          <div className="actions">
                            <button
                              className="btn"
                              disabled={
                                dirty ||
                                busy ||
                                running ||
                                !operationStatus?.enabled
                              }
                              onClick={() => preview(tool.operation)}
                            >
                              Check {tool.title.toLowerCase()}
                            </button>
                            {operationStatus?.authorized &&
                              operationStatus.executionMode === "automatic" && (
                                <button
                                  className="btn"
                                  disabled={dirty || busy || running}
                                  onClick={() =>
                                    void act(async () => {
                                      const result = (await maintenanceCommand(
                                        "run",
                                        [clientId],
                                        [tool.operation],
                                      )) as MaintenanceRunSummary;
                                      setNotice(
                                        `${result.result}: ${result.applied} changes applied.`,
                                      );
                                      if (historyRef.current)
                                        historyRef.current.open = true;
                                    })
                                  }
                                >
                                  Run now
                                </button>
                              )}
                          </div>
                        </div>
                      </details>
                    );
                  })}
              </div>
            ))}
          </>
        )}
        {running && (
          <p role="status">
            Run in progress: {status?.currentRun?.applied ?? 0} applied,{" "}
            {status?.currentRun?.failed ?? 0} failed. An action already sent may
            finish after cancellation.{" "}
            <button
              className="btn"
              onClick={() =>
                void act(async () => {
                  await maintenanceCommand("cancel");
                })
              }
            >
              Cancel run
            </button>
          </p>
        )}
        <details ref={resultsRef} className="config-section">
          <summary>
            Preview results
            {plan
              ? ` · ${candidateActions.length} changes available`
              : " · Not checked"}
          </summary>
          <div className="config-section__body">
            {!plan ? (
              <p>
                Not checked. Opening this page does not scan or change files.
              </p>
            ) : (
              <>
                {expired && (
                  <p role="status">
                    Preview expired. Check again before applying.
                  </p>
                )}
                {plan.criticalErrors.map((message) => (
                  <p role="alert" key={message}>
                    {message}
                  </p>
                ))}
                <p>
                  To recycle: {toRecycle.length} files (
                  {bytes(toRecycle.reduce((sum, a) => sum + a.bytes, 0))}). To
                  purge: {toPurge.length} entries (
                  {bytes(toPurge.reduce((sum, a) => sum + a.bytes, 0))}).
                  Blocked changes: {plan.blockedCount}.
                </p>
                <ul>
                  {plan.operationResults?.map((result) => (
                    <li key={result.operation}>
                      <strong>
                        {result.operation === "OrphanScan"
                          ? "Orphan files"
                          : "Recycle retention"}
                        :{" "}
                      </strong>
                      {result.state === "disabled"
                        ? "Not checked — disabled"
                        : result.state === "blocked"
                          ? "Blocked"
                          : result.state === "partial"
                            ? "Partial"
                            : result.actionable
                              ? "Changes available"
                              : "Nothing found"}
                      {result.reason && ` — ${result.reason}`}
                    </li>
                  ))}
                </ul>
                {plan.diagnostics?.map((d) => (
                  <p key={`${d.clientInstanceId}-${d.code}`}>
                    {d.message} {d.remediation}
                    {d.examples?.map((example, index) => (
                      <span className="cleanup-path" key={index}>
                        {" "}
                        Source: {example.clientPath} · Recycled:{" "}
                        {example.localPath}
                      </span>
                    ))}
                  </p>
                ))}
                {plan.actions.length > 0 && (
                  <div className="table-wrap">
                    <table>
                      <thead>
                        <tr>
                          <th>Change</th>
                          <th>Path</th>
                          <th>Reason</th>
                        </tr>
                      </thead>
                      <tbody>
                        {plan.actions
                          .slice(page * 25, (page + 1) * 25)
                          .map((action) => (
                            <tr key={action.id}>
                              <td>{maintenanceActionTitle(action)}</td>
                              <td className="cleanup-path">{action.path}</td>
                              <td>{action.blockedReason ?? action.reason}</td>
                            </tr>
                          ))}
                      </tbody>
                    </table>
                  </div>
                )}
                {plan.actions.length > 25 && (
                  <div className="actions">
                    <button
                      className="btn"
                      disabled={page === 0}
                      onClick={() => setPage((p) => p - 1)}
                    >
                      Previous
                    </button>
                    <span>Page {page + 1}</span>
                    <button
                      className="btn"
                      disabled={(page + 1) * 25 >= plan.actions.length}
                      onClick={() => setPage((p) => p + 1)}
                    >
                      Next
                    </button>
                  </div>
                )}
              </>
            )}
            <button
              className="btn primary"
              disabled={!ready || busy || running}
              onClick={() =>
                void act(async () => {
                  const reviewed = plan!;
                  setPlan(undefined);
                  const result = await applyMaintenance(reviewed.planId, true);
                  setNotice(
                    `${result.result}: ${result.applied} changes applied. Recycled ${bytes(result.bytesMoved)}; reclaimed ${bytes(result.bytesReclaimed ?? 0)}.`,
                  );
                  if (historyRef.current) historyRef.current.open = true;
                })
              }
            >
              Apply reviewed changes
              {candidateActions.length ? ` (${candidateActions.length})` : ""}
            </button>
          </div>
        </details>
        <details ref={historyRef} className="config-section">
          <summary>Recent runs</summary>
          <div className="config-section__body">
            {!selectedHistory.length ? (
              <p>No runs yet.</p>
            ) : (
              <div className="table-wrap" style={{ overflowX: "auto" }}>
                <table style={{ width: "100%", minWidth: "780px", tableLayout: "fixed" }}>
                  <colgroup>
                    <col style={{ width: "16%" }} />
                    <col style={{ width: "10%" }} />
                    <col style={{ width: "12%" }} />
                    <col style={{ width: "12%" }} />
                    <col style={{ width: "50%" }} />
                  </colgroup>
                  <thead>
                    <tr>
                      <th>Started</th>
                      <th>Result</th>
                      <th>Recycled</th>
                      <th>Reclaimed</th>
                      <th>Details</th>
                    </tr>
                  </thead>
                  <tbody>
                    {selectedHistory.slice(0, 20).map((run) => (
                      <tr key={run.runId}>
                        <td style={{ overflowWrap: "anywhere" }}>{time(run.startedAt)}</td>
                        <td style={{ overflowWrap: "anywhere" }}>{run.result}</td>
                        <td>{bytes(run.bytesMoved)}</td>
                        <td>{bytes(run.bytesReclaimed ?? 0)}</td>
                        <td>
                          {run.errors.length ? (
                            <details>
                              <summary>{run.failed} failed · {run.errors.length} error(s) — view details</summary>
                              <ul style={{ margin: "0.5rem 0", paddingLeft: "1.25rem" }}>
                                {run.errors.map((error, index) => <li key={index} style={{ overflowWrap: "anywhere", whiteSpace: "pre-wrap" }}>{error}</li>)}
                              </ul>
                            </details>
                          ) : `${run.applied} applied, ${run.skipped} skipped, ${run.failed} failed`}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </div>
        </details>
      </div>
    </section>
  );
}
