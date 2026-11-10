import { useChartInspection } from "../hooks/useChartInspection";
import { useEffect, useState } from "react";
import { getTransferHistory } from "../api/client";
import type {
  TransferBucket,
  TransferHistoryResponse,
  TransferRange,
} from "../api/types";
import { STATISTICS_UPDATED } from "../hooks/useStatisticsUpdates";

import { rateLabel, transferPath } from "../utils/transferChart";

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
  const key = `${instance ?? "all"}:${range}`;
  const inspection = useChartInspection<{
    instance: string;
    point: TransferBucket;
  }>(key, active);
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
    window.addEventListener(STATISTICS_UPDATED, load);
    document.addEventListener("visibilitychange", load);
    return () => {
      cancelled = true;
      window.removeEventListener("torrentarr:statistics-refresh", load);
      window.removeEventListener(STATISTICS_UPDATED, load);
      document.removeEventListener("visibilitychange", load);
    };
  }, [instance, range, active, key]);
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
    <section
      ref={inspection.ref}
      className="transfer-history"
      aria-label="Transfer history"
    >
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
                  onPointerLeave={inspection.leave}
                  onPointerCancel={inspection.clear}
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
                          fill="var(--chart-download)"
                          {...inspection.bind({
                            instance: series.instance,
                            point,
                          })}
                          tabIndex={0}
                          role="img"
                          aria-label={`${series.instance} · ${new Date(point.timestamp).toLocaleString()} · Download: ${rateLabel(point.downloadRate ?? 0)}`}
                        >
                          <title>{`${new Date(point.timestamp).toLocaleString()} · Download: ${rateLabel(point.downloadRate ?? 0)}`}</title>
                        </circle>
                        <circle
                          cx={
                            20 +
                            (index / Math.max(1, series.points.length - 1)) *
                              760
                          }
                          cy={130 - ((point.uploadRate ?? 0) / maximum) * 110}
                          r="1.5"
                          fill="var(--chart-upload)"
                          {...inspection.bind({
                            instance: series.instance,
                            point,
                          })}
                          tabIndex={0}
                          role="img"
                          aria-label={`${series.instance} · ${new Date(point.timestamp).toLocaleString()} · Upload: ${rateLabel(point.uploadRate ?? 0)}`}
                        >
                          <title>{`${new Date(point.timestamp).toLocaleString()} · Upload: ${rateLabel(point.uploadRate ?? 0)}`}</title>
                        </circle>
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
                {inspection.value?.instance === series.instance && (
                  <p role="status" className="hint">
                    {new Date(
                      inspection.value.point.timestamp,
                    ).toLocaleString()}{" "}
                    · Download{" "}
                    {rateLabel(inspection.value.point.downloadRate ?? 0)} ·
                    Upload {rateLabel(inspection.value.point.uploadRate ?? 0)}
                  </p>
                )}
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
          </figure>
        );
      })}
    </section>
  );
}
