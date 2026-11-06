import type { ReactNode } from "react";

export function MetricStrip({
  items,
  label,
}: {
  label: string;
  items: { label: string; value: ReactNode }[];
}) {
  return (
    <dl className="metric-strip" aria-label={label}>
      {items.map((item) => (
        <div key={item.label}>
          <dt>{item.label}</dt>
          <dd>{item.value}</dd>
        </div>
      ))}
    </dl>
  );
}

export function BreakdownBar({
  label,
  values,
}: {
  label: string;
  values: { label: string; value: number; tone: string }[];
}) {
  const total = values.reduce((sum, value) => sum + value.value, 0);
  return (
    <div className="breakdown" role="group" aria-label={label}>
      <div className="breakdown__bar" aria-hidden="true">
        {values.map((item) => (
          <span
            key={item.label}
            className={`stat-tone-${item.tone}`}
            style={{ flexGrow: item.value, flexBasis: 0 }}
          />
        ))}
      </div>
      <div className="breakdown__legend">
        {values.map((item) => (
          <span key={item.label}>
            <i className={`stat-tone-${item.tone}`} aria-hidden="true" />
            {item.label} <strong>{item.value.toLocaleString()}</strong>
          </span>
        ))}
        {total === 0 && <span>No data yet</span>}
      </div>
    </div>
  );
}
