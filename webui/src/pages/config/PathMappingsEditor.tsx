import { useEffect, useRef } from "react";
import type { ConfigDocument } from "../../api/types";
import type { FieldDefinition, ValidationError } from "./configTypes";
import { FieldGroup } from "./configFieldComponents";

export function PathMappingsEditor({
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
  const itemLabel =
    (
      {
        PathMappings: "path mapping",
        FreeSpace: "free-space monitor",
        CategoryMappings: "category mapping",
        CategoryTransitions: "category transition",
        TrackerAssignments: "tracker assignment",
      } as Record<string, string>
    )[kind] ?? "entry";
  const rows = (Array.isArray(value) ? value : []) as ConfigDocument[];
  const hasErrors = validationErrors.some(({ path }) => {
    const key = path.join(".");
    return key === dataPath || key.startsWith(`${dataPath}.`);
  });
  const details = useRef<HTMLDetailsElement>(null);
  useEffect(() => {
    if (hasErrors && details.current) details.current.open = true;
  }, [hasErrors]);
  return (
    <details
      className="config-section field--full-width collection-editor"
      ref={details}
      aria-label={field.label}
      data-field-path={dataPath}
    >
      <summary>{field.label}</summary>
      <div className="config-section__body">
        <p className="field-description">{field.description}</p>
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
          return (
            <div
              key={index}
              className={`collection-row ${isMapping || kind === "CategoryMappings" ? "collection-row--mapping" : isMonitor ? "collection-row--monitor" : "collection-row--rules"}`}
              role="group"
              aria-label={`${itemLabel[0]!.toUpperCase() + itemLabel.slice(1)} ${index + 1}`}
            >
              <FieldGroup
                title={null}
                fields={(field.itemFields ?? []).map((item) => ({
                  ...item,
                  description: "",
                  applyLive: false,
                  requiresRestart: false,
                }))}
                state={row}
                sectionKey={path}
                basePath={[]}
                validationErrors={validationErrors}
                onChange={(changed, def, next) =>
                  onChange(
                    rows.map((entry, i) =>
                      i === index
                        ? {
                            ...entry,
                            [changed.at(-1)!]:
                              def.type === "number" &&
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
              <button
                className="btn danger"
                aria-label={`Remove ${itemLabel}`}
                type="button"
                onClick={() => onChange(rows.filter((_, i) => i !== index))}
              >
                Remove
              </button>
            </div>
          );
        })}
        <button
          className="btn"
          type="button"
          onClick={() =>
            onChange([
              ...rows,
              Object.fromEntries(
                (field.itemFields ?? []).map((f) => [
                  f.path![0],
                  structuredClone(f.default),
                ]),
              ),
            ])
          }
        >
          Add {itemLabel}
        </button>
      </div>
    </details>
  );
}
