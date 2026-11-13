# Configuration editor

The **Settings** sidebar group edits the running Host's TOML through its configuration API.
Open **General**, **Interface & Access**, **Authentication**, **Torrent Clients**, or **Integrations**. This navigation change requires no upgrade steps for existing config files or databases. Integrations has separate Radarr, Sonarr, Lidarr, and Readarr sections,
including empty types. Torrent Clients groups legacy and canonical entries by
configured type, with qBittorrent first. Each torrent-client section shows its
instance count and an **Add Instance** button that uses that type's defaults.
Integrations instead has one **Add Instance** button above its sections; it
opens an integration-type picker and creates the chosen type's next instance.

Instance rows show address, category scope, enabled state, and unsaved changes;
client rows also show scan intervals, while integration rows show search/processing
mode. Both instance sections start with their overview; select a row or add an instance
to open its inline editor. Returning to the section hides the editor while retaining
its draft. Field search appears only while an editor is open. Editors and Save
controls use the page scroll, without a nested modal scroll area.

Use **Find a setting in this section** to search field names and descriptions.
Search filters the visible field groups. Instance row markers and the Unsaved changes
badge identify local drafts. Switching sections preserves those drafts; saving
publishes only the selected section. Leaving Settings and Maintenance prompts before discarding unsaved configuration or maintenance edits.

The editors preserve password redaction, validation, connection testing, tooltips,
and specialized collection editors. Destructive changes still require confirmation.

Ordinary successful saves report **Configuration saved**. Pending application,
errors, and warnings remain visible. Only explicit restart requirements returned
by the save API open a **Restart now / Later** confirmation listing affected
components. Deferral or restart failure keeps a pending notice with retry; a failed
save never triggers a restart.

## Section organization

| Editor | Ordered sections |
| --- | --- |
| Torrent client | General; Connection; Torrent scope; Download location; Free-space monitors; Seeding and Hit & Run; Trackers; Shared Settings shortcut |
| Arr instance | General; Connection; Categories and torrent clients; Entry search; Quality profiles; Request integrations; Torrent handling; Seeding; Trackers |
| General | Logging; Processing and categories; Connectivity; Updates and ffprobe; Worker recovery |
| Interface & Access | Listener and URL settings; Proxy and exposure; API access; Appearance and live display |
| Authentication | Access controls; Local login; OIDC |

Host, Port, Username, Password and Skip TLS Verify are consecutive in a client
Connection section. Arr URI, API key, TLS and connection testing stay together;
request integration credentials and their TLS settings are grouped. Each exposed
field appears once in its editor. Unrecognized saved fields survive ordinary
partial updates. Blank redacted password placeholders retain the stored secret;
use the password control to explicitly replace a credential.

## Torrent clients and storage

All client instances are peers. Legacy qBit names and canonical TorrentClient
sections share the same editor. qBittorrent is the shipped adapter; another product
requires an installed client factory and its capabilities.

DownloadPath is a client-visible default root. Existing category save paths are
preserved. PathMappings translates client paths to Torrentarr-visible local paths;
it never creates mounts or moves files. FreeSpace is a per-client collection of
local monitor Path, Enabled and Limit. Add/remove rows in the specialized editors.
Limit accepts bytes or K/M/G/T suffixes. Local paths in Docker are container paths.

```toml
[TorrentClient.seedbox]
Type = 'qbittorrent'
Host = 'localhost'
Port = 8080
DownloadPath = '/downloads'

[[TorrentClient.seedbox.Maintenance.PathMappings]]
ClientPath = '/downloads'
LocalPath = '/data/downloads'

[[TorrentClient.seedbox.FreeSpace]]
Path = '/data/downloads'
Enabled = false
Limit = '10G'
```

Old global CompletedDownloadFolder, FreeSpaceFolder, FreeSpace and AutoPauseResume
are migration inputs. They are no longer current global controls. Review disabled
monitors after ambiguous multi-client migration. See [storage migration](../configuration/config-file.md#client-download-paths-and-free-space-monitors).

## Maintenance tools

Open **Maintenance** from the sidebar. The torrent-client editor shows a compact summary and an **Open Shared Settings** shortcut selecting that saved client. Save a new or renamed client before using it. The shortcut preserves other unsaved client edits.

The ten tools are grouped under **Recovery**, **Organization**, and **Storage**. Tool settings and actions are visible on each category page. Preview results stay with the category that created them. **Shared Settings** holds scope, mappings, recycling, and safeguards. **Recent Runs** shows run history. Save and Discard affect only the selected client's maintenance settings.

See [Maintenance configuration](../configuration/maintenance.md) and [workflow](../features/torrent-maintenance.md).
