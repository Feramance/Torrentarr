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
  const rows = (Array.isArray(value) ? value : []) as ConfigDocument[];
  return (
    <section
      className="field field--full-width"
      aria-label={field.label}
      data-field-path={dataPath}
    >
      <h3>{field.label}</h3>
      <p className="field-description">{field.description}</p>
      {!rows.length && (
        <p>No path mappings. Example: /downloads → /data/downloads.</p>
      )}
      {rows.map((row, index) => {
        const path = `${dataPath}.${index}`;
        return (
          <div key={index} className="config-section">
            <h4>Path mapping {index + 1}</h4>
            <FieldGroup
              title={null}
              fields={field.itemFields ?? []}
              state={row}
              sectionKey={path}
              basePath={[]}
              validationErrors={validationErrors}
              onChange={(changed, _def, next) =>
                onChange(
                  rows.map((entry, i) =>
                    i === index ? { ...entry, [changed.at(-1)!]: next } : entry,
                  ),
                )
              }
            />
            <p style={{ overflowWrap: "anywhere" }}>
              <code>{`${String(row.ClientPath || "/downloads").replace(/[\\/]+$/, "")}/movie/file.mkv → ${String(row.LocalPath || "/data/downloads").replace(/[\\/]+$/, "")}/movie/file.mkv`}</code>
            </p>
            <button
              className="btn danger"
              type="button"
              onClick={() => onChange(rows.filter((_, i) => i !== index))}
            >
              Remove path mapping
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
        Add path mapping
      </button>
    </section>
  );
}
