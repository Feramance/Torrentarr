# Torrent maintenance and client instances

Torrentarr accepts legacy `[qBit]` / `[qBit-name]` sections and canonical
`[TorrentClient.name]` sections. Both formats use the same category ownership,
subcategory matching, seeding rules, and stalled-download settings. Instance
names identify torrents alongside their hashes; the same hash on two clients
has independent ignore and import state.

qBittorrent is the shipped adapter. The client registry accepts independent
`ITorrentClient` implementations supplied by registered factories, but this does
not add support for another client product by itself. Optional actions are gated
by the adapter's capabilities.

## Configuration changes

Saving connection changes removes the old connection before responding. Disabled,
deleted, and placeholder clients are removed from active snapshots. The existing
connection loop retries enabled clients; Arr workers restart with the saved
configuration. A failed replacement stays unavailable instead of using the old
endpoint. A remote request already dispatched before the save may still finish.

## Preview, arm, and apply

Maintenance remains opt-in. Preview first, explicitly arm the selected instances,
then apply the returned plan. Changing configuration invalidates the preview and
disarms maintenance. Apply checks enabled/armed state and the configuration
fingerprint between actions. Disarming stops subsequent actions.

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
file ownership across every enabled, configured client. An unavailable client,
failed enumeration, unmapped file, or unsupported file enumeration blocks
ownership-dependent changes. HTTP errors are failures, not empty inventories.
An empty successful torrent list is valid.

Shared content is preserved: removing one owning torrent removes its client
record without deleting files still owned by another torrent. Maintenance does
not use the unmapped content-deletion fallback retained for legacy lifecycle
cleanup. Existing failed-category lifecycle cleanup remains compatible when
maintenance mappings and recycling are absent.

Apply keeps an inventory only for the current run. Before each destructive action
it polls all required clients for membership and location changes. New or relocated
torrents receive fresh file enumeration, and torrent-removal candidates receive
fresh file and tracker checks. Unchanged non-candidate file ownership is reused.
A failed refresh cannot authorize a move using an older inventory.

Polling cannot make client updates and filesystem moves atomic. External file
renames that do not change exposed ownership metadata cannot be detected by
inventory reuse; avoid concurrent file renames during maintenance. An adapter
that cannot expose ownership changes must force fresh file enumeration.

## Recycling and cancellation

Recycling validates sources before moving content and removes the torrent only
after moves succeed. Failed validation, cancellation, exceptions, and rejected
client removal trigger rollback of completed moves. Rollback runs independently
of the cancelled request and does not overwrite a recreated source.

If restoration fails, the run reports the original and recycled paths. Preserve
those recycled files and recover them manually before retention cleanup. A
transport error during torrent removal has an uncertain remote outcome; the run
restores content where possible and reports the uncertainty rather than retrying
destructive removal automatically.

Rollback covers observable failures and cancellation, not a process crash or
power loss. There is no persistent transaction journal.
