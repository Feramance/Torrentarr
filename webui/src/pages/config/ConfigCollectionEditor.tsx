import { useId, useLayoutEffect, useRef, useState } from "react";
import type { ConfigDocument } from "../../api/types";
import type { FieldDefinition, ValidationError } from "./configTypes";
import { ConfigFieldSearch } from "./ConfigFieldSearch";
import { FieldGroup } from "./configFieldComponents";

const summaries: Record<
  string,
  [label: string, keys: string[], separator: string]
> = {
  SharedFileFilters: ["shared filter", ["Name", "Source"], " · "],
  PathMappings: ["path mapping", ["ClientPath", "LocalPath"], " → "],
  FreeSpace: ["free-space monitor", ["Path", "Enabled", "Limit"], " · "],
  CategoryMappings: ["category mapping", ["ClientPath", "Category"], " → "],
  Categories: ["category override", ["Name"], " · "],
  CategoryTransitions: ["category transition", ["From", "To"], " → "],
  TrackerAssignments: [
    "tracker assignment",
    ["TrackerContains", "Category"],
    " · ",
  ],
};

export function ConfigCollectionEditor({
  field,
  value,
  dataPath,
  validationErrors,
  onChange,
}: {
  field: FieldDefinition;
  value: unknown;
  dataPath: string;
  validationErrors: ValidationError[];
  onChange: (value: ConfigDocument[]) => void;
}) {
  const isMapping = field.path?.at(-1) === "PathMappings";
  const kind = field.path?.at(-1) ?? "";
  const isMonitor = kind === "FreeSpace";
  const [itemLabel, summaryKeys, separator] = summaries[kind] ?? [
    "entry",
    [],
    " · ",
  ];
  const rows = (Array.isArray(value) ? value : []) as ConfigDocument[];
  const listName = useId();
  const addButton = useRef<HTMLButtonElement>(null);
  const newRow = useRef<HTMLDetailsElement>(null);
  const [adding, setAdding] = useState<number | null>(null);
  const [touchedPaths, setTouchedPaths] = useState<string[]>([]);

  useLayoutEffect(() => {
    const row = newRow.current;
    if (!row || adding === null) return;
    row.open = true;
    row.querySelector<HTMLElement>("input, select, textarea")?.focus();
  }, [adding]);

  return (
    <section
      className="config-section field--full-width collection-editor"
      aria-label={field.label}
      data-field-path={dataPath}
    >
      <h4 className="config-section__heading" tabIndex={-1}>
        {field.label}
      </h4>
      <div className="config-section__body">
        {field.description && (
          <p className="field-description">{field.description}</p>
        )}
        {isMapping && (
          <p className="field-description">
            Example: /downloads → /data/downloads.
          </p>
        )}
        {!rows.length && (
          <p>
            {isMapping
              ? "No path mappings."
              : isMonitor
                ? "No free-space monitors. Add a local path to enable space protection."
                : `No ${field.label.toLowerCase()}.`}
          </p>
        )}
        {rows.map((row, index) => {
          const path = `${dataPath}.${index}`;
          const rowErrors = validationErrors.filter(({ path: errorPath }) => {
            const key = errorPath.join(".");
            return key === path || key.startsWith(`${path}.`);
          });
          const errors =
            adding === index
              ? rowErrors.filter(({ path }) =>
                  touchedPaths.includes(path.join(".")),
                )
              : rowErrors;
          const label = `${itemLabel[0]!.toUpperCase() + itemLabel.slice(1)} ${index + 1}`;
          const summary = summaryKeys
            .map((key) => {
              const value = row?.[key];
              if (key === "Enabled")
                return value === false ? "Disabled" : "Enabled";
              return typeof value === "string" || typeof value === "number"
                ? String(value)
                : "";
            })
            .filter(Boolean)
            .join(separator);
          return (
            <details
              key={index}
              name={listName}
              ref={index === adding ? newRow : undefined}
              className="collection-row"
              data-field-path={path}
              onToggle={(event) => {
                if (adding === index && !event.currentTarget.open)
                  setAdding(null);
              }}
              data-monitor-path={
                isMonitor ? String(row?.Path ?? "") : undefined
              }
              role="group"
              aria-label={label}
            >
              <summary>
                <span>{summary || label}</span>
                {rowErrors.length > 0 && (
                  <span className="collection-row__invalid">
                    Needs attention
                  </span>
                )}
              </summary>
              <div
                className="collection-row__body"
                onBlur={(event) => {
                  const fieldPath =
                    event.target.closest<HTMLElement>("[data-field-path]")
                      ?.dataset.fieldPath;
                  if (adding === index && fieldPath && fieldPath !== path) {
                    setTouchedPaths((paths) => [...paths, fieldPath]);
                  }
                }}
              >
                {errors
                  .filter((error) => error.path.join(".") === path)
                  .map((error) => (
                    <p key={error.message} className="field-error">
                      {error.message}
                    </p>
                  ))}
                <ConfigFieldSearch.Provider value="">
                  <FieldGroup
                    title={null}
                    fields={(field.itemFields ?? []).map((item) => ({
                      ...item,
                      description: "",
                      applyLive: false,
                      requiresRestart: false,
                    }))}
                    state={row ?? {}}
                    sectionKey={path}
                    basePath={[]}
                    validationErrors={errors}
                    onChange={(changed, def, next) =>
                      onChange(
                        rows.map((entry, i) =>
                          i === index
                            ? {
                                ...entry,
                                [changed.at(-1)!]:
                                  kind === "Categories" && next === ""
                                    ? null
                                    : def.type === "number" &&
                                        next !== "" &&
                                        Number.isFinite(Number(next))
                                      ? Number(next)
                                      : next,
                              }
                            : entry,
                        ),
                      )
                    }
                  />
                </ConfigFieldSearch.Provider>
                <div className="config-actions">
                  <button
                    className="btn"
                    type="button"
                    onClick={(event) => {
                      const details = event.currentTarget.closest("details")!;
                      details.open = false;
                      details.querySelector("summary")?.focus();
                      setAdding(null);
                    }}
                  >
                    Done
                  </button>
                  <button
                    className="btn danger"
                    aria-label={`Remove ${itemLabel}`}
                    type="button"
                    onClick={(event) => {
                      event.currentTarget.closest("details")!.open = false;
                      setAdding(null);
                      onChange(rows.filter((_, i) => i !== index));
                      addButton.current?.focus();
                    }}
                  >
                    Remove
                  </button>
                </div>
              </div>
            </details>
          );
        })}
        <button
          ref={addButton}
          className="btn"
          type="button"
          onClick={() => {
            setAdding(rows.length);
            setTouchedPaths([]);
            onChange([
              ...rows,
              Object.fromEntries(
                (field.itemFields ?? []).map((f) => [
                  f.path![0],
                  structuredClone(f.default),
                ]),
              ),
            ]);
          }}
        >
          Add {itemLabel}
        </button>
      </div>
    </section>
  );
}
