import { useChartInspection } from "../hooks/useChartInspection";
import { type ReactNode } from "react";

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
  unit = "torrents",
  active = true,
}: {
  label: string;
  values: { label: string; value: number; tone: string }[];
  unit?: string;
  active?: boolean;
}) {
  const inspection = useChartInspection<string>(label, active);
  const selected = inspection.value;
  const total = values.reduce((sum, value) => sum + value.value, 0);
  return (
    <div
      ref={inspection.ref}
      className="breakdown"
      role="group"
      aria-label={label}
    >
      <div className="breakdown__bar">
        {values.map((item) => (
          <span
            key={item.label}
            className={`stat-tone-${item.tone}`}
            style={{ flexGrow: item.value, flexBasis: 0 }}
            tabIndex={item.value > 0 ? 0 : undefined}
            role="button"
            aria-label={`${label} · ${item.label}: ${item.value.toLocaleString()} ${unit} (${total ? ((item.value / total) * 100).toFixed(1) : 0}%)`}
            {...inspection.bind(item.label)}
          />
        ))}
      </div>
      {selected && (
        <div className="stat-tooltip" role="tooltip">
          {values
            .filter((item) => item.label === selected)
            .map(
              (item) =>
                `${label} · ${item.label}: ${item.value.toLocaleString()} ${unit} (${total ? ((item.value / total) * 100).toFixed(1) : 0}%)`,
            )}
        </div>
      )}
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
