# Processes view

Processes shows what Torrentarr is doing, grouped by library type, torrent client,
and host task. Each Arr instance has one isolated worker with independent Search
and Torrent loops.

## Activity cards

- **Searches / items searched:** numeric results from the last completed search
  pass, with its completion timestamp. Running or sleeping activity appears below.
- **Managed torrents / downloading / seeding:** counts from complete client
  inventories, including owned subcategories when configured. **Arr queue** is the
  latest local queue count.
- **Torrent clients:** managed torrent, downloading, and seeding totals across
  owned categories. These totals use full snapshots, even when a displayed list
  is truncated.
- **Free Space Manager:** currently marked pauses and paused/resumed actions from
  the last allocation pass. Each storage monitor shows free space, reserve,
  committed downloads, remaining allocation, and any reason resumes are blocked.
- **Failed / Recheck:** successfully handled torrents from the last completed
  pass, current category inventory, and monitored category count.

A successful empty observation is **0**. Before the first observation, a dash and
pending message appear. Failed observations retain the last successful metrics
with an error or stale timestamp; they do not become zeroes.

Small status dots and **Issues** expose operational problems. Worker counts are
not headline metrics. Use an instance's **Actions** menu to restart its worker;
restarting affects both loops. Host tasks do not have individual restart actions.
The toolbar also provides global restart and Arr rebuild controls.

## Refresh behavior

Database commits and runtime activity changes notify visible views through the
authenticated statistics stream. Database change detection checks SQLite once a
second, including commits from separate worker processes. Reconnection fetches
current statistics; hidden views catch up when activated.

Client observations are cached and deduplicated per instance using that client's
`TorrentScanIntervalSeconds`. Aggregate views combine each client's latest
snapshot. Changing the interval applies without restarting Torrentarr. The direct
Arr **Live** setting does not disable database statistics or client snapshots.

Processes also refreshes its visible dashboard every two seconds as a fallback;
this reuses cached client snapshots rather than scanning clients every two seconds.

## Free-space allocation

Torrentarr reserves each active download's remaining bytes above the configured
free-space reserve and allocates the oldest eligible torrents first. Space above
the reserve alone does not guarantee that another download fits. Shared disks
have one budget across clients, and unmanaged active downloads consume that budget.

Only Torrentarr-owned pauses are resumed. Missing client inventories, unmatched
active download paths, unavailable storage, and failed protective actions can
withhold resumes. The card reports these reasons instead of implying that every
paused torrent can safely resume.
