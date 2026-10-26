# Advanced topics

The Host embeds the WebUI and supervises isolated Arr/category worker processes.
A standalone WebUI executable is available, but it is not the normal extra process
inside Host. All client instances are peers. Shared client state, configuration
publication and SQLite coordination prevent a worker failure from taking down the
web interface. See [architecture](architecture.md) and [database](database.md).

## Performance tuning

Settings.LoopSleepTimer is the ordinary torrent-processing interval in seconds;
SearchLoopDelay controls search spacing. Avoid reducing them solely to accelerate
file cleanup. Maintenance has independent UTC schedules and request/inspection caps.
Protection normally observes every five minutes; at-risk interrupted torrents use
targeted one-minute checks. Deadline recovery is inspected before audits and cleanup.
Ordinary tracker inspections may be cached for 15 minutes; final mutation checks
require fresh evidence. Network failures back off and preserve protection.

File inspection caps limit heavy eligibility work, while initial orphan directory
name ordering may still enumerate a large root. Use explicitly scoped roots and
daily scans for large libraries. Only one maintenance run executes at a time.

## Storage and configuration

Configure per-client DownloadPath, FreeSpace rows and Maintenance.PathMappings.
Paths do not create Docker mounts. One manager budgets shared filesystems across
clients. Complete seeders remain available; disk pressure does not override HnR.
See [disk-space management](../features/disk-space.md).

Config publication separates saved/pending/applied revisions. Worker cycles adopt
new snapshots at safe boundaries; connection replacements retire old endpoints.
Manual maintenance applies a reviewed preview once. Automatic mode requires
explicit opt-in, persists across restarts, and validates each run. Draft previews
cannot change active state. See [editor workflows](../webui/config-editor.md).

## Operations and recovery

Back up config and SQLite together before upgrading. Preserve interrupted rename
journals and investigate activation warnings. An unavailable client or uninspectable
filesystem preserves content; it does not clear tracker obligations. Unsupported
root-scoped hardlink counting remains excluded. External tracker account scraping,
point spending and tracker-side actions are not maintenance features.
