import { useCallback, useEffect, useState } from "react";
import { getTorrentAutomationStatus } from "../api/client";
import type { TorrentAutomationStatus } from "../api/types";
import { useInterval } from "../hooks/useInterval";

export function TorrentAutomationIssues({ clientId }: { clientId: string }) {
  const [status, setStatus] = useState<TorrentAutomationStatus>();
  const [error, setError] = useState("");
  const refresh = useCallback(async () => {
    try {
      setStatus(await getTorrentAutomationStatus());
      setError("");
    } catch {
      setError(
        "Torrent automation status is unavailable. Previous observations do not authorize changes.",
      );
    }
  }, []);
  useEffect(() => {
    void refresh();
  }, [refresh]);
  useInterval(() => {
    void refresh();
  }, 15000);
  const selected = (item: { clientInstanceId: string }) =>
    !clientId || clientId === "aggregate" || item.clientInstanceId === clientId;
  const risks =
    status?.protection
      .filter(selected)
      .filter(
        (p) =>
          p.state === "unknown" ||
          p.deadlineRisk !== "none" ||
          p.nativeStopLimitRisk ||
          p.reason.includes("pause ownership"),
      ) ?? [];
  const recovery = status?.recovery?.filter(selected) ?? [];
  const trackerIssues = status?.trackerIssues?.filter(selected) ?? [];
  return (
    <details className="config-section">
      <summary>
        Torrent automation ·{" "}
        {risks.length + recovery.length + trackerIssues.length} issues
      </summary>
      {error && <p role="status">{error}</p>}
      {status?.inspections?.filter(selected).map((i) => (
        <p key={i.clientInstanceId}>
          {i.clientInstanceId}: last protection inspection{" "}
          {i.lastSucceededAt
            ? new Date(i.lastSucceededAt).toLocaleString()
            : "not observed"}
          ; {i.consecutiveFailures} consecutive failures
          {i.nextAttempt
            ? `; next attempt ${new Date(i.nextAttempt).toLocaleString()}`
            : ""}
          .
        </p>
      ))}
      {[...risks, ...recovery, ...trackerIssues].map((item, index) => (
        <p key={`${item.clientInstanceId}-${item.torrentHash}-${index}`}>
          <strong>{item.torrentName || item.torrentHash}</strong> ·{" "}
          {item.clientInstanceId} · {"stage" in item ? `${item.stage}: ` : ""}
          {item.reason}
        </p>
      ))}
      {status && !risks.length && !recovery.length && !trackerIssues.length && (
        <p>
          No reported recovery or protection issues. This is not a storage
          cleanup check.
        </p>
      )}
    </details>
  );
}
