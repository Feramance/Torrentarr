# Manage configuration

Open **Manage** and select a torrent client. This is the single editor for maintenance tools. The client configuration dialog provides a summary and **Open Manage** shortcut; connection, free-space monitors, seeding, and tracker settings remain in Config.

Tools are grouped under **Recovery**, **Organization**, and **Storage**. Expand a card to configure or check that tool. Only one tool card stays open. Advanced filters, limits, schedules, and tuning start collapsed. Shared scope, path mappings, recycling, and safeguards are configured once under **Shared settings**.

Save settings before checking a tool. Saving does not run it. **Manual** applies a reviewed preview once. **Automatic** runs on the saved schedule and stays enabled after restart; no restart is required. Saving Automatic enables future scheduled runs and never executes a tool immediately. The schedule is shown beside Automatic, in UTC; blank inherits the shared client schedule. Manual hides the schedule without discarding it. Schedules use UTC; displayed times use your browser timezone.

```toml
[TorrentClient.seedbox]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080
ManagedCategories = ['movies']

[TorrentClient.seedbox.Maintenance]
Enabled = true
Scope = 'managed'
ExcludedTags = ['keep']

[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'

[TorrentClient.seedbox.Maintenance.Orphans]
Enabled = true
ScanRoots = ['/data/downloads']
MinimumAgeMinutes = 1440
MaxFilesPerRun = 10

[TorrentClient.seedbox.Maintenance.RecycleBin]
Enabled = true
Path = '/data/recycle'
RetentionEnabled = true
RetentionDays = 30

[TorrentClient.seedbox.Maintenance.Operations.OrphanScan]
Enabled = true
ExecutionMode = 'manual'
Schedule = '15 3 * * *'
```

The same nested settings work under legacy `qBit` / `qBit-*` sections. Do not declare the same client ID in both section styles. The persisted name remains `Maintenance`; Manage is its interface label.

Path mappings describe the same storage in two namespaces; they do not mount or move folders. Filesystem cleanup needs explicit mappings and scan roots. Never select an Arr library or filesystem root as an orphan scan root.

All original tools and their settings remain supported. Interim `Storage` and `Recovery` settings fill missing canonical maintenance values. Existing canonical values, disabled states, scopes, zero budgets, outcomes, and schedules take precedence. Schema 6.20.2 changes existing `session` execution modes to `manual`; schedules and all other settings remain saved. Tools without an operation policy also default to Manual. Select Automatic explicitly to resume scheduling. Migration never enables unrelated tools.

See [workflow and safety](../features/torrent-maintenance.md), [all controls](maintenance-controls.md), and [examples](../features/maintenance-examples.md).

## Recycling and native deletion

`Maintenance.RecycleBin.Enabled` is opt-in and defaults to `false`. Existing explicit values are preserved. When false, automatic torrent removal and Manage cleanup send the authorized deletion to the torrent client, including content deletion when requested. No local path mappings or global ownership inventory are required for native deletion. The client controls content handling; Torrentarr does not protect shared files across clients on this path.

When true, cleanup moves eligible files to recycling and retains ownership, shared-file and rollback checks. A recycling failure never falls back to permanent deletion. Manage previews label permanent deletion separately, and configuration changes require a new preview. Orphan-file cleanup follows the same recycling setting: it recycles when enabled and permanently deletes individual orphan files when disabled. Both orphan modes require complete global ownership inspection, explicit local path mappings, and configured scan roots. Files of every extension are eligible, including orphaned `.parts` files. Active qBittorrent `.!qB` files and hash-associated `.parts` files are protected. Empty folders and subfolders are removed after eligible contents are cleaned up; scan/mapping roots, protected paths and nonempty folders remain intact. Recycle retention requires an enabled recycle bin.

Issue details show the affected subject's evidence and next action. HTTP request summaries include method, route template, status, duration and request ID: successful requests are DEBUG, 4xx WARNING and 5xx ERROR. Caller cancellations are recorded at DEBUG with status 499; unavailable upstream services return 503 and upstream timeouts return 504. URLs, credentials and tracker passkeys are redacted from diagnostic evidence.

Rechecks deferred by an active recovery claim or safety limit are recorded as skipped with a reason, rather than as client failures. Missing unselected or incomplete torrent files retain ownership protection without generating missing-file warnings. Successfully checked orphan prerequisites clear their old alerts even when another selected operation is blocked.

Scheduled previews also reconcile older alerts for the operations they inspect; previews with temporary configuration overrides do not clear production alerts. Tracker observations expire from their original fetch time, and unknown states are rechecked sooner. Recovered or removed torrents clear their tracker alerts during host inspections, without waiting for an Arr worker cycle. Ordinary user pauses remain protected without being counted as automation issues unless an actual protection risk is present.
