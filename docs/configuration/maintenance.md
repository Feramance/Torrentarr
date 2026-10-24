# Maintenance configuration

Open **Config → Configure** on a torrent client and use **Path mappings** to add,
edit, or remove mappings. Save the client to persist changes to `config.toml`.
Active worker cycles finish with their current mappings; subsequent cycles use
the saved revision. The configuration status distinguishes saved changes waiting
for active operations from changes applied by all components. No restart is needed. Closing without saving
leaves changes unapplied; reload configuration to discard a retained draft.
Maintenance remains opt-in: use the
[maintenance preview](../features/torrent-maintenance.md) before arming or applying actions.

## Path mappings

**Torrent client path** is the root reported by the torrent client.
**Torrentarr path** is the same directory as seen by Torrentarr's process. In Docker,
use the container path, not the host path. For example, `/downloads/movie/file.mkv`
becomes `/data/downloads/movie/file.mkv` with this mapping:

```toml
[[qBit.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'
```

Mappings translate paths: they do not create mounts, copy, or move files. Both
roots must be absolute without `.` or `..` segments. Duplicate client roots are
rejected; overlapping roots use the longest matching prefix. Windows client paths
are supported, such as `'D:\Downloads'`; the local root must be absolute for the
operating system running Torrentarr. Preview checks filesystem accessibility.

## Canonical torrent-client sections

Path mappings work identically for legacy `[qBit]` / `[qBit-name]` and canonical
`[TorrentClient.name]` instances:

```toml
[TorrentClient.seedbox]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080

[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'
```

Replace example paths with your own. Seeding ratio, time limits, category/tracker
overrides, and Hit & Run protection remain configured through the existing
[seeding settings](seeding.md).

Legacy share-policy and maintenance-notification tables are ignored when loading
existing TOML and omitted on the next save. They no longer perform actions or send
notifications, and are not converted into seeding rules. Loading alone does not
rewrite a file solely to remove those tables.
