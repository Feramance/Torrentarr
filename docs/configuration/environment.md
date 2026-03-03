# Environment Variables

Torrentarr reads the configuration file path from a single environment variable. Section-level overrides (e.g. per-setting env vars) are **not** supported in Torrentarr; use `config.toml` for all other settings.

---

## Config file path: TORRENTARR_CONFIG

**Variable:** `TORRENTARR_CONFIG`  
**Purpose:** Path to the `config.toml` file (or its directory, depending on deployment).  
**Used by:** All Torrentarr processes (Host, WebUI, Workers).

If set, this overrides the default config file search order. This is the **only** environment variable Torrentarr reads for configuration.

**Examples:**

```bash
# Full path to config file (e.g. Docker or custom path)
export TORRENTARR_CONFIG=/config/config.toml

# Windows (PowerShell)
$env:TORRENTARR_CONFIG = "C:\path\to\config\config.toml"
```

**Docker:**

```yaml
services:
  torrentarr:
    image: feramance/torrentarr:latest
    environment:
      TORRENTARR_CONFIG: /config/config.toml
    volumes:
      - /path/to/config:/config
```

When `TORRENTARR_CONFIG` is set and starts with `/config`, Torrentarr uses `/config` as the base directory for the database (`qbitrr.db`) and logs. Otherwise it uses `./config` (relative to the current working directory).

---

## Config file search order (when TORRENTARR_CONFIG is not set)

1. `~/config/config.toml`
2. `~/.config/qbitrr/config.toml`
3. `~/.config/torrentarr/config.toml`
4. `./config.toml`

The first existing file wins. If none exist, the default path used for generating a new config is `~/config/config.toml`.

---

## No section-level overrides

Torrentarr does **not** support environment variables that override individual config keys (e.g. `QBITRR_SETTINGS_CONSOLE_LEVEL`). That behavior exists in the original [qBitrr](https://github.com/Feramance/qBitrr) (Python). In Torrentarr, all settings come from `config.toml` (and the single `TORRENTARR_CONFIG` for where that file is).

To change settings:

- Edit `config.toml` directly, or
- Use the WebUI Config Editor, or
- Mount a different `config.toml` in Docker and point `TORRENTARR_CONFIG` at it.

---

## Related documentation

- [Configuration File](config-file.md) — Full `config.toml` reference
- [Docker Installation](../getting-started/installation/docker.md) — Docker setup
- [qBittorrent Configuration](qbittorrent.md) — qBittorrent connection
