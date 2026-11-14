import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import type { ConfigDocument } from "../../api/types";
import { getTrackerSummary } from "../../config/torrentHandlingSummary";
import { ShellDialog } from "../../components/ShellDialog";
import { TrackerCard } from "./configFieldComponents";
import { getSchemaDefaults } from "./configFields";
import {
  createDefaultTrackerEntry,
  focusFirstValidationError,
} from "./configDocumentUtils";
import { trackerRuleFields, trackerScope } from "./trackerRuleFields";
import type { FieldDefinition, ValidationError } from "./configTypes";

export function TrackerSettings({
  rules,
  clientIds,
  integrationIds,
  onChange,
  onReplace,
  onSave,
  dirty,
  saving,
}: {
  rules: ConfigDocument[];
  clientIds: string[];
  integrationIds: string[];
  onChange: (path: string[], def: FieldDefinition, value: unknown) => void;
  onReplace: (rules: ConfigDocument[]) => void;
  onSave: (onValidationError: (error: ValidationError) => void) => void;
  dirty: boolean;
  saving: boolean;
}) {
  const [selection, setSelection] = useState<{
    index: number;
    error?: ValidationError;
  } | null>(null);
  const [assignment, setAssignment] = useState<{
    key: "ClientIds" | "ArrInstances";
    ClientIds: string[];
    ArrInstances: string[];
  } | null>(null);
  const editor = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  const index = selection?.index ?? -1;
  const rule = rules[index];
  const { key, ids } = trackerScope(rule ?? {});
  const scopeLabel =
    key === "ClientIds" ? "Torrent clients" : "Arr integrations";

  useEffect(() => {
    const panel = editor.current;
    if (!selection || !panel) return;
    panel.focus();
    if (selection.error) focusFirstValidationError([selection.error]);
  }, [selection]);

  const select = (index: number, error?: ValidationError) => {
    setAssignment(null);
    setSelection({ index, error });
  };

  const close = (removing = false) => {
    const target = removing
      ? rules.length > 1
        ? list.current
        : addButton.current
      : list.current?.querySelector<HTMLButtonElement>('[aria-pressed="true"]');
    target?.focus();
    setAssignment(null);
    setSelection(null);
  };

  const replaceScope = (
    index: number,
    scope: "ClientIds" | "ArrInstances",
    ids: string[],
  ) => {
    onReplace(
      rules.map((rule, position) => {
        if (position !== index) return rule;
        const settings = { ...rule, [scope]: ids };
        delete settings[scope === "ClientIds" ? "ArrInstances" : "ClientIds"];
        return settings;
      }),
    );
  };

  return (
    <section
      className="config-workspace__layout config-instance-layout"
      aria-label="Tracker rules"
    >
      <section className="config-arr-group">
        <header className="config-instance-header">
          <h3>
            Tracker rules <span>{rules.length}</span>
          </h3>
        </header>
        {rules.length === 0 && (
          <p className="hint">No tracker rules configured.</p>
        )}
        <div
          className="config-arr-list"
          ref={list}
          tabIndex={-1}
          aria-label="Configured trackers"
          role="group"
        >
          {rules.map((item, position) => {
            const name = String(item.Name || `Tracker ${position + 1}`);
            const scope = trackerScope(item);
            const active = index === position;
            return (
              <button
                key={position}
                type="button"
                className={`config-arr-row config-tracker-row${active ? " active" : ""}`}
                aria-pressed={active}
                onClick={() => select(position)}
              >
                <span className="config-arr-name">{name}</span>
                <span className="config-arr-cell">
                  <small>
                    {scope.key === "ClientIds"
                      ? "Torrent clients"
                      : "Arr integrations"}
                  </small>
                  <span>{scope.ids.join(", ") || "None selected"}</span>
                </span>
              </button>
            );
          })}
        </div>
        <div className="config-actions">
          <button
            ref={addButton}
            className="btn"
            type="button"
            onClick={() => {
              onReplace([
                ...rules,
                { ...createDefaultTrackerEntry(), ClientIds: [] },
              ]);
              select(rules.length);
            }}
          >
            Add Tracker
          </button>
          <button
            className="btn primary"
            type="button"
            onClick={() =>
              onSave((error) => {
                const position = Number(error.path[1]);
                if (error.path[0] === "Trackers" && rules[position]) {
                  select(position, error);
                }
              })
            }
            disabled={!dirty || saving}
          >
            Save Trackers
          </button>
        </div>
      </section>
      {rule && (
        <div className="config-workspace__editor" ref={editor} tabIndex={-1}>
          <TrackerCard
            key={index}
            title={String(rule.Name || `Tracker ${index + 1}`)}
            onClose={() => close()}
            fields={trackerRuleFields(rule)}
            state={rule}
            basePath={["Trackers", String(index)]}
            onChange={onChange}
            onDelete={() => {
              close(true);
              onReplace(rules.filter((_, position) => position !== index));
            }}
          >
            <ReactMarkdown>{getTrackerSummary(rule)}</ReactMarkdown>
            <div
              className="tracker-assignments"
              data-field-path={`Trackers.${index}.${key}`}
            >
              <div>
                <strong>{scopeLabel}</strong>
                <div>{ids.join(", ") || "None selected"}</div>
              </div>
              <button
                type="button"
                className="btn"
                aria-haspopup="dialog"
                onClick={() =>
                  setAssignment({
                    key,
                    ClientIds: key === "ClientIds" ? ids : [],
                    ArrInstances: key === "ArrInstances" ? ids : [],
                  })
                }
              >
                Edit assignments
              </button>
            </div>
            {assignment && (
              <ShellDialog
                title="Tracker assignments"
                className="tracker-assignment-dialog"
                onClose={() => setAssignment(null)}
              >
                <div
                  role="group"
                  aria-label="Assignment scope"
                  className="tracker-assignment-scopes"
                >
                  {(["ClientIds", "ArrInstances"] as const).map((scope) => (
                    <button
                      key={scope}
                      className={`btn${assignment.key === scope ? " primary" : ""}`}
                      type="button"
                      aria-pressed={assignment.key === scope}
                      onClick={() =>
                        setAssignment({ ...assignment, key: scope })
                      }
                    >
                      {scope === "ClientIds"
                        ? "Torrent clients"
                        : "Arr integrations"}
                    </button>
                  ))}
                </div>
                {assignment.key === "ArrInstances" && (
                  <p className="hint">
                    Each integration uses its own torrent-client scope.
                  </p>
                )}
                <fieldset
                  className="tracker-scope-options"
                  aria-label="Instances"
                >
                  {(assignment.key === "ClientIds"
                    ? clientIds
                    : integrationIds
                  ).map((id) => {
                    const scope = assignment.key;
                    const selected = assignment[scope].some(
                      (value) => value.toLowerCase() === id.toLowerCase(),
                    );
                    return (
                      <label key={id}>
                        <input
                          type="checkbox"
                          checked={selected}
                          onChange={() =>
                            setAssignment({
                              ...assignment,
                              [scope]: selected
                                ? assignment[scope].filter(
                                    (value) =>
                                      value.toLowerCase() !== id.toLowerCase(),
                                  )
                                : [...assignment[scope], id],
                            })
                          }
                        />
                        {id}
                      </label>
                    );
                  })}
                  {(assignment.key === "ClientIds" ? clientIds : integrationIds)
                    .length === 0 && (
                    <p className="hint">No instances configured.</p>
                  )}
                </fieldset>
                <div className="config-actions">
                  <button
                    className="btn"
                    type="button"
                    onClick={() => setAssignment(null)}
                  >
                    Cancel
                  </button>
                  <button
                    className="btn primary"
                    type="button"
                    disabled={assignment[assignment.key].length === 0}
                    onClick={() => {
                      replaceScope(
                        index,
                        assignment.key,
                        assignment[assignment.key],
                      );
                      setAssignment(null);
                    }}
                  >
                    Apply
                  </button>
                </div>
              </ShellDialog>
            )}
            <label className="tracker-protection-toggle">
              <input
                type="checkbox"
                checked={Boolean(rule.Protection)}
                onChange={(event) =>
                  onChange(
                    ["Trackers", String(index), "Protection"],
                    {} as FieldDefinition,
                    event.target.checked
                      ? getSchemaDefaults("TrackerProtection")
                      : null,
                  )
                }
              />
              Use observation-based protection
            </label>
          </TrackerCard>
        </div>
      )}
    </section>
  );
}
