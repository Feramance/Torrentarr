# Seeding configuration

Seeding policies control ordinary removal and rate limits. Hit & Run protection is
a separate prerequisite: meeting an ordinary removal limit never overrides an unmet
or unknown tracker obligation. Client counters are estimates and cannot guarantee
tracker-side credit or uninterrupted service.

Every torrent removal and referenced-file cleanup must pass all applicable protections.
`RemoveTorrent = -1` (Never remove) blocks cleanup as well as ordinary seeding removal.
Completed torrents must satisfy the effective ratio/time removal policy, including its
configured AND/OR combination and any legacy minimum seeding requirements. Incomplete
torrents must pass minimum age and stalled grace: `StalledDelay = -1` holds them indefinitely,
`0` adds no stalled grace, and positive values require that many idle minutes.
Custom-format failures, failed categories, missing files, Arr errors, and Manage actions
cannot bypass these checks. HnR always retains its own veto. Changed torrent activity,
category, location, or re-addition invalidates an earlier removal decision.

## Configuration structure

Each torrent client has CategorySeeding and Trackers. Legacy `[qBit]` / `[qBit-name]`
and canonical `[TorrentClient.name]` use the same keys. Arr torrent seeding settings
can override ordinary handling for that Arr category; client instances remain independent.

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

[[TorrentClient.seedbox.Trackers]]
Name = 'TorrentLeech Power User'
URI = 'https://tracker.torrentleech.org'
Priority = 0
Protection = { Policy = 'torrentleech-power', AllowClientRatioClearance = false, AdditionalSeedHours = 6.0, AccountingGraceMinutes = 60, RecoveryReserveHours = 6.0 }

[[TorrentClient.seedbox.Trackers]]
Name = 'TorrentLeech alternate host'
URI = 'https://tracker.tleechreload.org'
Protection = { Policy = 'torrentleech-power', AllowClientRatioClearance = false }

[[TorrentClient.seedbox.Trackers]]
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
| AccountingGraceMinutes | 60 minutes of healthy accounting evidence after requirements are met |
| RecoveryReserveHours | Six hours reserved before the fulfillment boundary for recovery |

Duration/ratio values must be finite and non-negative; a fulfillment window must be
positive. Zero extra margin, accounting grace or reserve disables that allowance.
Minimum time, extra seed margin, accounting grace, recovery reserve, cleanup delay,
and fulfillment deadline are different controls. Grace alone does not prove acceptance.
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
availability or tracker-side acceptance. Authorized Torrentarr-owned pauses may be
recovered; deliberate user pauses remain blockers unless explicit paused-repair
resume permission was selected.

Per-torrent native ratio, seed-time and inactive seed-time stop limits can interrupt
protected seeding. Explicit protection temporarily overrides conflicting limits and
records their original values, restoring them after clearance if the user has not
changed them. Global client preferences are unchanged. See the [official qBittorrent
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

### RemoveDeadTrackers

Legacy compatibility flag. True reports matching tracker failures and preserves
the torrent; direct dead-tracker torrent deletion is disabled. False permits
tracker URL removal through RemoveTrackerWithMessage, subject to fresh protection.
Ordinary tracker handling preserves and reports failures. Manage recovery may recycle content only after explicit configuration, confirmed repair, sustained failure, and all protection checks.

### RemoveTrackerWithMessage

Tracker-message matching for ordinary tracker handling. Removing a configured
tracker remains subject to fresh protection checks.

## Per-tracker configuration

### Tracker inheritance

For ordinary limits and tracker actions, Arr-level entries replace client entries
on normalized host collisions. Protection evaluates all applicable obligations
rather than choosing the highest-priority clearance rule.

### Tracker fields

Name is a display label. URI identifies the tracker host. Priority selects ordinary
handling precedence; it is not permission to discard another tracker obligation.
MaximumETA is an ordinary slow-download threshold. DownloadRateLimit/UploadRateLimit
are per-torrent KiB/s. MaxUploadRatio/MaxSeedingTime override ordinary limits.
AddTrackerIfMissing, RemoveIfExists, SuperSeedMode and AddTags remain explicit
tracker actions. Their capability/prerequisite checks still apply.

### HitAndRunMode

Legacy `and`, `or`, or `disabled`. `and` requires configured ratio and time;
`or` permits either. Existing explicit buffers and settings are retained. New
Protection presets are recommended for mandatory completion-based requirements.

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
later user changes are retained. Global qBittorrent preferences are never changed.
A stopped torrent with unknown pause ownership is reported rather than assumed safe
to resume. Review client global stop/queue settings separately.

## Troubleshooting

Before changing a policy, inspect tracker messages, privacy, completion timestamp,
current state, client counter resets, and the labeled protection evidence. Roughly
seven days does not satisfy the TorrentLeech Power User eight-day time route.
OnlyEncodes mandatory time begins at completion, not when a torrent was added.
Client upload/download counters and screenshots cannot establish the exact cause
of a tracker warning. Do not remove/re-add a torrent to try to erase an obligation.

See [Manage recovery](../features/torrent-maintenance.md) and
[maintenance configuration](maintenance.md) for safe repair and storage controls.
