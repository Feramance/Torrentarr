# WebUI API reference

The Host serves its API on the configured WebUI listener (default port 6969).
qBitrr's pinned OpenAPI contract is an upstream reference; Torrentarr adds its own
routes and changes. The generated curated document combines that reference with
explicit Torrentarr extensions. It is not proof that every upstream Python behavior
is implemented. Runtime endpoint integration tests and this page describe Torrentarr.

## Authentication

`/api/*` always requires the configured Bearer token, even when browser authentication
is disabled. A missing token is generated at startup. Use `Authorization: Bearer <token>`.
GET query-token support follows the configured policy and should be avoided on
shared/logged URLs. `/web/*` follows AuthDisabled; when authentication is enabled,
protected routes require a browser session or accepted Bearer token. It is not
unconditionally public. Login/setup/meta and health have their specific public rules.
Local and OIDC credentials are grouped under Authentication in Config.

## Interactive API

`GET /web/openapi.json` and `/api/openapi.json` return the curated document.
`GET /web/docs` and `/api/docs` redirect to Swagger with that document selected.
Use an API token for `/api` requests. Keep the pinned qBitrr reference distinct from
Torrentarr maintenance/configuration extensions when generating clients.

## System endpoints

GET `/health` reports health. GET `/web/meta` or `/api/meta` returns version/update
and auth flags. GET `/web/status` or `/api/status` reports configured client instances
in qbitInstances; the former default qbit singleton is removed. GET
`/web/torrent-clients` or `/api/torrent-clients` returns client IDs, types, connectivity
and capabilities. All client instances are peers.

## Process endpoints

GET `/web/processes` or `/api/processes` lists Host tasks and real worker heartbeats.
Client connections are shown separately through status/client endpoints. Per-Arr
rows combine torrent/search phase and wake times. POST process restart endpoints
use the returned restart key, not a displayed label. POST `processes/restart_all`
and `arr/rebuild` restart workers and should be deliberate operator actions.

## Log endpoints

Both route prefixes expose GET `logs`, `logs/{name}`, `logs/{name}/download`,
`logs/{name}/search`, and `logs/{name}/stream`. Responses distinguish file metadata,
text ranges, search results and streaming data. Log names are validated. Do not
construct filenames from client-provided paths. The WebUI supports search, filtering,
downloads and live tailing without changing the stored log files.

## Arr view endpoints

Arr catalogs include Radarr movies, Sonarr series/episodes, Lidarr artists/albums/tracks,
and Readarr authors/books. Catalog responses use endpoint-specific pagination,
filters and rollups; the WebUI adapters normalize these contracts. Arr links are
resolved through the validated Arr open endpoint. Thumbnail responses reject HTML
and cache validated image content; an unavailable image does not imply missing media.
See [Arr views](arr-views.md) for workflows and [parity reference](../parity/contract-baseline.md)
for the pinned upstream contract.

## Configuration endpoints

GET `config` returns a redacted document. GET `config/schema` returns the shared
field registry, including collection definitions and protection controls. GET
`config/status` reports saved/accepted/applied revision state. These routes exist
under both `/web` and `/api`.

POST `config` accepts dotted changed leaves and optional renames:

```json
{
  "changes": {
    "TorrentClient.seedbox.Maintenance.ErrorRecovery.Enabled": true,
    "TorrentClient.seedbox.Maintenance.Operations.OrphanScan.ExecutionMode": "manual"
  }
}
```

Partial changes retain unknown saved fields, unrelated values and redacted secrets.
Canonical TorrentClient IDs and legacy qBit names remain supported. Validation errors
return 400. Busy publication or interrupted rename recovery may return 409. A successful
save can include an activation warning: the saved revision is durable but a worker/client
needs attention. Do not repeat an already saved rename. Worker cycles adopt ordinary
changes at safe boundaries. Connection replacements retire the old endpoint.

Storage fields are per client: DownloadPath, FreeSpace rows, and Maintenance.PathMappings.
Legacy global storage keys are migration inputs rather than current controls. See
[configuration migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors)
and [editor workflows](config-editor.md).

## Manage and torrent automation endpoints

The existing `/web/maintenance/*` and `/api/maintenance/*` paths remain supported; Manage is the interface name.

| Method and path suffix | Purpose |
| --- | --- |
| GET maintenance/status | Operation state, authorization, next run, active run |
| GET maintenance/history | Recent runs; optional `limit` |
| POST maintenance/preview | Inspect selected clients and any of the ten operations |
| POST maintenance/apply | Apply a current preview; `authorizeOnce: true` permits one reviewed execution |
| POST maintenance/arm | Deprecated: returns 410; save Automatic mode to enable scheduling |
| POST maintenance/disarm | Deprecated: returns 410; save Manual mode to stop scheduling |
| POST maintenance/run | Run selected enabled Automatic operations |
| POST maintenance/cancel | Request cancellation |
| GET torrent-automation/status | Observed protection, recovery, and tracker issues |

Preview supports saved configuration or draft overrides; draft plans cannot execute. Apply rejects expired, consumed, stale, or unauthorized plans. Configure automatic execution through `Maintenance.Operations.<operation>.ExecutionMode` and its UTC schedule. Manual permits reviewed one-time application. Automatic persists across restarts and requires no restart to activate. Legacy `armed` and `armedOperations` status fields remain false/empty for compatibility. These operations retain their original identifiers.

All configuration controls use the existing config API. A Manage save sends only changed maintenance paths and reports validation, reload, and application status through that API.
