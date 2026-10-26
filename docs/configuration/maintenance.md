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
