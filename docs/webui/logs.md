# Logs view

The Logs page reads the same persisted process logs as the console. Select the
combined `all-YYYYMMDD.log` stream or a worker, Host, WebUI or free-space stream.
Files are under `logs/` in the resolved configuration data directory (normally
`/config/logs` in Docker).

## Loaded window and complete history

The initial view loads the last **2,000 lines**. The line count explicitly means
**loaded lines**. The viewer retains at most **20,000 lines** in memory; reaching
that bound does not truncate the file on disk.

- **Load older logs** prepends an earlier window, subject to the viewer limit.
- **Live** receives new entries using a server event stream, with delta polling as
  a fallback. It pauses when the page is inactive.
- **Follow** controls scrolling independently of Live.
- Level buttons and the viewer's find control filter/search the loaded window.
- **Search file** searches on the server; include rotated files for older history.
  Search results are bounded and indicate truncation.
- **Download** returns the complete selected file, including entries outside the
  loaded window. Download individual rotated files for previous days.

Multiline exception causes and appended operation/cycle/client/torrent context are
preserved. A request accepted by Arr is a submission result; check later observation
messages for completed work.

## Detail and retention

`Settings.ConsoleLevel` controls both console and file verbosity. DEBUG adds API
attempts/retries and decision reasons; TRACE adds snapshots, effective settings
and rule inputs. Runtime level selection is available from Processes. Persistent
changes belong in the configuration editor. See [Debug logging](../troubleshooting/debug-logging.md).

Files append on restart and rotate daily with seven files retained per stream.
There is **no size-based rotation or file size cap**. Large TRACE files remain
accessible through bounded tail reads and streaming downloads.

## API

Host exposes these routes under both `/web` and `/api`; standalone WebUI uses
`/web`. Authentication follows the configured WebUI settings.

| Route | Purpose |
| --- | --- |
| `GET /web/logs` | Available files and metadata |
| `GET /web/logs/{name}` | Bounded tail/history or byte delta |
| `GET /web/logs/{name}/stream` | Live events and rotation notifications |
| `GET /web/logs/{name}/search` | Bounded server search, optionally including rotated files |
| `GET /web/logs/{name}/download` | Complete file download |
| `POST /web/loglevel` | Runtime verbosity |

If entries appear missing, check the selected file/date, loaded window, active level
filters and verbosity first. If no files exist, check `Settings.Logging`, the
resolved configuration directory, filesystem permissions and whether the process
has started. Downloads can contain operational titles, hashes and local paths;
review them before sharing.
