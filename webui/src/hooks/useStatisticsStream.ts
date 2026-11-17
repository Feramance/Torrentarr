import { useEffect } from "react";
import { invalidateGetCache, watchStatistics } from "../api/client";
import { STATISTICS_UPDATED } from "./useStatisticsUpdates";

export function useStatisticsStream(): void {
  useEffect(() => {
    let stopped = false;
    let timer: number | undefined;
    let retry: number | undefined;
    let backoff = 1000;
    const abort = new AbortController();
    const changed = () => {
      backoff = 1000;
      if (timer !== undefined) return;
      timer = window.setTimeout(() => {
        timer = undefined;
        invalidateGetCache([
          "/web/processes",
          "/web/arr",
          "/web/status",
          "/web/statistics",
        ]);
        window.dispatchEvent(new Event(STATISTICS_UPDATED));
      }, 150);
    };
    const connect = async () => {
      try {
        await watchStatistics(changed, abort.signal);
      } catch {
        /* Keep the last observation while reconnecting. */
      }
      if (stopped) return;
      retry = window.setTimeout(() => void connect(), backoff);
      backoff = Math.min(backoff * 2, 30000);
    };
    void connect();
    return () => {
      stopped = true;
      abort.abort();
      window.clearTimeout(timer);
      window.clearTimeout(retry);
    };
  }, []);
}
