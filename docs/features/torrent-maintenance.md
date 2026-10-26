# Manage

Manage brings maintenance configuration, previews, execution, and history into one place. Select a torrent client, then expand one tool card. Opening the page does not scan or change torrents or files.

## Tools

| Group | Tools |
| --- | --- |
| Recovery | Error recovery, paused-torrent repair, unregistered-torrent cleanup |
| Organization | Category reconciliation, automatic torrent management, private-torrent tagging, tracker-error tagging |
| Storage | Orphan-file cleanup, hardlink audit, recycle-bin retention |

Every original configuration option remains available. Common options appear first; advanced tuning is collapsed. Shared settings hold the maintenance master switch, scope, path mappings, recycle bin, and execution safeguards. Connection, free-space monitoring, seeding, and trackers remain in the client configuration dialog.

## Configure, check, apply

1. Open **Manage**, or use **Open Manage** in a saved torrent client's configuration dialog.
2. Enable maintenance and configure the shared settings and chosen tool. Save changes. Saving never runs a tool.
3. Choose the tool's **Check** action. Review proposed mutations, blocked changes, and diagnostics in **Preview results**.
4. Apply the reviewed preview once in Manual, or select Automatic and save to enable future scheduled runs. Automatic remains enabled after restart and requires no restart to activate.

Unsaved changes must be saved or discarded before switching clients or leaving. The client-dialog shortcut preserves other client edits. Preview plans expire at their configured TTL, execute once, and become invalid after relevant configuration changes. Results and history start collapsed and open when used. Existing maintenance links continue to open Manage.

## Scheduling and protection

Every torrent removal and deletion of referenced torrent content requires current HnR clearance. Manual execution, custom-format rejection, failed categories, import errors, and file exclusions cannot bypass it. Unmet or unknown obligations preserve the torrent and content. Arr queue removal and blocklisting for failed downloads follow confirmed protected removal; Arr is not asked to remove the torrent independently. Recycle preparation rechecks clearance and restores moved content if clearance changes before client removal.

Manual mode applies a reviewed preview once. Automatic mode runs on the saved schedule without requiring a restart and retains scheduling across restarts. Existing session settings migrate to Manual with their schedules preserved. Cron expressions use UTC. Missed runs are not replayed; persisted claims prevent the same scheduled minute from running twice after restart.

The maintenance coordinator owns repair execution. Normal torrent processing and protection inspection observe repair incidents and preserve unresolved torrents. Durable request claims prevent duplicate checks across processes. Failed or unconfirmed checks do not authorize deletion. Recovery recycling requires an explicit outcome, confirmed completion, a continuously observed failure window, and all ownership and seeding safeguards.

## Recycling and retention

Recycling records durable source and destination paths and a committed recycling timestamp. Retention starts at that timestamp, rather than the media file's original modification time. Moving files on one filesystem does not reclaim disk space; permanently deleting expired recycled files does.

Incomplete moves and ambiguous ownership preserve files for manual recovery. Legacy recycled content receives a full retention interval when explicitly registered. Purges revalidate reviewed file size and modification time and respect actual file-count budgets. Orphan cleanup requires a fresh ownership inventory across configured clients and excludes recycle, resume-data, database, log, and pending recovery paths.

## Compatibility

The canonical configuration remains `Maintenance`. Interim `Storage` and `Recovery` layouts are read without discarding original maintenance settings. No optional tool is removed. Missing settings cannot be reconstructed if an earlier saved migration already discarded them; restore them from a backup or configure them again.

See [configuration](../configuration/maintenance.md), [control reference](../configuration/maintenance-controls.md), and [examples](maintenance-examples.md).
