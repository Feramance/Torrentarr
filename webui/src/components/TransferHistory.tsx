import { useChartInspection } from "../hooks/useChartInspection";
import { useEffect, useState } from "react";
import { getTransferHistory } from "../api/client";
import type {
  TransferBucket,
  TransferHistoryResponse,
  TransferRange,
} from "../api/types";

import {
  rateLabel,
  TRANSFER_LEFT,
  TRANSFER_RIGHT,
  transferPath,
} from "../utils/transferChart";

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
    const bucketMs = { "1h": 1, "24h": 5, "7d": 30 }[range] * 60_000;
    let timer: number;
    const schedule = (delay = bucketMs - (Date.now() % bucketMs)) => {
      timer = window.setTimeout(() => {
        if (busy) {
          schedule(250);
          return;
        }
        void load();
        schedule();
      }, delay);
    };
    void load();
    schedule();
    window.addEventListener("torrentarr:statistics-refresh", load);
    document.addEventListener("visibilitychange", load);
    return () => {
      cancelled = true;
      window.removeEventListener("torrentarr:statistics-refresh", load);
      window.clearTimeout(timer);
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
        const inspected =
          inspection.value?.instance === series.instance
            ? inspection.value.point
            : null;
        const shown = inspected ?? latest;
        const shownComplete =
          shown != null && shown.samples === shown.expectedSamples;
        const chartWidth = TRANSFER_RIGHT - TRANSFER_LEFT;
        const xAt = (index: number) =>
          TRANSFER_LEFT +
          (index / Math.max(1, series.points.length - 1)) * chartWidth;
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
                  onPointerMove={(event) => {
                    if (event.pointerType === "touch") return;
                    const rect = event.currentTarget.getBoundingClientRect();
                    if (!rect.width) return;
                    const index = Math.round(
                      Math.max(
                        0,
                        Math.min(
                          1,
                          (((event.clientX - rect.left) / rect.width) * 800 -
                            TRANSFER_LEFT) /
                            chartWidth,
                        ),
                      ) *
                        (series.points.length - 1),
                    );
                    inspection.inspect({
                      instance: series.instance,
                      point: series.points[index],
                    });
                  }}
                  viewBox="0 0 800 155"
                  role="img"
                  aria-label={`${series.instance} download and upload rates over ${range}; missing or incomplete intervals appear as gaps`}
                >
                  {[20, 75, 130].map((y) => (
                    <g key={y}>
                      <line
                        x1={TRANSFER_LEFT}
                        x2={TRANSFER_RIGHT}
                        y1={y}
                        y2={y}
                        className="chart-grid"
                      />
                      <text x={TRANSFER_LEFT - 6} y={y + 3} textAnchor="end">
                        {rateLabel(maximum - ((y - 20) / 110) * maximum)}
                      </text>
                    </g>
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
                          cx={xAt(index)}
                          cy={130 - (point.downloadRate / maximum) * 110}
                          r="1.5"
                          fill="var(--chart-download)"
                          {...inspection.bind({
                            instance: series.instance,
                            point,
                          })}
                          onPointerEnter={undefined}
                          onPointerLeave={undefined}
                          tabIndex={0}
                          role="img"
                          aria-label={`${series.instance} · ${new Date(point.timestamp).toLocaleString()} · Download: ${rateLabel(point.downloadRate ?? 0)}`}
                        >
                          <title>{`${new Date(point.timestamp).toLocaleString()} · Download: ${rateLabel(point.downloadRate ?? 0)}`}</title>
                        </circle>
                        <circle
                          cx={xAt(index)}
                          cy={130 - ((point.uploadRate ?? 0) / maximum) * 110}
                          r="1.5"
                          fill="var(--chart-upload)"
                          {...inspection.bind({
                            instance: series.instance,
                            point,
                          })}
                          onPointerEnter={undefined}
                          onPointerLeave={undefined}
                          tabIndex={0}
                          role="img"
                          aria-label={`${series.instance} · ${new Date(point.timestamp).toLocaleString()} · Upload: ${rateLabel(point.uploadRate ?? 0)}`}
                        >
                          <title>{`${new Date(point.timestamp).toLocaleString()} · Upload: ${rateLabel(point.uploadRate ?? 0)}`}</title>
                        </circle>
                      </g>
                    ) : null,
                  )}
                  <text x={TRANSFER_LEFT} y="150">
                    {new Date(series.points[0]!.timestamp).toLocaleString()}
                  </text>
                  <text x={TRANSFER_RIGHT} y="150" textAnchor="end">
                    {new Date(series.points.at(-1)!.timestamp).toLocaleString()}
                  </text>
                </svg>
                <div className="breakdown__legend">
                  <span>
                    <i className="stat-tone-teal" />
                    Download{" "}
                    {shown && shownComplete
                      ? rateLabel(shown.downloadRate ?? 0)
                      : "—"}
                  </span>
                  <span>
                    <i className="stat-tone-orange" />
                    Upload{" "}
                    {shown && shownComplete
                      ? rateLabel(shown.uploadRate ?? 0)
                      : "—"}
                  </span>
                  <span
                    className="hint"
                    role={inspected ? "status" : undefined}
                    aria-label={
                      inspected
                        ? shownComplete
                          ? `${new Date(inspected.timestamp).toLocaleString()} · Download ${rateLabel(inspected.downloadRate!)} · Upload ${rateLabel(inspected.uploadRate ?? 0)}`
                          : `${new Date(inspected.timestamp).toLocaleString()} · No complete sample`
                        : undefined
                    }
                  >
                    {inspected
                      ? `Inspecting · ${new Date(inspected.timestamp).toLocaleString()}${shownComplete ? "" : " · no sample"}`
                      : latest
                        ? `Latest complete interval · ${new Date(latest.timestamp).toLocaleString()}`
                        : "Latest complete interval unavailable"}
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
