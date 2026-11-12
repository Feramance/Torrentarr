# Console and statistics

Torrentarr uses grouped navigation for monitoring, libraries and clients, and administration. Display controls retain the saved theme and density preferences. Small screens use the Menu drawer.

Transfer activity appears inside Torrent Clients. It records whole-client download/upload rates, including traffic outside monitored categories. Collection runs on the server even with the browser closed. Scan intervals under 60 seconds also set the transfer polling interval. At intervals of 60 seconds or longer, transfer polling runs once per UTC minute. History is stored in minute buckets, survives restarts, and is kept for seven days in `torrentarr.db`. Samples are pruned hourly. No history is backfilled.

Choose 1 hour, 24 hours (default), or 7 days. Graphs use completed 1-, 5-, or 30-minute intervals. Each client has its own series; absent or incomplete intervals break the line. Coverage and the latest complete interval are labeled. Disabled clients are not sampled. Disconnected clients and unsupported metrics produce gaps rather than zero traffic. Database notifications refresh visible statistics independently of Live Arr polling; the existing Refresh button also refreshes statistics. Changing range or client fetches that selection once.

Monitored-category activity uses full inventories before the 500-row display cap and deduplicates overlapping categories. It is separate from whole-client transfer history and text search. Unavailable client inventories are excluded and partial coverage is labeled.

Library bars report available and missing **monitored** movies, episodes, albums, or books from the full selected catalog, independently of page and text filters. Monitoring is a separate count. Manage summarizes operation outcomes in the latest 20 displayed runs.

The `/web/statistics/transfers` and `/web/statistics/catalog` endpoints follow the configured WebUI authentication policy; their parameters and payloads are documented in OpenAPI. The history table is a Torrentarr extension and does not change qBitrr-compatible tables or TOML configuration.

The logo is true vector artwork with transparent negative space, traced and shaded from the PNG reference. WebUI and documentation use the SVG; platform icons are raster exports.

## Arr activity

Processes uses aligned instance rows. Each Arr Torrent activity target opens its category in Torrent Clients, selecting a unique matching client or all clients when the category is shared. Old torrent searches are cleared. Search activity and restart actions remain separate. Wiki and GitHub links are available in both navigation footers.

The two inline Arr graphs show queue depth and confirmed completed activity. Choose 1h, 24h (default), or 7d and group by Arr type (default) or individual worker. Radarr is orange, Sonarr teal, Lidarr green, and Readarr violet; worker colors are stable and series are distinguished by color only. Legends toggle series. Hover or focus a plotted point, touch it, or use the keyboard-accessible time slider to inspect exact timestamps and values. Dedicated sampled-data tables are omitted.

Queue observations come from fresh worker heartbeats every minute. Each worker polls the Arr download queue total once per minute with a 10-second deadline and reports it in its heartbeat; stale, stopped, or unavailable workers leave gaps. Graphs use per-minute buckets for 1h and hourly buckets for 24h and 7d. Queue buckets are averages rounded to whole items; type totals sum worker averages and disclose incomplete coverage. Completion buckets sum confirmed observations, separately for searches and torrent-import batches. Partial coverage still plots and labels its observed sample count; an interval with no observations is a gap, never a fabricated zero.

Only Torrentarr-submitted commands are tracked. Search requests count after Arr reports successful completion. Import requests count only with a correlated positive download-import history event dated after submission. Accepted commands, completed scans with no import, queue disappearance, tags, and failed/cancelled work do not count. Multiple files in one torrent batch and retries sharing confirmation evidence do not inflate the count. Counts use the time confirmation was recorded. Pending records survive worker restarts; unconfirmed work expires after seven days. There is no historical backfill.

The server reconciles only instances with pending work, at most at each client’s configured `TorrentScanIntervalSeconds`, using the existing API-version and TLS settings. Telemetry failures do not change search/import/protection behavior. Additional Torrentarr-specific tables retain samples and operation evidence for seven days; pruning runs hourly. Standalone WebUI cannot supply worker queue observations when no supervisor is present.

`GET /web/statistics/arr-activity?range=24h&worker=Radarr-example` follows WebUI authentication and returns worker identity/type, timestamped values, queue/completion coverage, and expected sample counts. Omit worker for all configured workers and retained history. Existing transfer-history endpoints are unchanged.
