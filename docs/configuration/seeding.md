# Seeding configuration

Seeding policies control ordinary removal and rate limits. Hit & Run protection is
a separate prerequisite: meeting an ordinary removal limit never overrides an unmet
or unknown tracker obligation. Client counters are estimates and cannot guarantee
tracker-side credit or uninterrupted service.

Every torrent removal and referenced-file cleanup must pass all applicable protections.
An explicit `RemoveTorrent = -1` (Never remove) at any applicable client, category,
Arr, or matching tracker scope blocks cleanup as well as ordinary seeding removal.
Omitting the setting inherits. Cleanup also stays disabled when no applicable rule enables removal.
Completed torrents must satisfy the effective ratio/time removal policy, including its
configured AND/OR combination and any legacy minimum seeding requirements. Incomplete
torrents must pass minimum age and stalled grace: `StalledDelay = -1` holds them indefinitely,
`0` adds no stalled grace, and positive values require that many idle minutes.
Custom-format failures, failed categories, missing files, Arr errors, and maintenance actions
cannot bypass these checks. HnR always retains its own veto. Changed torrent activity,
category, location, or re-addition invalidates an earlier removal decision.

## Configuration structure

Each torrent client has its own `CategorySeeding`. Configure tracker rules under
**Settings → Trackers** or as top-level `[[Trackers]]` entries in TOML.

Each rule requires exactly one nonempty list of instance IDs. Use `ClientIds`
for rules shared by torrent clients and `ArrInstances` for rules scoped to
integration categories. Each selected integration uses its existing torrent-client
scope. Arr overrides take precedence for ordinary tracker behavior, and every
applicable protection obligation still has to clear.

```toml
[TorrentClient.seedbox]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080

[TorrentClient.seedbox.CategorySeeding]
MaxUploadRatio = -1.0
MaxSeedingTime = -1
RemoveTorrent = -1
HitAndRunMode = 'disabled'

[[Trackers]]
ClientIds = ["seedbox"]
Name = 'TorrentLeech Power User'
URI = 'https://tracker.torrentleech.org'
Priority = 0
Protection = { Policy = 'torrentleech-power', AllowClientRatioClearance = false, AdditionalSeedHours = 6.0, AccountingGraceMinutes = 60, RecoveryReserveHours = 6.0 }

[[Trackers]]
ClientIds = ["seedbox"]
Name = 'TorrentLeech alternate host'
URI = 'https://tracker.tleechreload.org'
Protection = { Policy = 'torrentleech-power', AllowClientRatioClearance = false }

[[Trackers]]
ClientIds = ["seedbox"]
Name = 'OnlyEncodes'
URI = 'https://tracker.onlyencodes.cc'
Protection = { Policy = 'onlyencodes', AllowClientRatioClearance = false, FulfillmentHours = 168.0, DeadlineStart = 'added', AdditionalSeedHours = 6.0, AccountingGraceMinutes = 60, RecoveryReserveHours = 6.0 }
```

Replace the example tracker hosts with the hosts actually reported by your client;
this file does not discover tracker accounts or rules. Configure every reported private tracker alias, including alternate TorrentLeech hosts; an unconfigured alias preserves protection. A host matches itself and
subdomains, never unrelated suffixes. New protection is opt-in by adding Protection.
Before enabling automation, preview its decisions. Adding a policy begins conservative
observations; it does not invent past tracker credit.

## Tracker protection

| Tracker | Completed torrent clearance | Partial download | Fulfillment deadline |
| --- | --- | --- | --- |
| TorrentLeech Power User | Eight days of seeding OR tracker-recorded ratio 1.0 | At least 10% downloaded requires ratio 1.0; elapsed time cannot clear it | Eight days is a minimum, not a deadline |
| OnlyEncodes | 48 hours after completion, regardless of ratio and freeleech | Above 10% remains protected; incomplete downloads cannot clear by ratio/time | User-supplied text says both 198 hours and seven days; default warnings use 168 hours from start, awaiting confirmation |
| Unknown private tracker | Preserve and report until configured | Preserve and report | Unknown |

TorrentLeech rules are documented in its [HnR rules](https://wiki.torrentleech.org/doku.php/hnr)
and [user classes](https://wiki.torrentleech.org/doku.php/user_classes). Freeleech
remains protected. OnlyEncodes rules above are supplied by the account owner; its
0.4 overall account-ratio requirement is separate from per-torrent seed-time clearance.
A tracker that is configured with `HitAndRunMode = 'disabled'` and no `Protection`
has no clearance obligation and is treated as eligible. Only a private tracker with
no matching configuration stays `unknown` and is preserved.

### Protection controls

Each tracker row may have a Protection table. New tracker policies use the following:

| Key | Default and meaning |
| --- | --- |
| Policy | `custom`; also `torrentleech-power` and `onlyencodes` |
| MinimumSeedHours | 48 hours for custom; presets enforce 192/48 hours respectively |
| MinimumRatio | 1.0 for custom; TorrentLeech preset uses 1.0 |
| AllowClientRatioClearance | false; client ratio alone does not prove tracker acceptance; OnlyEncodes never clears by ratio |
| PartialPolicy | `preserve`; custom may use `ratio`; presets enforce their rules above |
| DownloadThresholdPercent | 10%; presets enforce their 10% thresholds |
| ThresholdInclusive | true for custom/TorrentLeech; OnlyEncodes is strictly above 10% |
| FulfillmentHours | absent means no deadline for custom/TorrentLeech; OnlyEncodes defaults to an unconfirmed 168-hour warning window |
| DeadlineStart | `added`; optional `completed` for a configured custom deadline |
| AdditionalSeedHours | Six additional observed hours after the mandatory minimum |
| AccountingGraceMinutes | 60 minutes of consecutive healthy accounting evidence after requirements are met |
| RecoveryReserveHours | Six hours reserved before the fulfillment boundary for recovery |

Duration/ratio values must be finite and non-negative; a fulfillment window must be
positive. Zero extra margin, accounting grace or reserve disables that allowance.
Minimum time, extra seed margin, accounting grace, recovery reserve, cleanup delay,
and fulfillment deadline are different controls. Zero accounting grace still requires two distinct qualifying observations. Grace resets after
an unhealthy observation or a gap longer than twice the configured scan interval.
Extra seeding after fulfillment is permitted.

### Local observations and uncertainty

Observations are durable by client, torrent and tracker/policy. Credit only accrues
between healthy, complete, actively seeding observations no more than 330 seconds
apart, bounded by both wall time and client counter increases. Paused, queued,
checking and errored periods earn no local credit. Counter rollback or changed
completion resets accumulated credit; outages and restart gaps receive no invented
credit. A new/different policy starts conservative observations. The remembered
maximum downloaded percentage prevents rechecks from erasing an existing obligation.

Decisions are protected, eligible or unknown, with reasons and local-estimate labels.
Every applicable obligation is evaluated, including multiple trackers and differing
client/Arr policies for the same tracker. Priority cannot discard another obligation.
Missing tracker health or requirements preserves protection. There is no dead-message
HnR bypass. No ratio infinity, freeleech flag, torrent age or deadline expiry proves clearance.

### Deadline boundaries

Latest recovery start equals fulfillment deadline minus remaining mandatory seeding,
accounting allowance and recovery reserve. Warnings start within 24 hours of that
boundary; at the boundary the risk is critical. If uninterrupted mandatory seeding
plus accounting would already miss the deadline, risk is unachievable. Incomplete
downloads expose a latest completion time; unknown ETA is reported as uncertainty.
A missed deadline preserves protection and triggers reporting/recovery.

Protection checks normally run at least every five minutes when enabled, with
targeted one-minute inspection for deadline or native stop-limit risk. Monitoring runs independently of long maintenance jobs. Outages may delay these checks;
failures back off and preserve protection. These checks cannot guarantee service
availability or tracker-side acceptance. H&R only restricts torrent/content deletion and recycling. Imports, pause/resume, tracker/category changes, and repair follow their configured policies and ownership checks independently.

Per-torrent native ratio, seed-time and inactive seed-time stop limits can interrupt
protected seeding. Protection temporarily overrides conflicting limits and records
their original values, restoring them after clearance if the user has not changed
them. Private torrents are reconciled even when no tracker policy is configured, so
qBittorrent cannot natively remove an unmatched private torrent before review.
Global client preferences are unchanged. See the [official qBittorrent
share-limit contract](https://github.com/qbittorrent/qBittorrent/wiki/WebUI-API-%28qBittorrent-5.0%29#set-torrent-share-limit).

## Global seeding settings

These are per-client CategorySeeding defaults, with Arr SeedingMode and tracker
ordinary limit overrides where configured. CategorySeeding.Categories can narrow a
category-specific override; matching subcategories requires MatchSubcategories.
Unset values inherit; an explicit negative sentinel preserves the documented disable
semantics. Category names remain case-sensitive identities.

### DownloadRateLimitPerTorrent

KiB/s; -1 means no managed rate setting. Positive values are translated to bytes/s.

### UploadRateLimitPerTorrent

KiB/s; -1 means no managed rate setting. These are per-torrent, not global preferences.

### MaxUploadRatio

Ordinary removal threshold, -1 disables. It does not prove tracker clearance.

### MaxSeedingTime

Ordinary removal time in seconds, -1 disables. Duration strings supported by the
loader are converted to seconds. Stalled-upload timing for ordinary removal is
separate from mandatory completion-based protection observations.

### RemoveTorrent

-1 never; 1 ratio; 2 time; 3 either; 4 both. HnR protection still gates removal.

### Retirement guards

Two optional controls apply to automatic retirement of completed torrents after
the usual ratio or time limits. Tracker protection still applies. Both controls
default to zero, which disables them independently.

```toml
[qBit.CategorySeeding]
MinimumReportedSeeders = 3
MinimumUploadInactivitySeconds = 3600
```

`MinimumReportedSeeders` uses qBittorrent's reported swarm seeder count. A
reported zero is a count. Missing, unsupported, or stale values hold retirement.
`MinimumUploadInactivitySeconds` uses persisted observations of the uploaded
payload counter. A fresh unchanged counter must span the configured window. [qBittorrent's torrent status serialization](https://github.com/qbittorrent/qBittorrent/blob/master/src/webui/api/serialize/serialize_torrent.cpp), [libtorrent's payload counters](https://libtorrent.org/reference-Torrent_Status.html#all-time-upload-all-time-download)
Uploads, counter resets, identity changes, and missing observations restart the
window. A gap longer than twice the client scan interval breaks continuity.

Open **Settings → Torrent Clients**, edit the client, and find both controls in
**Seeding and Hit & Run**. Use the same keys in `TorrentClient.<id>.CategorySeeding`. Category overrides
inherit the client defaults when blank. Enter zero in an override to disable
that guard for the category. The client editor shows the resulting requirements.
The Host retains inactive observation rows for up to seven days. Disabling the
inactivity guard stops recording samples for that policy. The normal scan gap
rule applies when it is enabled again. Debug logs show the latest evidence,
observation age, and threshold.

### RemoveDeadTrackers

Legacy compatibility flag. True reports matching tracker failures and preserves
the torrent; direct dead-tracker torrent deletion is disabled. False permits
tracker URL removal through RemoveTrackerWithMessage, subject to fresh protection.
Ordinary tracker handling preserves and reports failures. Maintenance recovery may recycle content only after explicit configuration, confirmed repair, sustained failure, and all protection checks.

### RemoveTrackerWithMessage

Tracker-message matching for ordinary tracker handling. Removing a configured
tracker remains subject to fresh protection checks. Private torrents keep the
configured tracker URL and their last remaining announce URL even when a message
matches; removing either would destroy the only way to re-announce. The same protections apply to explicit `RemoveIfExists` actions.

## Per-tracker configuration

### Tracker inheritance

For ordinary limits and tracker actions, Arr-level entries replace client entries
on normalized host collisions. Protection evaluates all applicable obligations
rather than choosing the highest-priority clearance rule.

### Tracker fields

Name is a display label. URI identifies the tracker host. Priority selects ordinary
handling precedence, with configuration order breaking ties; it is not permission to discard another tracker obligation.
MaximumETA is an ordinary slow-download threshold. DownloadRateLimit/UploadRateLimit
are per-torrent KiB/s. MaxUploadRatio/MaxSeedingTime override ordinary limits.
AddTrackerIfMissing, RemoveIfExists, SuperSeedMode and AddTags remain explicit
tracker actions. `SortTorrents` applies only to managed torrents matching an enabled
rule by announce host or configured tag. The tracker editor leaves optional overrides
inherited until configured.

### HitAndRunMode

- `and` requires every configured ratio and time condition.
- `or` accepts either configured condition.
- `ratio` requires a positive ratio target and uses the partial-download ratio for incomplete torrents.
- `time` requires a positive seed-time target. Incomplete torrents above the download threshold stay protected until completion.
- `disabled` turns off the Hit & Run rule.

Enabled rules confirm qualifying client readings with healthy tracker observations
and accounting grace. The default grace is 60 minutes. A tracker using the
Hit & Run thresholds can configure its own grace. The time condition uses
client-reported seeding time minus the tracker update buffer.

### MinSeedRatio

Legacy minimum client-estimated ratio; finite counters are required. Missing or
unhealthy tracker evidence cannot clear a legacy rule.

### MinSeedingTimeDays

Legacy minimum seeding days. `MinSeedingTime` is an accepted legacy alias in tracker
rows. New policies use MinimumSeedHours instead.

### HitAndRunMinimumDownloadPercent

Legacy threshold before an obligation applies. New presets enforce their tracker
thresholds and preserve a previously crossed threshold.

### HitAndRunPartialSeedRatio

Legacy partial-download ratio requirement. Partial time cannot replace a preset's
mandatory ratio or completion requirement.

### TrackerUpdateBuffer

Legacy accounting allowance in seconds, preserved when explicitly configured.
New policies expose AccountingGraceMinutes and AdditionalSeedHours independently.

## Native stop limits

When protection is explicitly configured, conflicting managed per-torrent stop
limits are recorded and temporarily disabled to preserve seeding. After clearance,
previous values are restored only if the unlimited values still belong to Torrentarr;
later user changes are retained. Private torrents without a matching tracker policy
are also treated as unresolved and keep their native stop limits disabled until
configuration or manual review resolves them. Global qBittorrent preferences are
never changed.
A stopped torrent with unknown pause ownership is reported rather than assumed safe
to resume. Review client global stop/queue settings separately.

## Troubleshooting

Before changing a policy, inspect tracker messages, privacy, completion timestamp,
current state, client counter resets, and the labeled protection evidence. Roughly
seven days does not satisfy the TorrentLeech Power User eight-day time route.
OnlyEncodes mandatory time begins at completion, not when a torrent was added.
Client upload/download counters and screenshots cannot establish the exact cause
of a tracker warning. Do not remove/re-add a torrent to try to erase an obligation.

See [Maintenance recovery](../features/torrent-maintenance.md) and
[maintenance configuration](maintenance.md) for safe repair and storage controls.
