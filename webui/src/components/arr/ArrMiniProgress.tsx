import { useChartInspection } from "../../hooks/useChartInspection";
import { useId, type JSX } from "react";

export interface ArrMiniProgressProps {
  label: string;
  available: number;
  missing: number;
}

/**
 * Compact segmented bar for browse tiles: monitored scope = available + missing.
 */
export function ArrMiniProgress({
  label,
  available,
  missing,
}: ArrMiniProgressProps): JSX.Element {
  const tooltipId = useId();
  const inspection = useChartInspection<"Available" | "Missing">(label);
  const selected = inspection.value;
  const total = available + missing;
  const availPct = total > 0 ? (available / total) * 100 : 0;
  const missPct = total > 0 ? (missing / total) * 100 : 0;
  const title = `${label}: ${available.toLocaleString()} available, ${missing.toLocaleString()} missing (${total.toLocaleString()} monitored)`;

  return (
    <div ref={inspection.ref} className="arr-mini-progress">
      <div className="arr-mini-progress__label-row">
        <span className="arr-mini-progress__label">{label}</span>
        <span className="arr-mini-progress__nums">
          {available.toLocaleString()} / {total.toLocaleString()}
        </span>
      </div>
      <div
        className="arr-mini-progress__track"
        role="meter"
        aria-valuemin={0}
        aria-valuemax={total}
        aria-valuenow={available}
        aria-label={title}
      >
        {total > 0 ? (
          <>
            <div
              className="arr-mini-progress__seg arr-mini-progress__seg--avail"
              style={{ width: `${availPct}%` }}
              tabIndex={0}
              role="button"
              aria-label={`Available: ${available.toLocaleString()} (${availPct.toFixed(1)}%)`}
              aria-describedby={
                selected === "Available" ? tooltipId : undefined
              }
              {...inspection.bind("Available")}
            />
            <div
              className="arr-mini-progress__seg arr-mini-progress__seg--miss"
              style={{ width: `${missPct}%` }}
              tabIndex={0}
              role="button"
              aria-label={`Missing: ${missing.toLocaleString()} (${missPct.toFixed(1)}%)`}
              aria-describedby={selected === "Missing" ? tooltipId : undefined}
              {...inspection.bind("Missing")}
            />
          </>
        ) : (
          <div className="arr-mini-progress__seg arr-mini-progress__seg--empty" />
        )}
      </div>
      {selected && (
        <div className="stat-tooltip" role="tooltip" id={tooltipId}>
          {label} · {selected}:{" "}
          {(selected === "Available" ? available : missing).toLocaleString()}{" "}
          items ({(selected === "Available" ? availPct : missPct).toFixed(1)}%)
          · {total.toLocaleString()} monitored
        </div>
      )}
    </div>
  );
}
