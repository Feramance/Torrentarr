import { useEffect, useRef, useState } from "react";
import ReactMarkdown from "react-markdown";
import type { ConfigDocument } from "../../api/types";
import { getTrackerSummary } from "../../config/torrentHandlingSummary";
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
  const [scopeOpen, setScopeOpen] = useState(false);
  const editor = useRef<HTMLDivElement>(null);
  const list = useRef<HTMLDivElement>(null);
  const addButton = useRef<HTMLButtonElement>(null);
  const index = selection?.index ?? -1;
  const rule = rules[index];
  const { key, ids } = trackerScope(rule ?? {});
  const scopeLabel =
    key === "ClientIds" ? "Torrent clients" : "Arr integrations";
  const options = key === "ClientIds" ? clientIds : integrationIds;

  useEffect(() => {
    const panel = editor.current;
    if (!selection || !panel) return;
    panel.focus();
    if (selection.error) focusFirstValidationError([selection.error]);
  }, [selection]);

  const select = (index: number, error?: ValidationError) => {
    setScopeOpen(false);
    setSelection({ index, error });
  };

  const close = (removing = false) => {
    const target = removing
      ? rules.length > 1
        ? list.current
        : addButton.current
      : list.current?.querySelector<HTMLButtonElement>('[aria-pressed="true"]');
    target?.focus();
    setScopeOpen(false);
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
            <div className="field">
              <label htmlFor={`tracker-scope-${index}`}>Applies to</label>
              <select
                id={`tracker-scope-${index}`}
                value={key}
                onChange={(event) => {
                  replaceScope(
                    index,
                    event.target.value === "ArrInstances"
                      ? "ArrInstances"
                      : "ClientIds",
                    [],
                  );
                  setScopeOpen(true);
                }}
              >
                <option value="ClientIds">Torrent clients</option>
                <option value="ArrInstances">Arr integrations</option>
              </select>
              <div
                data-field-path={`Trackers.${index}.${key}`}
                onBlur={(event) => {
                  if (!event.currentTarget.contains(event.relatedTarget))
                    setScopeOpen(false);
                }}
                onKeyDown={(event) => {
                  if (event.key === "Escape" && scopeOpen) {
                    event.stopPropagation();
                    setScopeOpen(false);
                    event.currentTarget.querySelector("button")?.focus();
                  }
                }}
              >
                <button
                  className="btn"
                  type="button"
                  aria-expanded={scopeOpen}
                  aria-controls={`tracker-scope-options-${index}`}
                  onClick={() => setScopeOpen(!scopeOpen)}
                >
                  {ids.length
                    ? ids.join(", ")
                    : `Select ${scopeLabel.toLowerCase()}`}
                </button>
                <fieldset
                  className="tracker-scope-options"
                  id={`tracker-scope-options-${index}`}
                  hidden={!scopeOpen}
                >
                  <legend>{scopeLabel}</legend>
                  {options.length === 0 && (
                    <p className="hint">No instances configured.</p>
                  )}
                  {options.map((id) => {
                    const selected = ids.some(
                      (value) => value.toLowerCase() === id.toLowerCase(),
                    );
                    return (
                      <label key={id}>
                        <input
                          type="checkbox"
                          checked={selected}
                          onChange={() =>
                            replaceScope(
                              index,
                              key,
                              selected
                                ? ids.filter(
                                    (value) =>
                                      value.toLowerCase() !== id.toLowerCase(),
                                  )
                                : [...ids, id],
                            )
                          }
                        />
                        {id}
                      </label>
                    );
                  })}
                </fieldset>
              </div>
              {key === "ArrInstances" && (
                <p className="hint">
                  Each integration uses its own torrent-client scope.
                </p>
              )}
            </div>
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
              Enable tracker protection policy
            </label>
          </TrackerCard>
        </div>
      )}
    </section>
  );
}
