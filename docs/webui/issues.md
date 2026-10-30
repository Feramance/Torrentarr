# Operational issues

The **Issues** view groups problems by cause and instance. Its badge counts active groups, not individual log messages or affected torrents.

- **Automation & safety** contains skipped searches, profile recovery, unresolved import ownership, tracker problems, and repair diagnostics.
- **System & services** contains worker failures, connectivity, download storage reserves, synchronization, configuration, database health, and update failures.

Expand a group to see its impact, corrective action, affected subjects, and links to existing settings or logs. Filter by instance, severity, status, or text. Errors appear before warnings. The view refreshes every five seconds while visible; its navigation count refreshes every 15 seconds.

Issues close after a successful observation of the same operation. Repeated failures update the existing issue. Resolved issues remain available for 30 days; unresolved issues do not expire. Removing an instance retires its issues with an explicit configuration-removal reason. Renames preserve issue ownership and history.

A search whose temporary profile cannot be safely prepared is skipped and retried later. Torrentarr records the original profile before changing Arr and preserves that record after uncertain requests. A profile changed manually to an unrelated value is not overwritten: check the original and temporary profiles and reconcile the conflict in Arr. Previously lost original profiles cannot be reconstructed automatically.

Replacement-search details are saved before blocklisting. After a restart or uncertain response, Torrentarr reads the complete Arr queue before continuing. Rejected submissions remain queued for retry. A full command queue causes a normal delay; an unreadable command queue blocks profile changes and search submission and creates an issue. Client connectivity and worker recovery require successful fresh observations. On Windows, starting the update helper does not close an update failure; its successful outcome must be confirmed.

Maintenance execution failures are reported once for the selected set of clients. An aggregate failure does not identify which client failed. Review Manage history for action errors; a successful retry with the same client selection closes the issue. An unrelated successful run does not clear it.

**Issue history unavailable** means SQLite could not be read, not that the application is healthy. Each process retains up to 100 pending issue updates and retries persistence. Pending worker reports are also carried in worker heartbeats. A crash during a database outage can lose pending history. Safety checks do not depend on issue reporting succeeding.

Routine activity, intentional pauses, disabled features, and a full search-command queue are not issues. Existing logs retain full exception details. The Issues view contains plain-language descriptions without credentials or raw upstream responses. Issue reporting does not send external notifications or perform repairs.

The read-only API is available at `/web/issues`, `/web/issues/summary`, and `/web/issues/{id}`, with matching `/api` routes. `/api` requires the existing Bearer token. Lists support `category=automation|system`, `state=active|resolved`, `severity=warning|error`, `instance`, `q`, zero-based `page`, and `page_size` (default 50, maximum 100). Details contain affected subjects and their resolution history. The shared SQLite extension table is created automatically on upgrade; no TOML changes are needed.
