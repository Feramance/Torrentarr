import type { ReactNode } from "react";

export function ConfigOverviewCard({
  name,
  subtitle,
  enabled,
  dirty,
  active,
  editLabel,
  onEdit,
  facts,
}: {
  name: string;
  subtitle: string;
  enabled?: boolean;
  dirty?: boolean;
  active: boolean;
  editLabel?: string;
  onEdit: () => void;
  facts: [label: string, value: ReactNode][];
}) {
  return (
    <article
      className={`config-overview-card${active ? " active" : ""}`}
      aria-label={name}
    >
      <header className="config-overview-card__header">
        <div>
          <h3>{name}</h3>
          <div className="config-overview-card__meta">
            <span>{subtitle}</span>
            {enabled !== undefined && (
              <span className={enabled ? "config-overview-enabled" : undefined}>
                {enabled ? "Enabled" : "Disabled"}
              </span>
            )}
            {dirty && <span className="config-overview-dirty">Unsaved</span>}
          </div>
        </div>
        <button
          type="button"
          className="btn small ghost"
          aria-label={editLabel ?? `Edit ${name}`}
          aria-pressed={active}
          onClick={onEdit}
        >
          Edit
        </button>
      </header>
      <dl>
        {facts.map(([label, value]) => (
          <div key={label}>
            <dt>{label}</dt>
            <dd>{value}</dd>
          </div>
        ))}
      </dl>
    </article>
  );
}
