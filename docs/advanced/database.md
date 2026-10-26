# Database schema

Torrentarr uses SQLite through EF Core. The file is `torrentarr.db`; do not confuse
it with qBitrr's database filename. Data and logs normally live beside the resolved
config.toml; TORRENTARR_CONFIG paths under Docker /config use /config as data root.
Only one Host may own a data directory.

## Compatible media and queue tables

The existing tables retain their qBitrr-compatible contracts: torrentlibrary,
moviesfilesmodel, moviequeuemodel, seriesfilesmodel, episodefilesmodel,
episodequeuemodel, artistfilesmodel, albumfilesmodel, albumqueuemodel, trackfilesmodel,
authorfilesmodel, bookfilesmodel, bookqueuemodel, filesqueued and searchactivity.
Client identity scopes persisted torrent state; equal hashes on different clients
are distinct. Arr/client renames preserve applicable media, queue and pause state.
The EF model and ManualSqliteMigrations are the source of truth; schema parity tests
protect the compatible table names.

## Torrentarr maintenance state

The additive `torrentarr_maintenance_state` table has Key and JSON Data columns.
It stores recovery incidents, completion-based protection observations, owned tags
and routine pauses, native limit restoration records, scheduled-minute claims and
scan cursors. Keys include client/torrent identity and state kind; protection
observations also include tracker/policy identity. This table is separate from the
compatible media/queue contracts.

Missing observations begin conservatively. Invalid persisted JSON raises an error
rather than discarding protection evidence. Restarts do not credit unobserved seed
time. Daily cleanup opt-in lives in config; durable daily claims prevent duplicate
runs. Recycle records preserve committed timestamps and unresolved recovery paths. Do not edit/delete maintenance state to try to
clear a tracker obligation.

## Concurrency and migrations

WAL mode, busy handling and scoped contexts support Host/worker access. Recovery
submission claims are transactional to avoid duplicate requests. Rename operations
coordinate SQLite and config through a journal beside config; startup recovers an
interrupted journal before normal processing. Do not delete it after interruption.
Manual additive migrations create required maintenance state for existing installs.

## Backup and rollback

Back up config and the database together using a consistent SQLite backup or stop
Torrentarr before copying the database and WAL state. Preserve recovery journals.
Rollback retains an additive table but older binaries do not implement its new
protection, recovery or scheduling behavior. Retain a compatible config/database
snapshot and inspect changed automation before resuming an older build.

## Maintenance and troubleshooting

Use built-in health/backup procedures before manual VACUUM or repair. Do not modify
compatible tables while workers run. A missing mount, locked database or corrupt
maintenance record must be investigated; none of these failures proves a torrent
is safe to remove. See [database troubleshooting](../troubleshooting/database.md),
[maintenance](../features/torrent-maintenance.md), and [architecture](architecture.md).
