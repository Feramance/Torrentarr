import { useEffect, useState } from "react";
import { getTransferHistory } from "../api/client";
import type {
  TransferBucket,
  TransferHistoryResponse,
  TransferRange,
} from "../api/types";
import { useWebUI } from "../context/WebUIContext";

import { rateLabel, transferPath } from "../utils/transferChart";

function TransferData({ points }: { points: TransferBucket[] }) {
  const [open, setOpen] = useState(false);
  return (
    <details
      className="transfer-data"
      onToggle={(event) => setOpen(event.currentTarget.open)}
    >
      <summary>View sampled data</summary>
      {open && (
        <div className="table-wrap">
          <table>
            <caption>
              Rates per interval; incomplete intervals are excluded from the
              line graph.
            </caption>
            <thead>
              <tr>
                <th>Interval start</th>
                <th>Download</th>
                <th>Upload</th>
                <th>Minutes sampled</th>
              </tr>
            </thead>
            <tbody>
              {points.map((point) => (
                <tr key={point.timestamp}>
                  <td>{new Date(point.timestamp).toLocaleString()}</td>
                  <td>
                    {point.downloadRate == null
                      ? "Unavailable"
                      : rateLabel(point.downloadRate)}
                  </td>
                  <td>
                    {point.uploadRate == null
                      ? "Unavailable"
                      : rateLabel(point.uploadRate)}
                  </td>
                  <td>
                    {point.samples} / {point.expectedSamples}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </details>
  );
}

export function TransferHistory({
  instance,
  active,
}: {
  instance?: string;
  active: boolean;
}) {
  const [range, setRange] = useState<TransferRange>("24h");
  const [result, setResult] = useState<{
    key: string;
    data: TransferHistoryResponse;
  } | null>(null);
  const [failedKey, setFailedKey] = useState("");
  const { liveArr } = useWebUI();
  const key = `${instance ?? "all"}:${range}`;
  const data = result?.key === key ? result.data : null;
  const error =
    failedKey === key ? "Transfer history unavailable. Try again shortly." : "";
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    let busy = false;
    const load = async () => {
      if (busy || document.hidden) return;
      busy = true;
      try {
        const next = await getTransferHistory(instance, range);
        if (!cancelled) {
          setResult({ key, data: next });
          setFailedKey("");
        }
      } catch {
        if (!cancelled) setFailedKey(key);
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
      window.removeEventListener("torrentarr:statistics-refresh", load);
      window.clearInterval(timer);
    };
  }, [instance, range, active, liveArr, key]);
  const maximum = Math.max(
    1,
    ...(data?.series.flatMap((series) =>
      series.points.flatMap((point) => [
        point.downloadRate ?? 0,
        point.uploadRate ?? 0,
      ]),
    ) ?? []),
  );
  return (
    <section className="transfer-history" aria-label="Transfer history">
      <div className="transfer-history__heading">
        <div>
          <h3>Transfer activity</h3>
          <p className="hint">
            Whole-client traffic · {instance ?? "All enabled clients"} · 7 days
            retained
          </p>
        </div>
        <div className="view-density-toggle" aria-label="History range">
          {(["1h", "24h", "7d"] as const).map((value) => (
            <button
              key={value}
              type="button"
              className={range === value ? "active" : ""}
              aria-pressed={range === value}
              onClick={() => setRange(value)}
            >
              {value}
            </button>
          ))}
        </div>
      </div>
      {error ? (
        <p role="status" className="hint">
          {error}
          {data ? " Showing the last received history." : ""}
        </p>
      ) : null}
      {!data && !error ? (
        <p className="hint">Loading transfer history…</p>
      ) : null}
      {data?.series.length === 0 && <p className="hint">No enabled clients.</p>}
      {data?.series.map((series) => {
        const hasData = series.points.some((point) => point.samples > 0);
        const complete = series.points.filter(
          (point) => point.samples === point.expectedSamples,
        );
        const latest = complete.at(-1);
        const coverage = series.points.reduce(
          (sum, point) => sum + point.samples,
          0,
        );
        const expected = series.points.reduce(
          (sum, point) => sum + point.expectedSamples,
          0,
        );
        return (
          <figure key={series.instance} className="transfer-chart">
            <figcaption>
              <strong>{series.instance}</strong>
              <span className="hint">
                {coverage} / {expected} minutes sampled · gaps indicate missing
                data
              </span>
            </figcaption>
            {hasData ? (
              <>
                <svg
                  viewBox="0 0 800 155"
                  role="img"
                  aria-label={`${series.instance} download and upload rates over ${range}; missing or incomplete intervals appear as gaps`}
                >
                  <text x="20" y="13">
                    {rateLabel(maximum)}
                  </text>
                  {[20, 75, 130].map((y) => (
                    <line
                      key={y}
                      x1="20"
                      x2="780"
                      y1={y}
                      y2={y}
                      className="chart-grid"
                    />
                  ))}
                  <path
                    d={transferPath(series.points, "downloadRate", maximum)}
                    className="chart-download"
                  />
                  <path
                    d={transferPath(series.points, "uploadRate", maximum)}
                    className="chart-upload"
                  />
                  {series.points.map((point, index) =>
                    point.samples === point.expectedSamples &&
                    point.downloadRate != null ? (
                      <g key={point.timestamp}>
                        <circle
                          cx={
                            20 +
                            (index / Math.max(1, series.points.length - 1)) *
                              760
                          }
                          cy={130 - (point.downloadRate / maximum) * 110}
                          r="1.5"
                          fill="var(--accent)"
                        />
                        <circle
                          cx={
                            20 +
                            (index / Math.max(1, series.points.length - 1)) *
                              760
                          }
                          cy={130 - ((point.uploadRate ?? 0) / maximum) * 110}
                          r="1.5"
                          fill="var(--primary)"
                        />
                      </g>
                    ) : null,
                  )}
                  <text x="20" y="150">
                    {new Date(series.points[0]!.timestamp).toLocaleString()}
                  </text>
                  <text x="780" y="150" textAnchor="end">
                    {new Date(series.points.at(-1)!.timestamp).toLocaleString()}
                  </text>
                </svg>
                <div className="breakdown__legend">
                  <span>
                    <i className="stat-tone-teal" />
                    Download {latest ? rateLabel(latest.downloadRate!) : "—"}
                  </span>
                  <span>
                    <i className="stat-tone-orange" />
                    Upload {latest ? rateLabel(latest.uploadRate!) : "—"}
                  </span>
                  <span className="hint">
                    Latest complete interval
                    {latest
                      ? ` · ${new Date(latest.timestamp).toLocaleString()}`
                      : " unavailable"}
                  </span>
                </div>
              </>
            ) : (
              <p className="hint">
                Collecting history. No samples in this period yet.
              </p>
            )}
            {hasData && <TransferData points={series.points} />}
          </figure>
        );
      })}
    </section>
  );
}
