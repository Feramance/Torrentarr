# Torrent and search investigation — 3 November 2026

## Reconnection fix deployed at 10:10 Europe/Malta

Rebuilt the Compose image and recreated only `torrentarr` at 10:10:23. The new
container is healthy. Host and all seven Arr workers connected to `qBit-General`
at startup and fetched their category inventories. New logs confirm independent
connection reconciliation continues at approximately 15-second intervals while
normal torrent cycles sleep. Startup and follow-up log checks found no error-level
events. A real outage was not induced to test recovery against live torrents.

Rollback image: `torrentarr:before-reconnect-fix-20261103`. Configuration and SQLite
snapshots: `.config/deployment-backups/reconnect-20261103T091014Z/`.

## Implementation and validation

The connection manager now runs an independent 15-second recovery loop in Host,
Arr Workers, and standalone WebUI. Successful connections advance a connection
revision; sleeping host/Arr torrent loops check it at most five seconds apart and
resume without waiting for `LoopSleepTimer`. Healthy probes do not advance it.
The recovery loop retains the existing per-client timeout, serialized reconciliation,
configuration mutation guard, and rejected-credentials suppression.

Validation: 64 focused infrastructure/worker tests and 20 host tests passed. The
three recovery/wake-up regressions passed again after the final configuration guard
was added. The standalone WebUI also built without warnings or errors.
The separate false-success reporting, removal-intent, and super-seeding findings
below were not changed as part of this reconnection fix.

## Follow-up around 09:45 Europe/Malta

qBittorrent is reachable again: its version endpoint returned **HTTP 200 both from
the host and from inside the running Torrentarr container**. These were read-only,
unauthenticated GETs; no reconnect, restart, or torrent action was triggered.

Torrentarr has not yet recovered its registered client. The host's 09:44:35 log
still reports `connected=False`, repeatedly deferring protection inspection with
the old `nextAttempt=08:52:05Z` (09:52 local). There have been no new worker torrent
cycles since the failed 09:24 cycles. Their logged next wake is approximately
10:24 local. The protection loop also requires an already registered client; it
does not itself reconnect it. The host orchestrator's connection reconciliation
uses the same configured one-hour loop interval (`Host/Program.cs:2653–2676`).
An overdue protection-inspection timer alone therefore does not restore processing.

Since 09:26, another **16 search commands** were accepted: four each for Lidarr,
Radarr-1080, Radarr-Anime, and Sonarr-1080. No new warning/error lines were found
in the checked service logs during that follow-up window. Radarr Anime accepted
IDs 80 and 81 again at 09:39 and 09:44, approximately 75 minutes after their earlier
submissions. This is consistent with `SearchAgainOnSearchCompletion=true` and its
small candidate set, rather than duplicate submissions within one pass. It also
means successful submission should not be mistaken for a successful grab.

The earlier missing-client success reporting, custom-format removal intent, and
unconditional super-seeding setter remain present in current source. No fresh
torrent cycle has occurred to re-evaluate those torrent decisions. Findings below
describe the original evidence window.

Read-only investigation of the running deployment's decision logs, current source,
and SQLite catalog. Main evidence window: **08:24–09:26 Europe/Malta**. Log timestamps
are local; embedded timestamps ending in `+00:00` are UTC (one hour earlier).
Existing working-tree changes were left intact. No torrent actions, search commands,
configuration changes, or restarts were issued during this investigation.

## Findings, in priority order

### 1. qBittorrent became unavailable around 08:50

The host and workers connected to `qBit-General` at 08:24. The host still received a
successful version response at 08:50:01, then torrent reads began failing around
08:50:04. Later attempts explicitly report `Socket error=ConnectionRefused` for
`192.168.0.68:8080`. All seven workers' next scheduled torrent cycles around 09:24
failed their client health checks and reported `No qBittorrent client available`.

This blocks torrent inspection, import orchestration, and removal. Searches continue
because Arr connectivity and search scheduling are separate. A successful Arr search
submission is not evidence that its download client accepted a new torrent.

Evidence: `.config/logs/torrentarr-20261103.log` around lines 1003439–1003525;
`.config/logs/worker-Sonarr-1080-20261103.log` around lines 404807–404813.
The database also contains active client-connection, protection-inspection, and
maintenance-inventory issues for this client. The local Torrentarr container is
running and healthy; qBittorrent is not a container on this Docker host.

**Next action:** restore/check the qBittorrent Web API on the configured remote
endpoint. The logs establish connection refusal, not why that service stopped
listening; a remote service failure, port change, or network rejection remains
unconfirmed.

### 2. Unavailable torrent processing is reported as successful

`TorrentProcessor.ProcessTorrentsAsync` logs the missing-client warning and returns
normally (`TorrentProcessor.cs:131–136`). The worker then updates `LastSuccess`,
reports a healthy torrent loop, and logs `outcome="success"`
(`Torrentarr.Workers/Program.cs:446–450`). This happened in all seven 09:24 cycles.

The worker then takes its normal sleep. With the current `LoopSleepTimer=3600`,
logs show roughly 3,571 seconds until the next cycle. Without an intervening wake,
configuration change, or restart, restoration of the remote API can therefore take
nearly an hour to reach worker processing. A healthy container and successful loop
status are misleading during this outage.

**Recommended correction:** distinguish unavailable/deferred processing from a
successful observation, retain the previous success timestamp, and retry connection
recovery sooner than the ordinary torrent interval. Preserve SearchOnly operation
and partial multi-client processing when implementing this.

### 3. Custom-format rejection reaches the seeding-limit gate

Five Sonarr Anime torrents were selected for custom-format cleanup but blocked by
the ordinary seeding-time limit. For example, *Reincarnated as a Sword S01E05*:

- Queue custom-format score: **1,400**; existing episode file: **1,600**.
- Custom-format check: `unmet=True`; H&R obligations explicitly cleared.
- Final removal intent: **SeedingLimit**.
- Final blocker: `seeding.limit`, with 1,079,510 of 1,209,600 seconds seeded
  (about 1.5 days remaining at that observation).

Evidence: `.config/logs/worker-Sonarr-Anime-20261103.log:226900–227041`.
The same pattern affects S01E01, E02, E03, and E04.

The custom-format branch calls `DeleteTorrentFromClientAsync` without an explicit
intent (`TorrentProcessor.cs:619`), so its default `SeedingLimit` applies
(`TorrentProcessor.cs:1003`). `SeedingService.EvaluateRemovalAsync` consequently
requires the regular completed-torrent removal limits. The intent enum currently
has no custom-format rejection intent.

**Effect:** lower-scoring duplicate downloads remain and the diagnostics describe
their blocker as seeding cleanup rather than their actual rejection trigger.
This is a mismatch between the custom-format branch and the final policy boundary,
not evidence of unsafe deletion. Any correction must preserve H&R, minimum age,
protected-state/tag, fresh-inventory, and storage/deletion safeguards; do not simply
bypass the final removal check.

### 4. Tracker reconciliation sends redundant super-seeding mutations

The combined log records **1,281 accepted super-seeding updates** during the window,
including **1,016 from the host**. Examples repeatedly apply `SuperSeedMode=False`.

`SeedingService.ApplyTrackerActionsAsync` sends the setter whenever a configured
tracker matches and `SuperSeedMode` has a value (`SeedingService.cs:1438–1442`).
It does not compare the requested value with `torrent.SuperSeeding` first. This
produces avoidable client writes on repeated reconciliation and amplifies transport
logging. It is not proof that these requests caused the qBittorrent outage.

**Recommended correction:** avoid setters when fresh observed state already matches,
while preserving tracker precedence and conflicting-rule behavior.

### 5. Diagnostic logging is consuming substantial disk space

The 62-minute window produced approximately **215.6 MiB** in `all` plus another
**215.6 MiB** across the per-service files: about **431 MiB** of duplicated file
output. Lidarr and Sonarr Anime account for much of the per-item detail. The log
directory totals **16 GiB**; the filesystem still has about **120 GiB** available.

`ServiceLogSink.cs:44–45` deliberately writes both aggregate and service copies;
line 55 retains seven daily files per stream with no file-size limit. Current
retention bounds days, not bytes. This is a capacity risk, not a present disk-full
incident. Any reduction should retain useful decision evidence, preferably by
removing redundant actions/no-change reporting or bounding storage.

## Search behavior that checked out

The window contains **51 accepted search commands**, with no repeated accepted media
ID within an instance:

| Instance | Accepted commands | Observed explanation |
| --- | ---: | --- |
| Radarr-1080 | 13 | Progressing through quality candidates |
| Radarr-Anime | 13 | Progressing through missing candidates |
| Sonarr-1080 | 13 | Doctor Who specials; specials explicitly enabled |
| Lidarr | 12 | Progressing through missing albums |
| Radarr-4K | 0 | No eligible candidates |
| Sonarr-4K | 0 | No eligible candidates |
| Sonarr-Anime | 0 | No eligible candidates |

The configured submission spacing is 300 seconds, explaining roughly 12 commands
per hour per active instance. `SearchLimit=1` for these Radarr/Sonarr instances is
an active-command limit, not a one-search-per-cycle limit. Capacity deferrals at
`1/1` and deadline yields are expected: accepted items are persisted as searched
and later cycles continue with remaining candidates. No pending Sonarr replacement
retry rows were present in the database snapshot.

The zero-candidate instances match the catalog: monitored Radarr-4K entries have
reasons `None` (46) or `NotAvailable` (7); Sonarr-4K has 9 monitored `NotAvailable`
episodes; Sonarr Anime has 2,146 monitored `None` and 38 `NotAvailable` episodes.
These are consistent with availability filtering, not evidence that eligible work
was silently discarded. The catalog snapshot was taken shortly after the log window.

## Limits

No application code was changed and no tests were run. Findings were checked against
current source, log sequences, configuration scheduling values, read-only database
queries, and local container status. This does not establish successful downstream
grabs/imports, remote qBittorrent service health, or correctness of every torrent
outside the selected window. Older daily logs were not exhaustively audited.
