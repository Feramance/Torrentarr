# Quick start guide

Install a binary or Docker image using the [installation guide](installation/index.md).
qBittorrent is the shipped torrent adapter; Radarr, Sonarr, Lidarr and Readarr can
manage media categories. A client can also manage categories without an Arr.

## Configuration location

Set TORRENTARR_CONFIG to an explicit config.toml path when needed. Otherwise search
order is working-directory `.config/config.toml`, `~/config/config.toml`,
`~/.config/qbitrr/config.toml`, `~/.config/torrentarr/config.toml`, then `./config.toml`.
First run creates defaults if none exists. Docker normally mounts `/config` and sets
an explicit config path. SQLite and logs use the resolved config's data directory.

## Minimal setup

Copy [config.example.toml](https://github.com/Feramance/Torrentarr/blob/master/config.example.toml),
replace credentials and configure a client and Arr instance. Example:

```toml
[Settings]
ConsoleLevel = 'INFO'

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

[[TorrentClient.local.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'

[[TorrentClient.local.FreeSpace]]
Path = '/data/downloads'
Enabled = false
Limit = '10G'

[Radarr-Movies]
Managed = true
URI = 'http://localhost:7878'
APIKey = 'replace-me'
Category = 'movies'
```

Use container-visible service names instead of localhost when services run in
separate containers. Mount download files into Torrentarr at the mapped local path;
a mapping alone does not mount storage. Verify access before enabling monitors.
Configure authentication before exposing the listener to other machines.

## Build and start

For a local source build run `./build.sh`, which builds frontend assets before .NET.
Then run the Host from the build output or `dotnet run --project src/Torrentarr.Host`.
Docker builds include frontend assets; recreate the image/container after source
changes. See [Docker installation](installation/docker.md).

Open the configured listener and use Config connection tests. Verify client
connectivity, category ownership, Arr credentials and paths. Existing category save
paths are preserved when setting a default DownloadPath. All configured clients are
peers; legacy `[qBit]` remains a normal supported instance.

## Optional automation

Maintenance starts opt-in. Configure tracker protection before automatic cleanup.
Review the [tracker requirements and evidence limits](../configuration/seeding.md),
including mandatory completion-based time and unknown/private protection. Use
Manage → select a client → configure a tool → Save settings → Check → review.
Choose Manual to apply a reviewed preview once, or Automatic to enable future scheduled runs without requiring a restart.
New optional tools start disabled. Migration preserves settings, changes session modes to Manual, and never enables unrelated tools.

## Subsequent changes and rollback

Use Config while the Host owns the data directory. Ordinary saves apply at safe
processing boundaries; status distinguishes saved, pending and applied revisions.
Back up config and SQLite together before upgrading. Preserve an interrupted rename
journal. Rollback may lose newly introduced protection and scheduling behavior.
See [configuration reference](../configuration/config-file.md),
[editor workflows](../webui/config-editor.md), and [troubleshooting](../troubleshooting/index.md).
