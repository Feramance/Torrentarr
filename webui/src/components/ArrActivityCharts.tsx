import { useChartInspection } from "../hooks/useChartInspection";
import { useEffect, useState } from "react";
import { getArrActivity } from "../api/client";
import type {
  ArrActivityHistory,
  ArrActivitySeries,
  TransferRange,
} from "../api/types";
import { STATISTICS_UPDATED } from "../hooks/useStatisticsUpdates";
import {
  activityColor,
  activityCoverage,
  activityPath,
  activityValue,
  groupActivity,
  type ActivityMetric,
} from "../utils/arrActivityChart";

function ActivityPlot({
  series,
  metric,
  title,
  grouping,
  index,
  inspection,
  colorOf,
}: {
  series: ArrActivitySeries[];
  metric: ActivityMetric;
  title: string;
  grouping: "type" | "worker";
  index: number;
  inspection: ReturnType<typeof useChartInspection<number>>;
  colorOf: (series: ArrActivitySeries) => string;
}) {
  const maximum = Math.max(
    1,
    ...series.map((s) =>
      s.points.reduce((max, p) => Math.max(max, p[metric] ?? 0), 0),
    ),
  );
  const count = series[0]?.points.length ?? 0;
  const available = series.some((s) => s.points.some((p) => p[metric] != null));
  return (
    <figure className="arr-activity__plot">
      <figcaption>{title}</figcaption>
      {!available ? (
        <p className="hint">
          {series.length
            ? "Collecting activity history. Unavailable observations appear as gaps."
            : "No series selected."}
        </p>
      ) : (
        <>
          <svg
            viewBox="0 0 800 166"
            role="img"
            aria-label={`${title}, by ${grouping === "type" ? "Arr type" : "worker"}; incomplete coverage appears as gaps`}
            onPointerLeave={inspection.leave}
            onPointerCancel={inspection.clear}
            onPointerMove={(event) => {
              if (event.pointerType === "touch") return;
              const rect = event.currentTarget.getBoundingClientRect();
              if (!rect.width) return;
              inspection.inspect(
                Math.round(
                  Math.max(
                    0,
                    Math.min(
                      1,
                      (((event.clientX - rect.left) / rect.width) * 800 - 24) /
                        752,
                    ),
                  ) *
                    (count - 1),
                ),
              );
            }}
          >
            {[30, 86, 142].map((y) => (
              <line
                key={y}
                x1="24"
                x2="776"
                y1={y}
                y2={y}
                className="chart-grid"
              />
            ))}
            <text x="24" y="16">
              {maximum.toLocaleString(undefined, { maximumFractionDigits: 0 })}
            </text>
            <text x="24" y="160">
              {series[0]?.points[0]
                ? new Date(series[0].points[0].timestamp).toLocaleString()
                : ""}
            </text>
            <text x="776" y="160" textAnchor="end">
              {series[0]?.points.at(-1)
                ? new Date(series[0].points.at(-1)!.timestamp).toLocaleString()
                : ""}
            </text>
            <line
              x1={24 + (index / Math.max(1, count - 1)) * 752}
              x2={24 + (index / Math.max(1, count - 1)) * 752}
              y1="25"
              y2="142"
              className="activity-cursor"
            />
            {series.map((s) => (
              <g key={s.instance} style={{ color: colorOf(s) }}>
                <path
                  d={activityPath(s.points, metric, maximum)}
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  vectorEffect="non-scaling-stroke"
                />
                {s.points.map((p, i) =>
                  p[metric] != null &&
                  activityCoverage(p, metric) === p.expectedSamples ? (
                    <circle
                      key={p.timestamp}
                      cx={24 + (i / Math.max(1, count - 1)) * 752}
                      cy={142 - (p[metric]! / maximum) * 112}
                      r="2"
                      fill="currentColor"
                      tabIndex={0}
                      role="img"
                      aria-label={`${s.instance}: ${new Date(p.timestamp).toLocaleString()}, ${title}, ${activityValue(p, metric)}`}
                      {...inspection.bind(i)}
                    >
                      <title>{`${s.instance} · ${new Date(p.timestamp).toLocaleString()} · ${title}: ${activityValue(p, metric)}`}</title>
                    </circle>
                  ) : null,
                )}
              </g>
            ))}
          </svg>
          <dl className="activity-values">
            {series.map((s) => (
              <div key={s.instance}>
                <dt>
                  <svg width="24" height="8" aria-hidden="true">
                    <path d="M0 4H24" stroke={colorOf(s)} strokeWidth="2" />
                  </svg>
                  {s.instance}
                </dt>
                <dd>{activityValue(s.points[index], metric)}</dd>
              </div>
            ))}
          </dl>
        </>
      )}
    </figure>
  );
}

export function ArrActivityCharts({ active }: { active: boolean }) {
  const [range, setRange] = useState<TransferRange>("24h");
  const [grouping, setGrouping] = useState<"type" | "worker">("type");
  const [metric, setMetric] = useState<
    "searchesCompleted" | "importsConfirmed"
  >("searchesCompleted");
  const [hidden, setHidden] = useState<Set<string>>(() => new Set());
  const inspection = useChartInspection<number>(
    `${range}:${grouping}:${metric}:${[...hidden].join(":")}`,
    active,
  );
  const cursor = inspection.value;
  const [result, setResult] = useState<{
    range: TransferRange;
    data: ArrActivityHistory;
  } | null>(null);
  const [failed, setFailed] = useState<TransferRange | null>(null);
  useEffect(() => {
    if (!active) return;
    let cancelled = false,
      busy = false;
    const load = async () => {
      if (busy || document.hidden) return;
      busy = true;
      try {
        const data = await getArrActivity(range);
        if (!cancelled) {
          setResult({ range, data });
          setFailed(null);
        }
      } catch {
        if (!cancelled) setFailed(range);
      } finally {
        busy = false;
      }
    };
    void load();
    window.addEventListener("torrentarr:statistics-refresh", load);
    window.addEventListener(STATISTICS_UPDATED, load);
    document.addEventListener("visibilitychange", load);
    return () => {
      cancelled = true;
      window.removeEventListener(STATISTICS_UPDATED, load);
      document.removeEventListener("visibilitychange", load);
      window.removeEventListener("torrentarr:statistics-refresh", load);
    };
  }, [range, active]);
  const data = result?.range === range ? result.data : null;
  const series = groupActivity(data?.series ?? [], grouping);
  const shown = series.filter((s) => !hidden.has(`${grouping}:${s.instance}`));
  const workerIndex = new Map(series.map((s, i) => [s.instance, i]));
  const colorOf = (s: ArrActivitySeries) =>
    activityColor(s, grouping, workerIndex.get(s.instance) ?? 0);
  const count = series[0]?.points.length ?? 0;
  const index = Math.min(cursor ?? count - 1, Math.max(0, count - 1));
  const time = series[0]?.points[index]?.timestamp;
  const throughput =
    metric === "searchesCompleted"
      ? "Searches completed"
      : "Torrent imports confirmed";
  return (
    <section
      ref={inspection.ref}
      className="arr-activity"
      aria-label="Arr activity history"
    >
      <div className="arr-activity__heading">
        <h3>Arr activity</h3>
        <div className="arr-activity__controls">
          <label>
            Group by{" "}
            <select
              aria-label="Group activity by"
              value={grouping}
              onChange={(e) => setGrouping(e.target.value as "type" | "worker")}
            >
              <option value="type">Arr type</option>
              <option value="worker">Worker</option>
            </select>
          </label>
          <div className="view-density-toggle" aria-label="Arr activity range">
            {(["1h", "24h", "7d"] as const).map((value) => (
              <button
                type="button"
                key={value}
                aria-pressed={range === value}
                className={range === value ? "active" : ""}
                onClick={() => {
                  setRange(value);
                  inspection.clear();
                }}
              >
                {value}
              </button>
            ))}
          </div>
          <label>
            Completed activity{" "}
            <select
              aria-label="Completed activity metric"
              value={metric}
              onChange={(e) => setMetric(e.target.value as typeof metric)}
            >
              <option value="searchesCompleted">Searches completed</option>
              <option value="importsConfirmed">
                Torrent imports confirmed
              </option>
            </select>
          </label>
        </div>
      </div>
      {failed === range && (
        <p role="status" className="hint">
          Arr activity unavailable.
          {data ? " Showing the last received history." : ""}
        </p>
      )}
      {!data && failed !== range && (
        <p className="hint">Loading Arr activity…</p>
      )}
      {data && !series.length && (
        <p className="hint">
          Collecting activity history. No managed Arr workers yet.
        </p>
      )}
      {series.length > 0 && (
        <>
          <div className="activity-legend" aria-label="Activity series">
            {series.map((s) => {
              const key = `${grouping}:${s.instance}`;
              return (
                <button
                  type="button"
                  key={key}
                  aria-pressed={!hidden.has(key)}
                  onClick={() =>
                    setHidden((previous) => {
                      const next = new Set(previous);
                      if (next.has(key)) next.delete(key);
                      else next.add(key);
                      return next;
                    })
                  }
                >
                  <svg width="24" height="8" aria-hidden="true">
                    <path d="M0 4H24" stroke={colorOf(s)} strokeWidth="2" />
                  </svg>
                  {s.instance}
                </button>
              );
            })}
          </div>
          <div className="arr-activity__graphs">
            <ActivityPlot
              series={shown}
              title="Queue depth · items"
              metric="queueDepth"
              grouping={grouping}
              index={index}
              inspection={inspection}
              colorOf={colorOf}
            />
            <ActivityPlot
              series={shown}
              title={`${throughput} · per ${data!.bucketMinutes === 60 ? "hour" : `${data!.bucketMinutes} min`}`}
              metric={metric}
              grouping={grouping}
              index={index}
              inspection={inspection}
              colorOf={colorOf}
            />
          </div>
          <label className="activity-inspector">
            Inspect interval {time ? new Date(time).toLocaleString() : ""}
            <input
              type="range"
              min="0"
              max={Math.max(0, count - 1)}
              value={index}
              {...inspection.bind(index)}
              onChange={(e) => inspection.inspect(Number(e.target.value))}
              aria-label="Inspect activity time"
            />
          </label>
          <p className="hint">
            7 days retained. Queue values are interval averages rounded to whole
            items; completions are counted when confirmed. Gaps in worker
            coverage break the line.
          </p>
        </>
      )}
    </section>
  );
}
