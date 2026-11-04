# Debug logging

`Settings.ConsoleLevel` controls console and file detail. The configuration is
unchanged by the additional diagnostics; use the existing level selector or config
when a different level is needed. Runtime level changes propagate to isolated
workers, normally within five seconds.

| Level | Operational evidence |
| --- | --- |
| CRITICAL | Fatal failures |
| ERROR | Failed operations |
| WARNING | Degraded operation, rejected work and recoverable failures |
| NOTICE / INFO | Cycle outcomes, synchronization, saved item changes, actions and accepted/rejected results |
| DEBUG | Skip reasons, eligibility/capacity, removal gates, API attempts and retries, deferrals |
| TRACE / VERBOSE | Torrent snapshots, effective configuration precedence, file rules, tracker protection and catalog/queue comparison inputs |

Existing item eligibility and keep decisions also remain visible at INFO. Deep
snapshots and HTTP attempt diagnostics require DEBUG or TRACE. Routine EF Core SQL
and tracking messages are suppressed; database warnings and errors remain visible.

## Reading an operation

The existing prefix is preserved:

```text
[2026-11-03 12:34:56][DEBUG   ][Torrentarr.Radarr    ]HTTP operation finished: "Arr Get arr.local:7878 /api/v3/queue"; attempts=1; status=200; successful=True; elapsed=12.0ms | ProcessId[42] | SourceContext[Torrentarr.Infrastructure.Services.ArrImportService] | Operation[Import] | CycleId[example] | TorrentHash[example]
```

Context is appended when available: process ID, component (`SourceContext`),
operation, cycle, Arr/client instance, category, torrent hash, media ID and command
ID. Follow a cycle ID within the instance file, then use the hash or media ID to
follow later observations. Start/end events report elapsed time and outcome; sleep
messages report the next wake time. Separate torrent and search loops have separate
cycle IDs.

An API request being accepted does not prove that an import, search or deletion has
finished. Logs distinguish submission, persisted replacement intent, retry phases
and subsequent observed state. Protection decisions include actual and required
ratio/time, deadline/risk evidence, native client limits, and the reason a removal
was denied. Effective seeding diagnostics identify category, Arr and tracker inputs.
File filtering reports the matching folder/name rule or extension exclusion.

HTTP diagnostics record service, method, host/port, route, attempt number, transport
and HTTP status, elapsed time, retry delay and terminal outcome. They use the existing
retry policy. Direct torrent deletion remains single attempt because a failed
response can leave an uncertain remote outcome. Cancellation is not retried.

Request bodies, response bodies, credentials, authentication headers, cookies,
tracker passkeys and URL query strings are not diagnostic payloads. Rendered text,
context and multiline exceptions pass through credential/control-character
sanitization. Exceptions retain their causes and stack lines. Operational titles,
hashes, local paths and server host names may still appear; review logs before
sharing them.

Catalog synchronization updates episodes and tracks in place. Unchanged inventories
produce no row writes; track sync summaries distinguish added, updated, deleted and
unchanged rows. On the first complete Lidarr sync after upgrading, legacy tracks
without Arr IDs are replaced once. A failed or unsafe inventory leaves stored
tracks intact.

Excluded-file cleanup uses allowlist authorization while preserving H&R, ownership,
protected-state and recovery checks. A cleanup denial keeps the import pending and
includes its reason. A search-progress save failure is reported separately from Arr
command rejection: a command may already have been accepted even though local
progress could not be saved.

## Files and console

Enabled events go to console/Docker output and `all-YYYYMMDD.log` under `logs/` in
the resolved configuration data directory. Worker events also go to
`worker-<ArrName>-YYYYMMDD.log`; Host/embedded WebUI events go to
`torrentarr-YYYYMMDD.log`, standalone WebUI to `webui-YYYYMMDD.log`, and free-space
events to `freespace-YYYYMMDD.log`. File text is plain; console text can use ANSI
colors. The message and context are the same.

Files append across restarts and rotate at local midnight, retaining seven daily
files per stream. There is no file size cap. TRACE can generate very large files on
large libraries; retention limits days, not disk usage. `Settings.Logging = false`
disables file logging while console output continues.

For Docker the directory is normally `/config/logs`:

```bash
docker logs --since 10m torrentarr
docker exec torrentarr ls -lh /config/logs
```

The [Logs view](../webui/logs.md) shows a bounded window, not the entire retained
file. Download the complete file or search it on the server when investigating
older decisions. The [diagnostic parity checklist](../development/logging-parity.md)
maps the process stages to the corresponding evidence.
