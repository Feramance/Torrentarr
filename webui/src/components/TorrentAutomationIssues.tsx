import { useCallback, useEffect, useState } from "react";
import { getTorrentAutomationStatus } from "../api/client";
import type { TorrentAutomationStatus } from "../api/types";
import { useInterval } from "../hooks/useInterval";

const unknownReasons = {
  "tracker-accounting-unconfirmed": {
    label: "Tracker accounting unconfirmed",
  },
  "no-tracker-reported": {
    label: "No tracker reported",
    guidance: "Check the torrent’s Trackers tab in qBittorrent.",
  },
  "no-matching-tracker-rule": {
    label: "No matching tracker rule",
    guidance: "Check tracker rules for the listed hostname and client.",
  },
} satisfies Record<
  NonNullable<TorrentAutomationStatus["protection"][number]["unknownReason"]>,
  { label: string; guidance?: string }
>;

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
  const protection =
    status?.protection
      .filter(selected)
      .filter(
        (p) =>
          p.state === "unknown" ||
          p.deadlineRisk !== "none" ||
          p.nativeStopLimitRisk,
      ) ?? [];
  const recovery = status?.recovery?.filter(selected) ?? [];
  const trackerIssues = status?.trackerIssues?.filter(selected) ?? [];
  const issueCount =
    protection.filter(
      (item) =>
        item.nativeStopLimitRisk ||
        ["warning", "critical", "unachievable"].includes(item.deadlineRisk),
    ).length +
    recovery.filter((item) => item.stage !== "new" && item.stage !== "checked")
      .length +
    trackerIssues.filter((issue) => issue.requiresAttention !== false).length;
  return (
    <details className="config-section">
      <summary>Torrent automation · {issueCount} issues</summary>
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
      {[...protection, ...recovery, ...trackerIssues].map((item, index) => {
        const explanation =
          "unknownReason" in item && item.unknownReason
            ? unknownReasons[item.unknownReason]
            : undefined;
        return (
          <p key={`${item.clientInstanceId}-${item.torrentHash}-${index}`}>
            <strong>{item.torrentName || item.torrentHash}</strong> ·{" "}
            {item.clientInstanceId} · {"stage" in item ? `${item.stage}: ` : ""}
            {"tracker" in item && item.tracker ? `${item.tracker} · ` : ""}
            {explanation && (
              <>
                <strong>{explanation.label}</strong> ·{" "}
              </>
            )}
            {item.reason}
            {explanation &&
              "guidance" in explanation &&
              ` ${explanation.guidance}`}
          </p>
        );
      })}
      {status &&
        protection.length === 0 &&
        recovery.length === 0 &&
        trackerIssues.length === 0 && (
          <p>No reported recovery or protection issues.</p>
        )}
    </details>
  );
}
