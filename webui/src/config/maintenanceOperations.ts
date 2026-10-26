import type {
  MaintenanceAction,
  MaintenanceOperation,
  MaintenanceOperationResult,
} from "../api/types";

export const maintenanceCategories = [
  "Recovery",
  "Organization",
  "Storage",
] as const;
export const maintenanceOperations: {
  operation: MaintenanceOperation;
  title: string;
  category: string;
  group: string;
  description: string;
  paths: string[];
  essential: string[];
}[] = [
  {
    operation: "ErrorRecovery",
    title: "Error recovery",
    category: "Recovery",
    group: "Recovery",
    description:
      "Repair errored torrents and control what happens after confirmed failure.",
    paths: ["ErrorRecovery"],
    essential: ["ErrorRecovery.Outcome"],
  },
  {
    operation: "RepairPaused",
    title: "Paused-torrent repair",
    category: "Recovery",
    group: "Recovery",
    description:
      "Resume completed paused torrents or recheck incomplete downloads.",
    paths: ["Repair"],
    essential: ["Repair.ResumeCompleted", "Repair.RecheckIncomplete"],
  },
  {
    operation: "UnregisteredCleanup",
    title: "Unregistered-torrent cleanup",
    category: "Recovery",
    group: "Recovery",
    description:
      "Handle confirmed tracker registration failures with repair and recycling safeguards.",
    paths: ["Unregistered"],
    essential: ["Unregistered.CompletedOnly"],
  },
  {
    operation: "CategoryReconcile",
    title: "Category reconciliation",
    category: "Organization",
    group: "Organization",
    description:
      "Apply category mappings, completion transitions, and tracker assignments.",
    paths: ["CategoryMappings", "CategoryTransitions", "TrackerAssignments"],
    essential: [
      "CategoryMappings",
      "CategoryTransitions",
      "TrackerAssignments",
    ],
  },
  {
    operation: "AutomaticManagement",
    title: "Automatic torrent management",
    category: "Organization",
    group: "Organization",
    description:
      "Enable client automatic management for eligible torrents; category paths may relocate files.",
    paths: ["AutomaticManagement.Enabled", "AutomaticManagement.IgnoredTags"],
    essential: [],
  },
  {
    operation: "PrivateTagging",
    title: "Private-torrent tagging",
    category: "Organization",
    group: "Organization",
    description: "Apply a configured tag to private torrents.",
    paths: ["AutomaticManagement.PrivateTag"],
    essential: ["AutomaticManagement.PrivateTag"],
  },
  {
    operation: "TrackerErrorTagging",
    title: "Tracker-error tagging",
    category: "Organization",
    group: "Organization",
    description:
      "Tag tracker failures and optionally remove cleared tags owned by Torrentarr.",
    paths: [
      "AutomaticManagement.TrackerErrorTag",
      "AutomaticManagement.RemoveClearedTrackerErrorTag",
    ],
    essential: ["AutomaticManagement.TrackerErrorTag"],
  },
  {
    operation: "OrphanScan",
    title: "Orphan-file cleanup",
    category: "Storage",
    group: "Storage",
    description:
      "Recycle unreferenced files after checking ownership across every torrent client.",
    paths: ["Orphans"],
    essential: ["Orphans.ScanRoots", "Orphans.MinimumAgeMinutes"],
  },
  {
    operation: "HardlinkAudit",
    title: "Hardlink audit",
    category: "Storage",
    group: "Storage",
    description:
      "Find torrents without additional hardlinks and apply the configured tag.",
    paths: ["Hardlinks"],
    essential: ["Hardlinks.Tag"],
  },
  {
    operation: "RecycleRetention",
    title: "Recycle-bin retention",
    category: "Storage",
    group: "Storage",
    description:
      "Permanently delete reviewed recycled files after their retention period.",
    paths: [],
    essential: [],
  },
];

export function maintenanceToolForPath(
  path: string[],
): MaintenanceOperation | undefined {
  const key = path.slice(1).join(".");
  if (path[1] === "Operations") return path[2] as MaintenanceOperation;
  return maintenanceOperations.find((tool) =>
    tool.paths.some((prefix) => key === prefix || key.startsWith(prefix + ".")),
  )?.operation;
}

export function inspectionOutcome(result?: MaintenanceOperationResult): string {
  if (!result) return "Not checked";
  if (result.state === "disabled") return "Not checked — disabled";
  if (result.state === "blocked" || result.uninspectable > 0)
    return "Could not check completely";
  if (result.actionable > 0) return "Changes available";
  if (result.blockedActions > 0) return "Changes blocked";
  return "Nothing found";
}

export function maintenanceActionTitle(action: MaintenanceAction): string {
  switch (action.kind) {
    case "AddTag":
      return `Add tag “${action.arguments?.tag ?? "configured tag"}”`;
    case "RemoveTag":
      return `Remove owned tag “${action.arguments?.tag ?? "configured tag"}”`;
    case "SetCategory":
      return `Change category to “${action.arguments?.category ?? "configured category"}”`;
    case "QuarantineFile":
      return "Move file into recycling";
    case "RecycleContent":
      return "Recycle files and remove torrent entry";
    case "RegisterRecycleEntry":
      return "Begin retention for legacy recycled content";
    case "DeleteExpiredRecycleItem":
      return "Permanently delete expired recycle files";
    case "Recheck":
      return "Recheck existing files";
    case "Resume":
      return "Resume torrent";
    case "SetAutomaticManagement":
      return "Enable qBittorrent automatic management";
    default:
      return action.kind;
  }
}
