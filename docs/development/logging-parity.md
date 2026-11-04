# Process diagnostic parity

Torrentarr follows qBitrr's diagnostic depth by process stage, rather than copying
Python messages verbatim. Its isolated worker architecture also needs process and
cycle context. This checklist describes implemented evidence, not a claim that
every possible live Arr/client response has been exercised.

| Stage | Evidence | Main implementation |
| --- | --- | --- |
| Worker lifecycle | PID, revision, restart delay, cycle ID/outcome/duration, next wake | Workers Program, WorkerProcessSupervisor |
| Connectivity | Client identity, host/port, probe and credential deferral | QBittorrentConnectionManager |
| Torrent inspection | State, progress, age/activity/completion, ETA, native limits | TorrentProcessor |
| Leave-alone decisions | Protection, forced/super seeding, age, effective stalled/ETA rules and free-space precedence | TorrentProcessor |
| File filtering | Per-file progress/priority, matching rule and configured allow/ignore inputs | TorrentProcessor |
| Seeding/removal | Category/Arr/tracker precedence, actual/required ratio/time, final inventory/import/protection gate | SeedingService |
| Tracker protection | Cache/fetch, rule result, remaining seed time, deadline and native risk | TorrentAutomationService, SeedingService |
| Catalog/queue synchronization | Instance/type, counts, successful saved changes and elapsed time | ArrSyncService, DatabaseRetryExtensions |
| Search | Candidate reason/scope/covered IDs, ordering, command capacity, submission and deferred work | ArrMediaService, SearchExecutor |
| Replacement retry | Persisted intent, blocklist phase, retries, deadlines and accepted commands/items | SonarrRetryQueue, ArrImportService |
| Profile switching | Observed/original/temp profile, cutoff, retry limit and outcome | QualityProfileSwitcherService |
| Import/custom formats | Path/mode precedence, queue/catalog scores, missing evidence, submission vs completion | ArrImportService |
| Free space | Client/path availability, allocation, pause/resume decision and duration | FreeSpaceService |
| Maintenance | Preview/run identity, candidate/blocked counts, action reason and stale/final result | MaintenanceCoordinator |
| API transport | Per-call attempts/status/timing, retry reason/delay, cancellation and terminal result | HttpRetryHelper |
| Delivery | Process routing, context, sanitized multiline causes, same console/file content | ServiceLogSink, ServiceLogFormatter |
| Viewer | Loaded-window count, older/full-file guidance, bounded memory | LogsView |

INFO records outcomes and existing operational item decisions. DEBUG explains gates
and attempts. TRACE adds inputs and effective settings. No configuration, scheduling,
retry policy, database schema or torrent action is changed to produce diagnostics.
HTTP logging never fetches extra data and never records bodies, headers or query
strings. Deletion's single-attempt transport path remains single attempt.

## Regression evidence

- `HttpDiagnosticLoggingTests`: concurrent request context isolation, transient
  response/retry details, exhaustion, cancellation, single-attempt mutation and
  exclusion of authentication/query/body secrets.
- `WorkerLogEventSinkTests`: concurrent process routing, context, console/file text,
  multiline exception sanitization, restart append behavior and safe filenames.
- `AutomationLoggingTests`: effective rule inputs and denied removal with exactly
  the existing tracker read and no mutations; successful database-save reporting.
- `TorrentProcessorFileFilterTests`: unchanged matching behavior and winning reasons.
- `LogsView.test.tsx`: bounded viewer/history, stream/delta behavior, loaded-window
  guidance, filters and rotation.
- Existing non-live service/worker/maintenance/import/search suites guard behavior.

Live verification uses normal scheduled processing. It must not manufacture torrent
removals, imports, searches or retries merely to obtain example log lines. Large
TRACE libraries can produce large daily files; daily retention has no size cap.
