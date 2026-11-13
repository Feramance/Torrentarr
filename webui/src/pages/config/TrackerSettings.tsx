import { useState } from "react";
import ReactMarkdown from "react-markdown";
import type { ConfigDocument } from "../../api/types";
import { getTrackerSummary } from "../../config/torrentHandlingSummary";
import { TrackerCard } from "./configFieldComponents";
import { getSchemaDefaults } from "./configFields";
import { createDefaultTrackerEntry } from "./configDocumentUtils";
import { trackerRuleFields, trackerScope } from "./trackerRuleFields";
import type { FieldDefinition } from "./configTypes";

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
  onSave: () => void;
  dirty: boolean;
  saving: boolean;
}) {
  const [open, setOpen] = useState<number | null>(null);
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
    <section className="config-section" aria-label="Tracker rules">
      {rules.length === 0 && (
        <p className="hint">No tracker rules configured.</p>
      )}
      <div className="tracker-grid">
        {rules.map((rule, index) => {
          const { key, ids } = trackerScope(rule);
          const scopeLabel =
            key === "ClientIds" ? "Torrent clients" : "Arr integrations";
          const options = key === "ClientIds" ? clientIds : integrationIds;
          return (
            <TrackerCard
              key={index}
              fields={trackerRuleFields(rule)}
              state={rule}
              basePath={["Trackers", String(index)]}
              onChange={onChange}
              onDelete={() => {
                setOpen(null);
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
                    setOpen(index);
                  }}
                >
                  <option value="ClientIds">Torrent clients</option>
                  <option value="ArrInstances">Arr integrations</option>
                </select>
                <div
                  data-field-path={`Trackers.${index}.${key}`}
                  onBlur={(event) => {
                    if (!event.currentTarget.contains(event.relatedTarget))
                      setOpen((current) =>
                        current === index ? null : current,
                      );
                  }}
                  onKeyDown={(event) => {
                    if (event.key === "Escape" && open === index) {
                      event.stopPropagation();
                      setOpen(null);
                      event.currentTarget.querySelector("button")?.focus();
                    }
                  }}
                >
                  <button
                    className="btn"
                    type="button"
                    aria-expanded={open === index}
                    aria-controls={`tracker-scope-options-${index}`}
                    onClick={() => setOpen(open === index ? null : index)}
                  >
                    {ids.length
                      ? ids.join(", ")
                      : `Select ${scopeLabel.toLowerCase()}`}
                  </button>
                  <fieldset
                    className="tracker-scope-options"
                    id={`tracker-scope-options-${index}`}
                    hidden={open !== index}
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
                                        value.toLowerCase() !==
                                        id.toLowerCase(),
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
          );
        })}
      </div>
      <div className="config-actions">
        <button
          className="btn"
          type="button"
          onClick={() =>
            onReplace([
              ...rules,
              { ...createDefaultTrackerEntry(), ClientIds: [] },
            ])
          }
        >
          Add Tracker
        </button>
        <button
          className="btn primary"
          type="button"
          onClick={onSave}
          disabled={!dirty || saving}
        >
          Save Trackers
        </button>
      </div>
    </section>
  );
}
