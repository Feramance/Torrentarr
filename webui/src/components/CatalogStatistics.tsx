import { useEffect, useState } from "react";
import {
  getCatalogStatistics,
  type CatalogStatistics as Statistics,
} from "../api/client";
import { useWebUI } from "../context/WebUIContext";
import { BreakdownBar } from "./Statistics";

export function CatalogStatistics({
  kind,
  instance,
  active,
}: {
  kind: string;
  instance?: string;
  active: boolean;
}) {
  const { liveArr } = useWebUI();
  const key = `${kind}:${instance ?? "aggregate"}`;
  const [result, setResult] = useState<{
    key: string;
    data: Statistics;
  } | null>(null);
  const [failedKey, setFailedKey] = useState("");
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    let busy = false;
    const load = async () => {
      if (busy || document.hidden) return;
      busy = true;
      try {
        const data = await getCatalogStatistics(kind, instance);
        if (!cancelled) {
          setResult({ key, data });
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
  }, [kind, instance, active, liveArr, key]);
  const data = result?.key === key ? result.data : null;
  if (!data)
    return (
      <p className="hint">
        {failedKey === key
          ? "Catalog statistics unavailable."
          : "Loading catalog statistics…"}
      </p>
    );
  return (
    <section className="catalog-statistics" aria-label="Catalog availability">
      <div className="catalog-statistics__heading">
        <strong>
          {data.available.toLocaleString()} / {data.monitored.toLocaleString()}{" "}
          monitored {data.unit} available
        </strong>
        <span className="hint">
          {data.total.toLocaleString()} {data.unit} in catalog · full selected
          library
        </span>
      </div>
      <BreakdownBar
        label={`Availability of monitored ${data.unit}`}
        values={[
          { label: "Available", value: data.available, tone: "teal" },
          { label: "Missing", value: data.missing, tone: "muted" },
        ]}
      />
      {failedKey === key && (
        <p className="hint" role="status">
          Refresh unavailable; showing the last received statistics.
        </p>
      )}
    </section>
  );
}
