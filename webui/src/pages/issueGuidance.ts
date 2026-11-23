type Guidance = [
  title: string,
  consequence: string,
  checks: string[],
  verification: string,
];

const previewAgain =
  "Preview the same operations for the same clients again. Check that the warning clears and the affected checks complete.";
const preview = (
  title: string,
  consequence: string,
  ...checks: string[]
): Guidance => [title, consequence, checks, previewAgain];

const maintenance: Record<string, Guidance> = {
  "missing-file": preview(
    "Torrentarr couldn’t find files at their mapped paths",
    "The preview could not verify these files on the local filesystem. Operations that need local file evidence may be incomplete.",
    "Check that each example file is selected and fully downloaded in the torrent client.",
    "For completed files, compare the client path with the path Torrentarr checked. Verify that the path mapping and mount make that file visible inside Torrentarr.",
  ),
  "missing-mapping": preview(
    "A required path mapping is missing",
    "The preview cannot translate client paths to local paths.",
    "Add a mapping from the client’s download prefix to the directory mounted in Torrentarr.",
  ),
  "invalid-mapping": preview(
    "A path mapping is invalid",
    "The preview cannot reliably translate client paths.",
    "Review the mapping error in Technical details and correct the prefixes in Config.",
  ),
  "unmatched-prefix": preview(
    "A client path matches no mapping",
    "Torrentarr cannot locate the file for local inspection.",
    "Compare the client path with the configured prefixes and add or correct the matching mapping.",
  ),
  "unsafe-path": preview(
    "A mapped path failed a safety check",
    "Torrentarr excluded the path from local inspection.",
    "Compare the two paths and check for traversal, symlinks, or a path outside the configured root.",
  ),
  "inaccessible-local-root": preview(
    "Torrentarr cannot access a mapped root",
    "Local files under this root could not be inspected.",
    "Check the mount and read permissions from the Torrentarr process or container.",
  ),
  "client-unavailable": preview(
    "Maintenance could not reach a torrent client",
    "Torrentarr could not inspect this client’s inventory.",
    "Check the client endpoint in Config and test access from Torrentarr.",
  ),
  "client-disabled": preview(
    "A disabled client’s ownership is unknown",
    "Maintenance cannot verify whether this client owns files in scope.",
    "Review which clients share the storage. Enable inspection for the client if it should participate.",
  ),
  "unknown-client": preview(
    "A selected maintenance client no longer exists",
    "The requested client could not be inspected.",
    "Refresh Manage and select a configured client.",
  ),
  "unknown-operation": preview(
    "A maintenance operation was not recognized",
    "The requested operation could not be previewed.",
    "Refresh Manage and select an operation offered by this version.",
  ),
  "torrent-list-failed": preview(
    "Maintenance could not list torrents",
    "Ownership and actions based on this client’s inventory could not be checked.",
    "Confirm the client opens and that Torrentarr’s credentials can list torrents.",
  ),
  "tracker-list-failed": preview(
    "Maintenance could not read tracker status",
    "Checks that depend on tracker data are incomplete.",
    "Open an affected torrent in the client and verify that its tracker list is accessible.",
  ),
  "file-list-failed": preview(
    "Maintenance could not read a torrent’s file list",
    "Torrentarr could not verify file ownership.",
    "Check the affected torrent’s Files tab and review client logs for listing failures.",
  ),
  "empty-file-list": preview(
    "The client returned an empty file list",
    "Torrentarr could not verify ownership for a nonempty torrent.",
    "Check that the client has completed metadata retrieval and displays the torrent’s files.",
  ),
  "file-enumeration-unsupported": preview(
    "The client cannot enumerate torrent files",
    "Operations requiring file ownership cannot use this client’s inventory.",
    "Check the client type and capability in Manage before selecting a file-based operation.",
  ),
  "properties-failed": preview(
    "Maintenance could not read torrent properties",
    "Torrentarr could not use those properties to classify the torrent.",
    "Check that the torrent still exists and that Torrentarr can read its properties.",
  ),
  "privacy-unknown": preview(
    "Torrent privacy could not be verified",
    "Operations that rely on privacy classification cannot safely use this torrent.",
    "Check the torrent’s metadata and privacy flag in its client.",
  ),
  "ownership-not-inspected": preview(
    "Shared file ownership was not inspected",
    "Torrentarr lacks ownership evidence from a client that may share this storage.",
    "Review participating clients and their path mappings before running file-based maintenance.",
  ),
  "orphan-prerequisite": preview(
    "Orphan scanning is missing a prerequisite",
    "Torrentarr could not establish safe orphan-scan coverage.",
    "Review the specific prerequisite in Technical details and check scan roots and client coverage in Manage.",
  ),
  "inspection-unsupported": preview(
    "The client cannot perform a required inspection",
    "The selected operation cannot verify its prerequisites with this client.",
    "Check the operation’s capability requirements in Manage and select a supported client.",
  ),
  "operation-prerequisite": preview(
    "A maintenance prerequisite was not met",
    "The affected operation was blocked during preview.",
    "Use the reason in Technical details to correct the indicated prerequisite, then inspect the preview result.",
  ),
  "local-inspection-failed": preview(
    "A local filesystem check failed",
    "Maintenance could not verify local evidence.",
    "Check the path and error in Technical details, then confirm that Torrentarr can read the mount.",
  ),
  "hardlink-inspection-failed": preview(
    "Torrentarr could not verify hardlinks",
    "The preview cannot confirm whether the file has a library hardlink.",
    "Check library-root coverage, file access, and hardlink support on the mounted filesystem.",
  ),
  "recycle-recovery-required": preview(
    "The recycle area needs recovery",
    "Retention housekeeping is blocked by an incomplete recycle operation.",
    "Review the recycle recovery details and recent maintenance runs before running retention again.",
  ),
  "conflicting-actions": preview(
    "Maintenance operations proposed conflicting actions",
    "Conflicting actions cannot safely run together.",
    "Review the affected torrent and preview the operations separately to decide which action is appropriate.",
  ),
};

const guidance: Record<string, Guidance> = {
  "worker.lifecycle": [
    "Worker is not making healthy progress",
    "Work for this Arr instance may be delayed.",
    [
      "Check the worker status in Processes and inspect its most recent log errors.",
      "Correct the reported dependency failure before restarting the worker.",
    ],
    "Confirm that the worker reports a healthy heartbeat and resumes successful cycles.",
  ],
  "worker.torrent": [
    "Worker torrent cycle failed",
    "Torrent work for this instance did not finish.",
    [
      "Inspect the torrent cycle error in worker logs. Check the client and storage implicated by the error.",
    ],
    "Wait for the next successful torrent cycle.",
  ],
  "worker.search": [
    "Worker search cycle failed",
    "Automatic searches for this instance may be delayed.",
    [
      "Check worker logs for the failing search step and confirm that the Arr instance is reachable.",
    ],
    "Wait for the next successful search cycle.",
  ],
  "network.internet": [
    "Torrentarr could not verify internet connectivity",
    "Work that requires connectivity may wait.",
    [
      "Check DNS and reachability of the configured probe targets from the Torrentarr host or container.",
    ],
    "Wait for the connectivity probe to succeed.",
  ],
  "client.connection": [
    "Torrent client connection check failed",
    "Torrent management and ownership checks for this client may be incomplete.",
    [
      "Check the client URL, port, credentials, and network access from Torrentarr.",
    ],
    "Wait for Torrentarr’s client version probe to succeed.",
  ],
  "storage.reserve": [
    "Download storage reserve needs attention",
    "Torrentarr could not check the monitored path or enforce its configured free-space reserve.",
    [
      "Check the monitor path, free space, and configured reserve.",
      "If storage is accessible, check the free-space manager logs and the torrent client’s connection before retrying.",
    ],
    "Wait for the next successful reserve check.",
  ],
  "config.apply": [
    "Saved configuration could not be applied",
    "One or more components may still be using an earlier configuration.",
    [
      "Review the failing component and error in Technical details, then correct and save the indicated setting.",
    ],
    "Confirm that all components acknowledge the accepted revision.",
  ],
  "config.validation": [
    "Configuration validation failed",
    "The indicated configuration values need correction.",
    [
      "Read the validation messages in Technical details and correct the indicated fields in Config.",
    ],
    "Save and confirm the validation warning clears.",
  ],
  "config.client": [
    "Torrent client configuration is incomplete",
    "Torrentarr cannot manage this client with the current settings.",
    [
      "Open this client’s settings and verify its type, address and required credentials.",
    ],
    "Save and wait for the configuration check to clear.",
  ],
  "config.storage": [
    "Client storage configuration needs correction",
    "Torrentarr cannot safely inspect the configured storage.",
    [
      "Review the reported path error and check this client’s mappings and local mounts.",
    ],
    "Save and check that the storage configuration warning clears.",
  ],
  "config.migration-action-required": [
    "Migrated client settings need review",
    "A migrated storage or client setting requires your input.",
    [
      "Read the migration findings in Technical details and correct the affected client’s settings.",
    ],
    "Save and confirm that the migration check clears.",
  ],
  "automation.tracker": [
    "A tracker reported a torrent problem",
    "The torrent may need attention before it can continue normally.",
    [
      "Review the tracker message in Technical details and compare it with the torrent’s tracker status in the client.",
    ],
    "Check that a later inspection no longer reports the problem.",
  ],
  "automation.recovery": [
    "Torrent recovery is pending",
    "Torrentarr has not confirmed successful recovery.",
    [
      "Review the recovery operation and stage, then check the torrent state and access to its files.",
    ],
    "Confirm the recovery operation completes in Manage.",
  ],
  "automation.protection": [
    "A tracker obligation may be at risk",
    "A seeding deadline or limit may prevent this torrent from meeting tracker requirements.",
    [
      "Check the tracker, deadline and seeding limits in Manage. Keep the torrent available while the obligation remains.",
    ],
    "Look for a fresh protection check that confirms the risk has cleared.",
  ],
  "automation.inspection": [
    "Torrent protection could not be inspected",
    "Torrentarr is relying on previously recorded protection information.",
    [
      "Verify client connectivity and that Torrentarr can read torrent and tracker data.",
    ],
    "Wait for the next successful protection inspection.",
  ],
  "maintenance.run": [
    "Maintenance execution failed",
    "Some selected actions may still need attention.",
    [
      "Open Recent Runs in Manage, inspect the failed action, and address its reported error before retrying.",
    ],
    "Confirm a successful run for the same client selection.",
  ],
  "arr.sync.catalog": [
    "Arr catalog sync failed",
    "Torrentarr’s local media catalog may be out of date.",
    [
      "Check the Arr connection and inspect the catalog synchronization error in worker logs.",
    ],
    "Wait for the next successful catalog sync.",
  ],
  "arr.sync.queue": [
    "Arr queue sync failed",
    "Torrentarr may have stale download queue information.",
    [
      "Check that the Arr queue is accessible and inspect the queue synchronization error.",
    ],
    "Wait for the next successful queue sync.",
  ],
  "search.capacity": [
    "Arr search queue capacity could not be checked",
    "Torrentarr holds searches while it cannot read the command queue.",
    [
      "Check Arr connectivity and review the command queue error in worker logs.",
    ],
    "Wait for Torrentarr to read the queue successfully.",
  ],
  "search.submission": [
    "Arr search submission could not be confirmed",
    "The requested search may not have started.",
    [
      "Check the candidate and request error in Technical details. Look for the corresponding command in Arr.",
    ],
    "Confirm that Arr accepts a subsequent submission for this candidate.",
  ],
  "search.progress": [
    "Accepted search progress could not be saved",
    "Arr accepted the command, but Torrentarr may retry it because local progress was not recorded.",
    ["Check the Arr command and review local database errors before retrying."],
    "Confirm that Torrentarr records progress on a later successful cycle.",
  ],
  "profile.prepare": [
    "Temporary quality profile could not be prepared",
    "Torrentarr skipped the affected search.",
    [
      "Verify the configured original and temporary profiles in Arr and check the worker log for the failed change.",
    ],
    "Wait for a later search to prepare the profile successfully.",
  ],
  "profile.restore": [
    "Original quality profile could not be restored",
    "An affected Arr item may still have the temporary profile.",
    [
      "Compare the current profile and the recorded original in Arr. Resolve a conflicting manual change before retrying.",
    ],
    "Confirm the affected item has its intended profile and the warning clears.",
  ],
  "import.ownership": [
    "Import owner could not be determined",
    "Torrentarr cannot route this torrent to an Arr instance with confidence.",
    [
      "Compare the torrent’s category and client with the managed categories and subcategory rules.",
    ],
    "Confirm a later processing pass verifies ownership or excludes the torrent.",
  ],
  "import.scan": [
    "Arr import scan failed",
    "The completed download may still need to be imported.",
    ["Check that Arr can access the download path and inspect the scan error."],
    "Confirm that Arr accepts the import scan.",
  ],
  "import.history": [
    "Arr import history could not be checked",
    "Torrentarr could not verify import state.",
    ["Check the Arr connection and look up this download in Arr history."],
    "Wait for Torrentarr’s history check to succeed.",
  ],
  "import.replacement": [
    "Replacement search could not be confirmed",
    "The replacement workflow may still be waiting for Arr.",
    [
      "Check the affected Arr queue item and worker logs for the failed replacement step.",
    ],
    "Confirm the replacement workflow completes and the issue clears.",
  ],
  "torrent.failed-category": [
    "Failed torrent could not be moved to its parking category",
    "Torrentarr could not confirm the intended parking state.",
    [
      "Check the client state and the parking failure in worker logs before changing the torrent’s location.",
    ],
    "Confirm Torrentarr can complete parking for this torrent.",
  ],
  "import.rescue": [
    "Automatic import rescue needs attention",
    "Torrentarr could not finish rescuing the affected download.",
    [
      "Read the rescue reason and check the download’s queue and import state in Arr.",
    ],
    "Confirm the rescue step completes on a later pass.",
  ],
  "import.rescue-reconciliation": [
    "Import rescue state could not be reconciled",
    "Torrentarr is holding further rescue work until the prior result can be verified.",
    [
      "Check Arr queue access and review the reconciliation error in the worker logs.",
    ],
    "Wait for Torrentarr to verify the previous rescue state.",
  ],
  "torrent.processing": [
    "Torrent processing did not complete",
    "Some work for the torrent or category may be delayed.",
    [
      "Read the processing error and check the affected client, category, or storage location.",
    ],
    "Wait for the same torrent or category to process successfully.",
  ],
  "media.validation-unavailable": [
    "Media inspection is unavailable",
    "Torrentarr cannot validate media with ffprobe.",
    [
      "Check the expected ffprobe executable path and permissions, or enable the configured ffprobe updater.",
    ],
    "Confirm that ffprobe runs and a fresh media inspection succeeds.",
  ],
  "ffprobe.update": [
    "ffprobe update failed",
    "A usable ffprobe update was not confirmed.",
    [
      "Check the release, platform, and installation destination described in Technical details.",
    ],
    "Confirm the updater completes and ffprobe runs.",
  ],
  "database.health": [
    "Database health could not be verified",
    "Persistent state may be unavailable and some work may be held.",
    [
      "Check database logs, storage availability, and permissions on the data directory.",
    ],
    "Wait for a successful database health check.",
  ],
  "update.check": [
    "Application update check failed",
    "Torrentarr could not confirm whether a release is available.",
    ["Check network access to the release service and inspect update logs."],
    "Confirm that a subsequent update check completes.",
  ],
  "update.apply": [
    "Application update did not complete",
    "Torrentarr could not confirm installation of the requested release.",
    [
      "Review the apply error in update logs and check access to the installation directory.",
    ],
    "Confirm the update result reports success and the running version matches.",
  ],
  "shared-file-filter": [
    "Shared file filter unavailable",
    "Torrentarr uses the last valid shared filter if available. Processing and import wait when there is no usable list.",
    ["Check the filter source and the reported read or parsing error."],
    "Confirm the filter loads on the next check.",
  ],
};

const removalReasons: Record<string, string> = {
  SeedingLimit: "The torrent reached a configured seeding limit.",
  StalledDownload: "The stalled-download rule requested removal.",
  RejectedByAllowlist: "The allowlist rule requested removal.",
  RejectedByCustomFormat: "The custom-format rule requested removal.",
  OrdinaryRetirement: "The retirement rule requested removal.",
};

export function issueGuidance(code: string): Guidance | undefined {
  if (code.startsWith("maintenance.preview.")) {
    const diagnostic = code.slice("maintenance.preview.".length);
    return Object.hasOwn(maintenance, diagnostic)
      ? maintenance[diagnostic]
      : undefined;
  }
  if (code.startsWith("torrent.removal.")) {
    const reason = removalReasons[code.slice("torrent.removal.".length)];
    if (typeof reason === "string")
      return [
        "Torrent removal was blocked",
        reason,
        [
          "Check the blocker in Technical details and review the torrent’s tracker obligations and storage state in Manage.",
        ],
        "Wait for a fresh evaluation to clear the blocker.",
      ];
  }
  return Object.hasOwn(guidance, code) ? guidance[code] : undefined;
}
