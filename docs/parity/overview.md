# qBitrr and Torrentarr

Torrentarr is a **C# port** of [qBitrr](https://github.com/Feramance/qBitrr) (Python). For day-to-day use you only need to know the following.

## What is shared

- **Configuration:** The same `config.toml` format (Torrentarr also reads common paths used by qBitrr; see [Configuration](../configuration/config-file.md)).
- **Database:** The same **logical** schema for media/queue data; Torrentarr’s file is `torrentarr.db` (not `qbitrr.db`).
- **Goal:** Match qBitrr’s automation behavior (qBittorrent + Radarr, Sonarr, Lidarr) with compatible settings.

## What differs (by design)

- **Product release numbers:** Torrentarr’s **major** version is **one ahead** of qBitrr’s (e.g. qBitrr 5.x, Torrentarr 6.x) so the two products are not confused. Your `config.toml` `ConfigVersion` follows Torrentarr’s schema, not qBitrr’s tag.
- **How it runs:** Torrentarr uses a .NET **Host** plus **Web UI** and **per-Arr worker** processes, not a single Python process.

## Do I need the parity matrix?

**No**, unless you contribute code or need to compare behavior to upstream. Maintainer-facing detail lives in [contributor-reference.md](contributor-reference.md) and the [full parity matrix](full-parity-matrix.md).

## More reading

- [Getting started](../getting-started/index.md) — install and first run
- [Configuration index](../configuration/index.md)
- [Troubleshooting: database](../troubleshooting/database.md)
