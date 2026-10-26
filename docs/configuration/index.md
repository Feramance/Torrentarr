# Configuration

Use the WebUI Config editor or the TOML configuration file. Client identity,
connection credentials, torrent scope, paths, monitors, seeding, trackers and
maintenance are configured per client, with maintenance tools in Manage. Arr instances keep connection settings beside
API credentials and TLS options. Global Settings, Web Settings and Authentication
have separate dialogs. See [editor workflows](../webui/config-editor.md).

## Configuration structure

```toml
[Settings]
ConsoleLevel = 'INFO'
LoopSleepTimer = 5

[WebUI]
Host = '127.0.0.1'
Port = 6969
AuthDisabled = true

[TorrentClient.local]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080
UserName = 'admin'
Password = 'replace-me'
DownloadPath = '/downloads'

[Radarr-Movies]
Managed = true
URI = 'http://localhost:7878'
APIKey = 'replace-me'
Category = 'movies'
```

Clients may use canonical TorrentClient.<id> or legacy qBit/qBit-name sections.
Do not define duplicate identities. All clients are peers. Arr sections are named
single tables such as `[Radarr-Movies]`, `[Sonarr-TV]`, `[Lidarr-Music]`, or
`[Readarr-Books]`, rather than unnamed arrays of instances.

## Configuration sections

- [Full file reference](config-file.md): lookup order, fields, environment overrides,
  data-directory behavior, migration and live application.
- [Manage configuration](maintenance.md) and [complete controls](maintenance-controls.md):
  explicit roots, shared storage mappings, bounded recovery, recycling and retention.
- [Seeding and tracker protection](seeding.md): ordinary limits, legacy aliases,
  mandatory time, deadlines, partial/freeleech obligations and evidence uncertainty.
- [Arr configuration](arr/index.md), [torrent handling](torrents.md),
  [search](search/index.md), and [quality profiles](quality-profiles.md).
- [WebUI](webui.md) and [authentication](webui-authentication.md).

## Storage and migration

DownloadPath is client-visible; FreeSpace[].Path is local to Torrentarr; path mappings
translate between them. Monitors start empty and new rows disabled. Global storage
fields are legacy migration inputs. Ambiguous multi-client migration leaves monitors
disabled for review. See [current storage reference](config-file.md#client-download-paths-and-free-space-monitors)
and [disk-space behavior](../features/disk-space.md).

## Save and validation

Partial saves preserve unrelated values, unknown saved fields and redacted secrets.
Numeric bounds, narrowing filters and explicit storage mappings are validated.
Identity/connection sections open by default; validation opens affected sections.
Worker cycles adopt saved revisions at safe boundaries. Use Config status to distinguish
saved/pending/applied. Connection replacement failures do not reuse an old endpoint.

Manage saves only maintenance settings. Checking and applying a reviewed preview are separate actions. Manual applies once; Automatic enables future scheduled runs without requiring a restart. Existing schedules survive migration; persistent automatic execution is explicit per operation. Private tracker protection runs independently, and unknown or unmet obligations preserve seeding. Recovery preserves unresolved failures unless an explicitly configured recycling outcome passes confirmed-repair, continuous-failure, ownership, and protection checks.

## Configuration file location

TORRENTARR_CONFIG wins, followed by working-directory .config/config.toml,
~/config/config.toml, ~/.config/qbitrr/config.toml, ~/.config/torrentarr/config.toml,
and ./config.toml. The first existing file wins. Data/logs normally sit beside the
resolved config; Docker's /config layout uses /config as its data directory.

## Backup and troubleshooting

Back up config and SQLite together. Keep secrets out of version control and screenshots.
Use single-quoted TOML literal strings for regex/backslash patterns. Do not delete a
rename recovery journal after interruption. Only one Host may own a data directory.
See [quick start](../getting-started/quickstart.md),
[path mappings](../troubleshooting/path-mapping.md), and [common issues](../troubleshooting/common-issues.md).
