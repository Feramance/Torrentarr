# Disk space management

Free-space monitors belong to each torrent client. One manager reserves capacity
across clients sharing the same local filesystem, using the largest configured
reserve for that filesystem. Downloads are considered oldest first. Completed seeders
are preserved; low space does not authorize deleting protected content.

## Configuration

Use **Config → client → Download location**, **Path mappings**, and **Free-space
monitors**. DownloadPath is seen by the torrent client; monitor Path is seen by
Torrentarr. Both must refer to the intended mounted filesystem.

```toml
[TorrentClient.seedbox]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080
DownloadPath = '/downloads'

[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'

[[TorrentClient.seedbox.FreeSpace]]
Path = '/data/downloads'
Enabled = false
Limit = '10G'
```

Set Enabled deliberately after verifying paths. Limit accepts non-negative bytes or
K/M/G/T suffixes; invalid values and traversal paths are rejected. Zero reserves no
extra bytes. An unavailable monitor fails conservatively for affected downloads.
Mapped paths use the longest matching prefix. Unmapped/ambiguous ownership cannot
justify optimistic free-space decisions.

## How it works

For each filesystem the manager budgets current free bytes, the largest reserve,
and remaining downloading work. It pauses downloading work when capacity is
insufficient and resumes only work carrying Torrentarr's own free-space pause marker.
Manually paused downloads stay unmarked. A failed pause rolls back a newly created
marker; lost markers on tagless clients are stored in SQLite per client/hash.
Completed seeders are not paused as a space-saving substitute. Unresolved pressure
is reported; files are not deleted to satisfy the reserve.

## Migration

Global Settings.CompletedDownloadFolder, FreeSpaceFolder, FreeSpace, AutoPauseResume
and FreeSpaceThresholdGB are accepted legacy migration inputs. New saves use per-client
DownloadPath and FreeSpace lists. Single-client unambiguous paths can migrate;
ambiguous multi-client inherited monitors are disabled for review. Existing explicit
per-client settings take precedence. Legacy environment overrides remain compatibility
inputs, not new global controls. See [migration details](../configuration/config-file.md#client-download-paths-and-free-space-monitors).

## Monitoring and troubleshooting

Inspect Config application status, monitor errors and free-space logs. In Docker,
monitor paths must exist inside the container, with the actual download volume mounted.
A path mapping alone cannot create access to a filesystem. Verify mount identity,
permissions, free bytes and required reserve before changing limits.

For repeated pauses, budget unfinished downloads, imports and other applications
sharing the disk. For missing resumes, inspect pause ownership and available capacity;
never assume a manual pause belongs to Torrentarr. A delayed worker or unavailable
client may postpone handling, but does not make protected content eligible for removal.
See [path troubleshooting](../troubleshooting/path-mapping.md) and
[maintenance protection](torrent-maintenance.md).
