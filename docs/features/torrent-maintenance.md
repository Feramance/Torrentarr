# Torrent maintenance and client instances

Torrentarr accepts legacy `[qBit]` / `[qBit-name]` sections and canonical
`[TorrentClient.name]` sections. Both formats use the same category ownership,
subcategory matching, seeding rules, and stalled-download settings. Instance
names identify torrents alongside their hashes; the same hash on two clients
has independent ignore and import state.

All client instances are equal. Configuration order and the name `qBit` confer no
priority. Status uses `qbitInstances` only, and categories are evaluated for every
client. Hash-only operations reject matches on more than one client and incomplete
lookups. Specify an instance when another client is unavailable.

qBittorrent is the shipped adapter. The client registry accepts independent
`ITorrentClient` implementations supplied by registered factories, but this does
not add support for another client product by itself. Optional actions are gated
by the adapter's capabilities.

## Configuration changes

Saving connection changes removes the old connection before responding. Disabled,
deleted, and placeholder clients are removed from active snapshots. The existing
connection loop retries enabled clients; Arr workers restart with the saved
configuration. A failed replacement stays unavailable instead of using the old
endpoint. A remote request already dispatched before the save may still finish. Old client
references reject subsequent requests; disposal waits for active requests to finish.
Client renames preserve stored passwords, notification tokens, and persisted torrent
state, including import, seeding, and free-space pause markers. Arr renames retain
media and queue associations. Existing recycle directories split by client are
renamed within the same root; change the recycle root in a separate save. Exported
resume-data filenames and historical logs keep their original instance names.

Only one Host may manage a data directory at a time. If Host is running, save configuration
through its WebUI; a separately launched standalone WebUI rejects config/password
writes for that config. Worker cycles also participate in config coordination.

Renames briefly stop workers. Conflicting names or stored destination state are
rejected rather than merged. Active processing can return HTTP 409; retry after
it finishes. Configuration and SQLite changes use a recovery record beside config
(`config.toml.rename.json`). Startup recovers it before normal configuration loading
or worker startup. Do not delete this record after an interrupted save. If config
was externally edited during recovery, startup stops until the expected config is
restored. A post-save activation warning means config was saved but workers or
clients need attention; resubmitting the rename is unnecessary.

## Preview, arm, and apply

Maintenance remains opt-in. Preview first, explicitly arm the selected instances,
then apply the returned plan. Changing configuration invalidates the preview and
disarms maintenance. Apply checks enabled/armed state and the configuration
fingerprint between actions, including unselected clients used for file ownership.
Disarming stops subsequent actions.

Apply reevaluates each action from current torrent state. This includes scope
exclusions, minimum age, H&R obligations, tracker state, policy priority, minimum
seed count and seeding time, and paused/completed state for repair. An action that
is no longer eligible is skipped; it is not replaced with a different action.
Skipped actions and failures are recorded in maintenance events and run history.

H&R uses the same decision as lifecycle deletion: category and Arr settings,
matched tracker overrides, partial-download thresholds, tracker update buffer,
and the supported dead-tracker bypass.

## File ownership and deletion

New destructive maintenance requires validated local path mappings and complete
file ownership across every configured client with a real endpoint. A disabled
client still has unknown remote ownership and blocks destructive maintenance;
disabling management does not mean its torrents or shared files disappeared.
An unavailable client,
failed enumeration, unmapped file, or unsupported file enumeration blocks
ownership-dependent changes. HTTP errors are failures, not empty inventories.
An empty successful torrent list is valid.

Shared content is preserved: removing one owning torrent removes its client
record without deleting files still owned by another torrent. Maintenance does
not use the unmapped content-deletion fallback retained for legacy lifecycle
cleanup. Existing failed-category lifecycle cleanup remains compatible when
maintenance mappings and recycling are absent.

Before each file-moving or content-deleting action, apply re-enumerates every required client's torrent
files and trackers, including owners whose top-level torrent metadata is unchanged.
A failed refresh cannot authorize a move using an older inventory. Non-destructive
operations refresh only the current torrent on its owning client, including its
files and trackers, before checking eligibility. Retention cleanup rechecks the
recycled files directly. Initial inventory collection still covers all required clients; candidate
snapshots never authorize content movement or deletion.

Polling cannot make client updates and filesystem moves atomic. Avoid concurrent
external file renames during maintenance: a change after the final ownership check
can still race a filesystem operation.

## Free-space pause markers

The free-space manager resumes only downloads carrying its own pause marker.
Manually paused downloads remain unmarked even when disk space is low. If a
resume succeeds but marker cleanup fails, the manager attempts to pause the
same torrent again without using the cancelled request token. Active downloads
retry stale-marker cleanup on later passes, including when space is still low.

## Recycling and cancellation

Recycling pauses active torrents and confirms they have stopped before moving
content. It validates sources before moving content and removes the torrent only
after moves succeed. Failed validation, cancellation, exceptions, and rejected
client removal trigger rollback of completed moves. Rollback runs independently
of the cancelled request and does not overwrite or delete a recreated source.
After successful rollback, a torrent paused by recycling is resumed when removal
was rejected or never attempted. An uncertain removal outcome leaves it paused.
Delete requests are not retried automatically: a lost response, timeout, or
retryable HTTP error may mean the server already removed the torrent record.

If restoration fails, the run reports the original and recycled paths. Preserve
those recycled files and recover them manually before retention cleanup. A
transport error during torrent removal has an uncertain remote outcome; the run
restores content where possible and reports the uncertainty rather than retrying
destructive removal automatically.

Rollback covers observable failures and cancellation, not a process crash or
power loss. There is no persistent transaction journal.

## Free-space failure handling

Each client contributes one complete torrent list. Failed, unavailable, or disabled
clients make the pass incomplete: healthy downloads can still be paused, but no
download resumes. Active downloads outside the managed category also reserve
space. Planned resumes wait until all protective pauses succeed. A failed or
rejected pause withholds resumes until a later successful pass. Clients without
tag support use instance-scoped SQLite pause markers.
