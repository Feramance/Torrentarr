import { useEffect, useRef } from "react";

export const STATISTICS_UPDATED = "torrentarr:statistics-updated";

/** Coalesce invalidations, including changes arriving while a request is running. */
export function useStatisticsUpdates(
  active: boolean,
  refresh: () => void | Promise<void>,
): void {
  const latest = useRef(refresh);
  latest.current = refresh;
  useEffect(() => {
    if (!active) return;
    let stopped = false;
    let running = false;
    let pending = false;
    const update = async () => {
      if (document.hidden || stopped) return;
      pending = true;
      if (running) return;
      running = true;
      try {
        while (pending && !stopped && !document.hidden) {
          pending = false;
          try {
            await latest.current();
          } catch {
            /* Keep the last successful observation. */
          }
        }
      } finally {
        running = false;
      }
    };
    window.addEventListener(STATISTICS_UPDATED, update);
    document.addEventListener("visibilitychange", update);
    return () => {
      stopped = true;
      window.removeEventListener(STATISTICS_UPDATED, update);
      document.removeEventListener("visibilitychange", update);
    };
  }, [active]);
}
