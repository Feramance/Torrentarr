/** Custom event to request a tab switch from outside AppShell (e.g. Processes → qBit). */

export const NAVIGATE_TAB_EVENT = "qbitrr:navigate-tab";

export type NavigableTab =
  | "issues"
  | "processes"
  | "logs"
  | "history"
  | "radarr"
  | "sonarr"
  | "lidarr"
  | "readarr"
  | "qbittorrent"
  | "config"
  | "maintenance";

export interface ConfigDestination {
  client?: string;
  arr?: string;
  field?: string;
  monitorPath?: string;
}

export interface NavigateTabDetail {
  tab: NavigableTab;
  historyWorker?: string;
  relatedLogs?: {
    stream: string;
    runId?: string;
    startedAt?: string;
    operation?: "SearchSubmission";
  };
  manageClientId?: string;
  configDestination?: ConfigDestination;
  issueInstance?: string;
  issueCategory?: "automation" | "system";
  preserveConfigDraft?: boolean;
  /** When set with tab=qbittorrent, expand/focus this category after navigation. */
  qbitCategory?: string;
  qbitInstance?: string;
}

/** Dispatch a cross-component request to switch the active AppShell tab. */
export function requestNavigateTab(detail: NavigateTabDetail): void {
  window.dispatchEvent(
    new CustomEvent<NavigateTabDetail>(NAVIGATE_TAB_EVENT, { detail }),
  );
}
