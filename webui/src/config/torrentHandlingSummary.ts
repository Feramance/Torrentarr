/**
 * Live summary text for how torrents are handled based on config state.
 * Used by ConfigView Arr and torrent client modals; updates as the user edits (no save required).
 */

import { get } from "lodash-es";
import {
  parseDurationToSeconds,
  parseDurationToMinutes,
} from "./durationUtils";
import type { ConfigDocument } from "../api/types";

function trackerProtectionSummary(
  tracker: Record<string, unknown>,
  name: string,
): string | undefined {
  const policy = tracker.Protection as Record<string, unknown> | undefined;
  if (!policy || typeof policy !== "object") return undefined;
  const preset = String(policy.Policy ?? "custom");
  const hours =
    preset === "onlyencodes"
      ? 48
      : preset === "torrentleech-power"
        ? 192
        : Number(policy.MinimumSeedHours ?? 48);
  return (
    `- **${name}** — ${preset} protection: ${hours} mandatory completed seed hours, ${Number(policy.AdditionalSeedHours ?? 6)} extra hours and ${Number(policy.AccountingGraceMinutes ?? 60)} minutes of accounting grace. ` +
    (preset === "onlyencodes"
      ? "Ratio never replaces mandatory time. The default 168-hour warning deadline awaits confirmation. "
      : policy.AllowClientRatioClearance
        ? "Client-estimated ratio clearance is explicitly enabled; it cannot guarantee tracker credit. "
        : "Client-ratio-only clearance is disabled. ") +
    "Partial and unknown obligations remain protected. Every applicable tracker must clear; a missed deadline never permits removal."
  );
}

const REMOVE_TORRENT_LABELS: Record<number, string> = {
  [-1]: "Do not remove",
  1: "On max upload ratio",
  2: "On max seeding time",
  3: "On ratio OR time",
  4: "On ratio AND time",
};

function getVal<T = unknown>(
  state: ConfigDocument | null,
  path: string[],
  fallback: T,
): T {
  if (!state) return fallback;
  const v = get(state, path);
  return (v === undefined || v === null ? fallback : v) as T;
}

function plural(n: number, singular: string, pluralStr: string): string {
  return n === 1 ? singular : pluralStr;
}

function formatSeconds(seconds: number): string {
  if (!Number.isFinite(seconds) || seconds < 0) return "disabled";
  if (seconds >= 86400) {
    const days = Math.round(seconds / 86400);
    return `${days} ${plural(days, "day", "days")}`;
  }
  if (seconds >= 3600) {
    const hours = Math.round(seconds / 3600);
    return `${hours} ${plural(hours, "hour", "hours")}`;
  }
  if (seconds >= 60) {
    const minutes = Math.round(seconds / 60);
    return `${minutes} ${plural(minutes, "minute", "minutes")}`;
  }
  const secs = Math.round(seconds);
  return `${secs} ${plural(secs, "second", "seconds")}`;
}

function formatMinutes(minutes: number): string {
  if (!Number.isFinite(minutes) || minutes < 0) return "disabled";
  if (minutes >= 1440) {
    const days = Math.round(minutes / 1440);
    return `${days} ${plural(days, "day", "days")}`;
  }
  if (minutes >= 60) {
    const hours = Math.round(minutes / 60);
    return `${hours} ${plural(hours, "hour", "hours")}`;
  }
  const mins = Math.round(minutes);
  return `${mins} ${plural(mins, "minute", "minutes")}`;
}

function resolveHnrMode(v: unknown): "and" | "or" | "disabled" {
  if (v === true) return "and";
  if (v === false) return "disabled";
  const s = String(v ?? "").toLowerCase();
  if (s === "and" || s === "or") return s;
  return "disabled";
}

function removeTorrentLabel(num: number): string {
  const n = Number(num);
  return REMOVE_TORRENT_LABELS[n] ?? "Do not remove";
}

function listPreview(arr: unknown[], maxItems = 3, suffix = "\u2026"): string {
  if (!Array.isArray(arr) || arr.length === 0) return "";
  const parts = arr
    .slice(0, maxItems)
    .map((x) => String(x).trim())
    .filter(Boolean);
  if (parts.length === 0) return "";
  const rest = arr.length > maxItems ? suffix : "";
  return parts.join(", ") + rest;
}

/** Build human-readable summary for an Arr instance: lifetime narrative. */
export function getArrTorrentHandlingSummary(
  state: ConfigDocument | null,
): string {
  if (!state || typeof state !== "object") {
    return "No torrent handling rules configured.";
  }

  const blocks: string[] = [];
  const torrent = getVal<Record<string, unknown>>(state, ["Torrent"], {});
  const caseSensitive = Boolean(torrent.CaseSensitiveMatches);
  const folderExcl = Array.isArray(torrent.FolderExclusionRegex)
    ? (torrent.FolderExclusionRegex as string[]).length
    : 0;
  const fileExcl = Array.isArray(torrent.FileNameExclusionRegex)
    ? (torrent.FileNameExclusionRegex as string[]).length
    : 0;
  const allowlist = Array.isArray(torrent.FileExtensionAllowlist)
    ? (torrent.FileExtensionAllowlist as string[])
    : [];
  const allowlistPreview = listPreview(allowlist, 4);
  const autoDelete = Boolean(torrent.AutoDelete);
  const ignoreYounger = parseDurationToSeconds(
    torrent.IgnoreTorrentsYoungerThan,
    -1,
  );
  const maxEta = parseDurationToSeconds(torrent.MaximumETA, -1);
  const maxDeletable =
    Math.round(Number(torrent.MaximumDeletablePercentage) * 10000) / 100;
  const doNotRemoveSlow = Boolean(torrent.DoNotRemoveSlow);
  const stalledDelayMin = parseDurationToMinutes(torrent.StalledDelay, -1);
  const reSearchStalled = Boolean(torrent.ReSearchStalled);

  blocks.push("### How a torrent is handled");

  // 1. New / downloading
  const newParts: string[] = [];
  if (Number.isFinite(ignoreYounger) && ignoreYounger >= 0) {
    newParts.push(
      `New torrents are ignored for the first ${formatSeconds(ignoreYounger)}`,
    );
  }
  if (maxEta >= 0) {
    newParts.push(
      `torrents with estimated time remaining over ${formatSeconds(maxEta)} may be treated as stuck and removed`,
    );
  }
  if (Number.isFinite(maxDeletable) && maxDeletable <= 100) {
    newParts.push(
      `torrents at or below ${maxDeletable}% completion may be removed`,
    );
  }
  newParts.push(
    doNotRemoveSlow
      ? "slow downloads are kept"
      : "slow downloads may be removed",
  );
  newParts.push(
    autoDelete
      ? "non-media files are auto-deleted"
      : "non-media files are kept",
  );
  const matchLine =
    (caseSensitive ? "Case-sensitive" : "Case-insensitive") +
    (folderExcl || fileExcl
      ? ` matching with ${folderExcl + fileExcl} exclusion(s)`
      : "") +
    (allowlistPreview ? ` and allowlist ${allowlistPreview}` : "") +
    ".";
  blocks.push(
    (newParts.length > 0 ? newParts.join("; ") + ". " : "") +
      matchLine.charAt(0).toLowerCase() +
      matchLine.slice(1),
  );

  // 2. If the download stalls
  if (!Number.isFinite(stalledDelayMin) || stalledDelayMin < 0) {
    blocks.push(
      "Stalled downloads have infinite grace and are not removed through stalled handling.",
    );
  } else if (stalledDelayMin === 0) {
    blocks.push(
      "Stalled downloads are cleaned up immediately after the age gate.",
    );
  } else {
    blocks.push(
      `If the download stops progressing for ${formatMinutes(stalledDelayMin)}, it is treated as stalled and will be removed after that delay. ` +
        `Before removing, ${reSearchStalled ? "a replacement is searched for." : "no replacement search is performed."}`,
    );
  }

  // 3. When seeding
  const seeding = getVal<Record<string, unknown>>(
    state,
    ["Torrent", "SeedingMode"],
    {},
  );
  const removeTorrent = Number(seeding.RemoveTorrent ?? -1);
  const maxRatio = Number(seeding.MaxUploadRatio ?? -1);
  const maxTimeSec = parseDurationToSeconds(seeding.MaxSeedingTime, -1);
  const removeDead = Boolean(seeding.RemoveDeadTrackers);
  const removeMessages = Array.isArray(seeding.RemoveTrackerWithMessage)
    ? (seeding.RemoveTrackerWithMessage as string[]).length
    : 0;

  const seedParts: string[] = [];
  seedParts.push(
    `Once seeding, torrents ${removeTorrent === -1 ? "are not removed by ratio or time" : `may be removed ${removeTorrentLabel(removeTorrent).toLowerCase()}`}.`,
  );
  if (Number.isFinite(maxRatio) && maxRatio >= 0) {
    seedParts.push(
      `Maximum ratio is ${maxRatio} (upload ${maxRatio}\u00d7 what you downloaded).`,
    );
  }
  if (Number.isFinite(maxTimeSec) && maxTimeSec >= 0) {
    seedParts.push(`Maximum seeding time is ${formatSeconds(maxTimeSec)}.`);
  }
  if (removeDead || removeMessages > 0) {
    seedParts.push(
      "Tracker messages cannot directly delete torrents. Recovery keeps and reports failed torrents; protection checks guard normal removal.",
    );
  }

  const hnrMode = resolveHnrMode(seeding.HitAndRunMode);
  const minRatio = Number(seeding.MinSeedRatio ?? 1);
  const minDays = Number(seeding.MinSeedingTimeDays ?? 0);
  if (hnrMode === "disabled") {
    seedParts.push(
      "Legacy Hit and Run is off here. Tracker protection still applies; unknown private obligations are preserved.",
    );
  } else {
    const daysVal = Number.isFinite(minDays) ? minDays : 0;
    const daysLabel = plural(
      daysVal === 1 ? 1 : Math.max(0, daysVal),
      "day",
      "days",
    );
    const req =
      hnrMode === "and"
        ? "must reach both ratio and time before removal"
        : "reaching either ratio or time allows removal";
    seedParts.push(
      `Hit and Run is on (${hnrMode}): min ratio ${Number.isFinite(minRatio) ? minRatio : 1}, min time ${daysVal} ${daysLabel} \u2014 ${req}.`,
    );
  }
  blocks.push(seedParts.join(" "));

  return blocks.join("\n\n");
}

/** Build human-readable summary for a torrent client: lifetime narrative. */
export function getQbitTorrentHandlingSummary(
  state: ConfigDocument | null,
): string {
  if (!state || typeof state !== "object") {
    return "No torrent handling rules configured.";
  }

  const blocks: string[] = [];
  const seeding = getVal<Record<string, unknown>>(
    state,
    ["CategorySeeding"],
    {},
  );
  const managedCats = get(state, ["ManagedCategories"]) as string[] | undefined;
  const matchSubcategories = Boolean(get(state, ["MatchSubcategories"]));
  const managedPreview =
    Array.isArray(managedCats) && managedCats.length > 0
      ? managedCats.slice(0, 3).join(", ") +
        (managedCats.length > 3 ? "\u2026" : "")
      : "";

  const removeTorrent = Number(seeding.RemoveTorrent ?? -1);
  const maxRatio = Number(seeding.MaxUploadRatio ?? -1);
  const maxTimeSec = parseDurationToSeconds(seeding.MaxSeedingTime, -1);
  const dlLimit = Number(seeding.DownloadRateLimitPerTorrent ?? -1);
  const ulLimit = Number(seeding.UploadRateLimitPerTorrent ?? -1);
  const stalledDelayMin = parseDurationToMinutes(seeding.StalledDelay, -1);
  const ignoreYounger = parseDurationToSeconds(
    seeding.IgnoreTorrentsYoungerThan,
    -1,
  );

  blocks.push("### How a torrent is handled");

  // 1. New / in managed category
  if (managedPreview) {
    const scope = matchSubcategories
      ? `When a torrent category matches a managed prefix (Match subcategories is on; e.g. ${managedPreview} matches seed as well as seed/child paths)`
      : `When a torrent is in a managed category exactly (e.g. ${managedPreview})`;
    const newLine =
      scope +
      (Number.isFinite(ignoreYounger) && ignoreYounger >= 0
        ? `, it is left alone for the first ${formatSeconds(ignoreYounger)} so it is not treated as stalled.`
        : ".");
    blocks.push(newLine);
  } else if (Number.isFinite(ignoreYounger) && ignoreYounger >= 0) {
    blocks.push(
      `New torrents are ignored for the first ${formatSeconds(ignoreYounger)} so they are not treated as stalled.`,
    );
  }

  // 2. If the download stalls
  if (!Number.isFinite(stalledDelayMin) || stalledDelayMin < 0) {
    blocks.push(
      "Stalled downloads have infinite grace and are not removed through stalled handling.",
    );
  } else if (stalledDelayMin === 0) {
    let s = "Stalled downloads are cleaned up immediately after the age gate";
    if (managedPreview) {
      s += matchSubcategories
        ? ` for torrents under managed category prefixes (${managedPreview})`
        : ` in ${managedPreview}`;
    }
    s += ".";
    if (Number.isFinite(ignoreYounger) && ignoreYounger >= 0) {
      s += ` New torrents are ignored for the first ${formatSeconds(ignoreYounger)}.`;
    }
    blocks.push(s);
  } else {
    let s = `If the download stops progressing for ${formatMinutes(stalledDelayMin)}`;
    if (managedPreview) {
      s += matchSubcategories
        ? ` for torrents under managed category prefixes (${managedPreview})`
        : ` in ${managedPreview}`;
    }
    s += ", Torrentarr removes it.";
    if (Number.isFinite(ignoreYounger) && ignoreYounger >= 0) {
      s += ` New torrents are ignored for the first ${formatSeconds(ignoreYounger)} (not treated as stalled).`;
    }
    blocks.push(s);
  }

  // 3. When seeding
  const seedParts: string[] = [];
  seedParts.push(
    `Once seeding, a torrent ${removeTorrent === -1 ? "is not removed by ratio or time" : `may be removed ${removeTorrentLabel(removeTorrent).toLowerCase()}`}.`,
  );
  if (Number.isFinite(maxRatio) && maxRatio >= 0) {
    seedParts.push(
      `Maximum ratio is ${maxRatio} (upload ${maxRatio}\u00d7 what you downloaded).`,
    );
  }
  if (Number.isFinite(maxTimeSec) && maxTimeSec >= 0) {
    seedParts.push(`Maximum seeding time is ${formatSeconds(maxTimeSec)}.`);
  }
  seedParts.push(
    dlLimit >= 0 || ulLimit >= 0
      ? "Rate limits are set (per-torrent speed caps)."
      : "Rate limits are off.",
  );
  seedParts.push(
    "Torrents are not removed for being slow or stalled except as above.",
  );
  blocks.push(seedParts.join(" "));

  const hnrMode = resolveHnrMode(seeding.HitAndRunMode);
  const minRatio = Number(seeding.MinSeedRatio ?? 1);
  const minDays = Number(seeding.MinSeedingTimeDays ?? 0);
  if (hnrMode === "disabled") {
    blocks.push(
      "Legacy category Hit and Run is off. Tracker protection still applies; unknown private obligations are preserved.",
    );
  } else {
    const daysVal = Number.isFinite(minDays) ? minDays : 0;
    const daysLabel = plural(
      daysVal === 1 ? 1 : Math.max(0, daysVal),
      "day",
      "days",
    );
    const req =
      hnrMode === "and"
        ? "must reach both ratio and time before removal"
        : "reaching either ratio or time allows removal";
    blocks.push(
      `Hit and Run is on (${hnrMode}): min ratio ${Number.isFinite(minRatio) ? minRatio : 1}, min time ${daysVal} ${daysLabel} \u2014 ${req}.`,
    );
  }

  return blocks.join("\n\n");
}

/** Describe the rule on its own card as settings change. */
export function getTrackerSummary(tracker: ConfigDocument): string {
  const name = String(tracker.Name ?? "").trim() || "Tracker";
  const mode = resolveHnrMode(tracker.HitAndRunMode);
  const ratio = Number(tracker.MinSeedRatio ?? 1);
  const days = Number(tracker.MinSeedingTimeDays ?? 0);
  const time = `${days} ${plural(days, "day", "days")} of seeding`;
  const protection = trackerProtectionSummary(tracker, name);
  const legacy =
    mode === "disabled"
      ? "Legacy Hit & Run is off. Removal still requires every applicable obligation to clear."
      : `Legacy Hit & Run requires ratio ${ratio} ${mode === "and" ? "and" : "or"} ${time} before removal.`;
  const sort = tracker.SortTorrents
    ? " Queue sorting uses this rule’s priority when the client has queuing enabled."
    : "";
  return (protection ?? legacy) + sort;
}
