import { useEffect, useState } from "react";
import { getArrActivity } from "../api/client";
import type {
  ArrActivityHistory,
  ArrActivitySeries,
  TransferRange,
} from "../api/types";
import { useWebUI } from "../context/WebUIContext";
import {
  activityColor,
  activityDash,
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
  inspect,
}: {
  series: ArrActivitySeries[];
  metric: ActivityMetric;
  title: string;
  grouping: "type" | "worker";
  index: number;
  inspect: (index: number) => void;
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
            onClick={(event) => {
              const rect = event.currentTarget.getBoundingClientRect();
              inspect(
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
              {maximum.toLocaleString(undefined, { maximumFractionDigits: 1 })}
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
              <g key={s.instance} style={{ color: activityColor(s, grouping) }}>
                <path
                  d={activityPath(s.points, metric, maximum)}
                  fill="none"
                  stroke="currentColor"
                  strokeWidth="2"
                  vectorEffect="non-scaling-stroke"
                  strokeDasharray={activityDash(s, grouping)}
                />
                {s.points.map((p, i) =>
                  p[metric] != null &&
                  activityCoverage(p, metric) === p.expectedSamples &&
                  (i === 0 ||
                    i === s.points.length - 1 ||
                    activityCoverage(s.points[i - 1]!, metric) !==
                      p.expectedSamples ||
                    activityCoverage(s.points[i + 1]!, metric) !==
                      p.expectedSamples) ? (
                    <circle
                      key={p.timestamp}
                      cx={24 + (i / Math.max(1, count - 1)) * 752}
                      cy={142 - (p[metric]! / maximum) * 112}
                      r="2"
                      fill="currentColor"
                    />
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
                    <path
                      d="M0 4H24"
                      stroke={activityColor(s, grouping)}
                      strokeWidth="2"
                      strokeDasharray={activityDash(s, grouping)}
                    />
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
  const { liveArr } = useWebUI();
  const [range, setRange] = useState<TransferRange>("24h");
  const [grouping, setGrouping] = useState<"type" | "worker">("type");
  const [metric, setMetric] = useState<
    "searchesCompleted" | "importsConfirmed"
  >("searchesCompleted");
  const [hidden, setHidden] = useState<Set<string>>(() => new Set());
  const [cursor, setCursor] = useState<number | null>(null);
  const [result, setResult] = useState<{
    range: TransferRange;
    data: ArrActivityHistory;
  } | null>(null);
  const [failed, setFailed] = useState<TransferRange | null>(null);
  const [dataOpen, setDataOpen] = useState(false);
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
    const timer = liveArr
      ? window.setInterval(() => void load(), 60000)
      : undefined;
    return () => {
      cancelled = true;
      window.clearInterval(timer);
      window.removeEventListener("torrentarr:statistics-refresh", load);
    };
  }, [range, active, liveArr]);
  const data = result?.range === range ? result.data : null;
  const series = groupActivity(data?.series ?? [], grouping);
  const shown = series.filter((s) => !hidden.has(`${grouping}:${s.instance}`));
  const count = series[0]?.points.length ?? 0;
  const index = Math.min(cursor ?? count - 1, Math.max(0, count - 1));
  const time = series[0]?.points[index]?.timestamp;
  const throughput =
    metric === "searchesCompleted"
      ? "Searches completed"
      : "Torrent imports confirmed";
  return (
    <section className="arr-activity" aria-label="Arr activity history">
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
                  setCursor(null);
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
                    <path
                      d="M0 4H24"
                      stroke={activityColor(s, grouping)}
                      strokeWidth="2"
                      strokeDasharray={activityDash(s, grouping)}
                    />
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
              inspect={setCursor}
            />
            <ActivityPlot
              series={shown}
              title={`${throughput} · per ${data!.bucketMinutes} min`}
              metric={metric}
              grouping={grouping}
              index={index}
              inspect={setCursor}
            />
          </div>
          <label className="activity-inspector">
            Inspect interval {time ? new Date(time).toLocaleString() : ""}
            <input
              type="range"
              min="0"
              max={Math.max(0, count - 1)}
              value={index}
              onChange={(e) => setCursor(Number(e.target.value))}
              aria-label="Inspect activity time"
            />
          </label>
          <p className="hint">
            7 days retained. Queue values are interval averages; completions are
            counted when confirmed. Partial worker coverage is labeled and
            breaks the line.
          </p>
          <details
            className="activity-data"
            onToggle={(e) => setDataOpen(e.currentTarget.open)}
          >
            <summary>View activity data</summary>
            {dataOpen && (
              <div className="table-wrap">
                <table>
                  <caption>Observed activity and coverage per interval</caption>
                  <thead>
                    <tr>
                      <th>Time</th>
                      <th>{grouping === "type" ? "Arr type" : "Worker"}</th>
                      <th>Queue depth</th>
                      <th>Searches completed</th>
                      <th>Imports confirmed</th>
                      <th>Queue coverage</th>
                      <th>Completion coverage</th>
                    </tr>
                  </thead>
                  <tbody>
                    {shown.flatMap((s) =>
                      s.points.map((p) => (
                        <tr key={`${s.instance}:${p.timestamp}`}>
                          <td>{new Date(p.timestamp).toLocaleString()}</td>
                          <td>{s.instance}</td>
                          <td>{activityValue(p, "queueDepth")}</td>
                          <td>{activityValue(p, "searchesCompleted")}</td>
                          <td>{activityValue(p, "importsConfirmed")}</td>
                          <td>
                            {p.queueSamples}/{p.expectedSamples}
                          </td>
                          <td>
                            {p.completionSamples}/{p.expectedSamples}
                          </td>
                        </tr>
                      )),
                    )}
                  </tbody>
                </table>
              </div>
            )}
          </details>
        </>
      )}
    </section>
  );
}
