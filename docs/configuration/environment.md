# Environment Variables

Torrentarr supports a config path override plus section-level environment overrides for `Settings` and primary `qBit` fields.

---

## Config file path override

**Variable:** `TORRENTARR_CONFIG`
**Purpose:** Path to the `config.toml` file (or its directory, depending on deployment).
**Used by:** All Torrentarr processes (Host, WebUI, Workers).

If set, this overrides the default config file search order.

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

When `TORRENTARR_CONFIG` is set and starts with `/config`, Torrentarr uses `/config` as the base directory for the database (`torrentarr.db`) and logs. Otherwise it uses `./config` (relative to the current working directory).

---

## Config file search order (when TORRENTARR_CONFIG is not set)

1. `~/config/config.toml`
2. `~/.config/qbitrr/config.toml`
3. `~/.config/torrentarr/config.toml`
4. `./config.toml`

The first existing file wins. If none exist, the default path used for generating a new config is `~/config/config.toml`.

---

## Section-level overrides

Torrentarr reads the following environment variables at load time:

- `TORRENTARR_SETTINGS_*` for `Settings` keys.
- `TORRENTARR_QBIT_*` for primary `qBit` connection keys.

For compatibility with qBitrr-style deployments, equivalent `QBITRR_*` aliases are also accepted for these keys.

### Supported `Settings` overrides

- `TORRENTARR_SETTINGS_CONSOLE_LEVEL`
- `TORRENTARR_SETTINGS_LOGGING`
- `TORRENTARR_SETTINGS_COMPLETED_DOWNLOAD_FOLDER`
- `TORRENTARR_SETTINGS_FREE_SPACE`
- `TORRENTARR_SETTINGS_FREE_SPACE_FOLDER`
- `TORRENTARR_SETTINGS_NO_INTERNET_SLEEP_TIMER`
- `TORRENTARR_SETTINGS_LOOP_SLEEP_TIMER`
- `TORRENTARR_SETTINGS_SEARCH_LOOP_DELAY`
- `TORRENTARR_SETTINGS_AUTO_PAUSE_RESUME`
- `TORRENTARR_SETTINGS_FAILED_CATEGORY`
- `TORRENTARR_SETTINGS_RECHECK_CATEGORY`
- `TORRENTARR_SETTINGS_TAGLESS`
- `TORRENTARR_SETTINGS_IGNORE_TORRENTS_YOUNGER_THAN`
- `TORRENTARR_SETTINGS_FFPROBE_AUTO_UPDATE`
- `TORRENTARR_SETTINGS_AUTO_UPDATE_ENABLED`
- `TORRENTARR_SETTINGS_AUTO_UPDATE_CRON`
- `TORRENTARR_SETTINGS_PING_URLS` (comma-separated list)

### Supported primary `qBit` overrides

- `TORRENTARR_QBIT_DISABLED`
- `TORRENTARR_QBIT_HOST`
- `TORRENTARR_QBIT_PORT`
- `TORRENTARR_QBIT_USERNAME`
- `TORRENTARR_QBIT_PASSWORD`

When both `TORRENTARR_*` and `QBITRR_*` variants are present for the same key, `TORRENTARR_*` takes precedence.

---

## Related documentation

- [Configuration File](config-file.md) — Full `config.toml` reference
- [Docker Installation](../getting-started/installation/docker.md) — Docker setup
- [qBittorrent Configuration](qbittorrent.md) — qBittorrent connection
