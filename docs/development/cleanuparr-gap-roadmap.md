# Torrentarr roadmap from the Cleanuparr comparison

This roadmap captures features worth developing after the comparison of [Torrentarr PR #563 at `8c70f575`](https://github.com/Feramance/Torrentarr/tree/8c70f575d4e581d9c6f25d9ea0ecdc19dfa0cd08) and [Cleanuparr v2.11.1](https://github.com/Cleanuparr/Cleanuparr/releases/tag/v2.11.1). The goal is to improve Torrentarr's existing qBittorrent and Arr workflows. Recheck each gap against the target branch before starting because the comparison is pinned.

Each work package has a **Ready to ship** checkbox. Check it only when its acceptance items and the shared release criteria pass on the intended release commit. Record evidence beside the package or link its PR. Deferred and excluded work is outside the current release scope.

## Release sequence

| Package | Priority | Release dependency |
| --- | --- | --- |
| 1. Search decisions and preview | P1 | None. Establishes how new search decisions will be shown. |
| 2. Operational issue alerts | P1 | None. Uses existing Issues. |
| 3. qBittorrent stall safeguards | P1 | None. Uses existing torrent policy and removal authority. |
| 4. Proactive search pacing | P1 | Package 1 decision reasons so operators can see why work was deferred. |
| 5. qBittorrent API-key login | P2 | None. |
| 6. Shared file filter lists | P2 | None. |
| 7. Related log links | P2 | Use existing IDs first. Package 1 supplies search detail links when available. |
| 8. Root-aware hardlink auditing | P2 | Ship only if reliable filesystem inspection is feasible on the supported deployment. |
| 9. Arr-blocked import rescue | P2 | Verify supported Arr rejection reasons and import confirmation before enabling each integration. |
| 10. Failed-torrent category choice | P2 | Verify safe client category changes and queue-only Arr removal. If Package 9 is enabled, finish rescue before applying a failed outcome. |
| 11. Seeder-count and inactivity retirement guards | P2 | Verify fresh client evidence for each guard. |
| 12. Proactive search gates and quiet hours | P2 | Package 1 for shared eligibility and decision reasons. Compose with Package 4 controls when enabled. |

## Shared release criteria

- [x] Existing configs keep their behavior on upgrade. New automation is opt-in. Any config additions appear consistently in the schema, docs and relevant WebUI editor, and survive partial saves.
- [x] Any exposed endpoint is documented in the existing API reference and follows the existing authentication policy. Credentials, tracker passkeys, authorization headers and sensitive URL query values stay out of logs, issue payloads, previews and public responses.
- [x] Any new storage is a Torrentarr extension. Upgrade and restart preserve existing qBitrr-compatible data. Document retention, bounds and what disabling a feature does.
- [x] Any mutation still uses the existing ownership, configuration scope, path validation and removal authorization. Unknown or stale evidence never grants clearance. Validate against disposable fixtures before exercising a destructive path.
- [x] Relevant automated tests pass on the release commit. Verify any changed UI workflow on narrow and wide layouts. Record verification, activation and rollback in the PR or release notes.

## 1. Search decisions and preview

**Goal.** From Processes, an operator can see what one Arr worker recently considered, why it did or did not submit a command, and what it would consider next. The existing chart remains the aggregate view.

**Build.** Add a Search activity drilldown to each Arr row. Show timestamp, title or safe identifier, missing/upgrade/replacement reason where known, and outcome. Record meaningful decisions and transitions in bounded history. Coalesce repeated skips for the same item and reason or summarize them by count. Mark submission, confirmed completion and observed grab separately. Link to Arr when the item has a linkable ID.

Expose a read-only candidate preview for one worker using the same eligibility and ordering logic the worker uses. Evaluate against the latest usable observations and label their age. A preview may become stale between display and execution. It cannot reserve a candidate, change profiles, modify queue state or issue an Arr command. Use existing Arr activity records for confirmed outcomes where suitable.

- [x] A representative missing item, upgrade, and skipped item each display the reason the worker used.
- [x] The same inputs give the same eligibility and ordering in preview and execution. Preview produces no mutation.
- [x] An accepted command without completion or grab evidence shows only the confirmed state. Stale or unavailable data is visible.
- [x] Repeated skips and old decisions are bounded. Preview and history work for multiple instances without leaking decisions between them.
- [x] **Ready to ship.** Evidence: [search activity documentation](../webui/console-statistics.md#arr-activity), the SQLite search-decision fixtures, and the Processes drilldown. Preview plans from the latest usable synced catalog with the worker’s planner and current gate observations.

Source: [Cleanuparr Seeker](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/seeker/index.mdx) and [Torrentarr console statistics](../webui/console-statistics.md).

## 2. Operational issue alerts

**Goal.** Operators can learn that a worker, import workflow or tracker obligation needs attention without keeping the UI open.

**Build.** Add one outbound webhook destination with an enable switch, selected issue types, and **Send test** in Settings. Start with sustained worker failures, actionable blocked imports and tracker protection deadlines. Deliver from the Host using persisted Issues transitions so workers do not send duplicates. Send the issue type, severity, instance, state, safe summary and issue ID. Resolve alerts only for an opening that was eligible under the notification policy. A reminder about an approaching tracker deadline must preserve any uncertainty in the underlying observation.

Apply bounded timeouts and retries. Use a stable event ID for retry deduplication and avoid replaying historical issues when notifications are enabled. Delivery failures must not interrupt automation or recursively generate more webhook deliveries. Redact the destination's secrets and prevent redirects from silently sending data to a different destination.

- [x] A new qualifying issue alerts once under repeated reports and concurrent worker updates. Its resolution is distinguishable.
- [x] Host restart, delivery timeout and HTTP failure exercise retry and deduplication behavior.
- [x] Disabled notifications and non-selected issues cause no delivery. Send test does not create an operational issue transition.
- [x] The payload, test response and logs contain no credentials, private tracker URLs or raw upstream responses.
- [x] **Ready to ship.** Evidence: [webhook alerts](../webui/issues.md#webhook-alerts), `IssueAlertDispatcherTests` and `IssueAlertEndpointTests`.

Source: [Cleanuparr notifications](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/notifications/index.mdx) and [Torrentarr Issues](../webui/issues.md).

## 3. qBittorrent stall safeguards

**Goal.** Separate metadata stalls, genuine lack of progress and deliberate throttling without adding a second strike engine.

**Build.** Add an optional elapsed metadata-stall threshold based on qBittorrent's reported metadata state. Start its timer from a usable observation and stop the timer when metadata arrives or the state changes. Evaluate any resulting candidate through existing authorization, including age, minimum stall grace and private-tracker rules. Preserve the existing policy when the threshold is disabled.

Add an optional alternate-speed exception to slow-speed decisions when the client confirms alternate limits are active. Leave independent error, repair and metadata handling on their own paths. On meaningful progress, reset the applicable continuous stall window. Distinguish actual download progress from a recheck or a stale snapshot before resetting. Report the hold, reset or candidate reason through existing diagnostics.

- [x] Metadata appears before and after the threshold in separate cases. Only the sustained case yields a candidate.
- [x] Genuine progress resets the stall window. Snapshot gaps and rechecks neither fabricate progress nor authorize removal.
- [x] Alternate-speed mode suppresses only the configured slow judgment. Unsupported or unavailable status is explicitly handled without inferring that normal limits are active.
- [x] Unknown private-tracker evidence, explicit never-remove policies and active rechecks remain protected.
- [x] **Ready to ship.** Evidence: [stall safeguards configuration](../configuration/torrents.md#qbittorrent-stall-safeguards), `DownloadStallTrackerTests` and the processor's `MetadataGrace` fixture.

Source: [Cleanuparr Queue Cleaner](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/queue-cleaner/index.mdx) and [Torrentarr torrent rules](../configuration/torrents.md).

## 4. Proactive search pacing

**Goal.** Avoid submitting searches for recently grabbed media or while an instance already has enough active downloading work.

**Build.** Add two independent, opt-in controls per Arr instance: recent-grab cooldown and active-download ceiling. Use correlated Arr grab evidence for the cooldown. An accepted search command alone does not prove a grab. Count distinct active downloads in the instance's existing ownership scope using fresh queue or client observations. At the ceiling, defer proactive work until the next eligible cycle. With a configured gate and unavailable input, defer rather than assume an empty queue, and display why.

Surface these decisions in Package 1. Apply the gates to proactive missing and upgrade work. Keep replacement search behavior separate. Package 12 adds quiet hours and the remaining eligibility controls.

- [x] A recent grab defers the affected item and expires at the configured boundary. Other candidates remain eligible.
- [x] Duplicate observations count once. Below, at and above the active ceiling produce the expected result.
- [x] Arr unavailability and stale client data explain the deferral. Clearing the condition resumes proactive work without manual intervention.
- [x] Replacement searches keep their existing behavior. Restart does not erase a cooldown while reliable grab evidence remains available.
- [x] **Ready to ship.** Evidence: [pacing configuration](../configuration/search/index.md#proactive-search-pacing), `SearchPacingTests` and the search executor and preview fixtures.

## 5. qBittorrent API-key login

**Goal.** Add qBittorrent API-key authentication within the existing client configuration and connection flow.

**Build.** Add a separate redacted credential field and an explicit authentication choice. Existing clients default to their current login mode and retain stored credentials on partial saves. Use the upstream-supported auth mechanism for configured API-key mode. Verify supported qBittorrent versions and endpoints against upstream documentation during implementation. Report unsupported auth, a rejected key and a network failure distinctly. Do not fall back to a different credential mode on failure.

- [x] Old credential and configured bypass flows still work. A supported instance can connect, reconnect and perform a representative authorized client operation with its key.
- [x] Old versions, expired or rejected keys, and unreachable instances produce actionable errors.
- [x] Redacted fields preserve the stored key until explicitly replaced or cleared. No key leaks through configuration reads, logs or connection tests.
- [x] **Ready to ship.** The loopback and config API fixtures cover authentication responses and credential preservation. A disposable qBittorrent 5.3.0 instance verified generated-key login, reconnect, category creation and readback, and rejection after key rotation. [API key configuration](../configuration/qbittorrent.md#api-key-authentication)

## 6. Shared file filter lists

**Goal.** Update one reviewed list for several Arr instances while retaining Torrentarr's existing file filtering and import rules.

**Build.** Allow explicit Arr references to a named local file or HTTPS list. Define and document one bounded format compatible with the existing filter matcher. Keep current rules effective alongside the shared list and apply shared entries as additional exclusions. Parse and validate the complete list before activation. Refresh outside torrent processing. Retain the last valid version on transient read or parse failure and raise an Issue. If no usable list has ever loaded, hold filtering-dependent actions for affected instances and show why.

Provide a read-only preview for example paths, with matching source and list freshness. Reuse the regular matcher. Bound fetch size, time, and pattern evaluation. Review URL and local-path handling for unsafe access. Do not change qBittorrent's native settings.

- [x] Two instances share a list and keep their respective local rules. A successful refresh changes effective matches consistently.
- [x] Malformed, unreachable, oversized or unsafe inputs cannot activate a partial list. Last-valid or unavailable state is visible.
- [x] A torrent with eligible media and an excluded extra can still import under existing rules. File matches grant no new deletion authority.
- [x] **Ready to ship.**

Named Settings lists and per-Arr references round-trip
through TOML and the generated schema. Hosted refresh activates validated bounded
snapshots, retains last-valid data, reports Issues, and holds unavailable lists.
The processing matcher and authenticated preview share these snapshots.
Shared-only matches cannot authorize deletion. Persistent priority ownership
supports restoration after refresh or restart. Focused tests cover source access,
validation, two consumers, failure retention, deletion withholding, and priority
restoration. The authenticated preview was checked in both browser layouts.

Source: [Cleanuparr Malware Blocker](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/malware-blocker/index.mdx) and [Torrentarr file filtering](../configuration/torrents.md).

## 7. Related log links

**Goal.** Open the relevant log context directly from an Issue, maintenance run or search decision.

**Build.** Use reliable operation or run IDs already present in structured records. Add **View related logs** to detail views with such an ID, and **Copy details** for a safe support summary. Open the existing Logs page with the stream and filter selected. Indicate missing or expired history. Where an exact run ID is unavailable, offer the known service stream without implying an exact match.

- [x] A link finds the intended run across eligible active and rotated logs, without matching an unrelated instance.
- [x] Expired or missing data produces a clear empty state and a way back to normal log browsing.
- [x] Copied details follow the same redaction policy as the source view.
- [x] **Ready to ship.** Evidence: [related log links](../webui/logs.md), rotated log search and issue API fixtures, and `LogsView.test.tsx`. Search decisions link to their submission cycle.

## 8. Root-aware hardlink auditing

**Goal.** Distinguish evidence of a library hardlink from links found only in a download or cross-seed root.

**Build.** Use the existing root-aware audit option if filesystem inspection can support it reliably. Define explicit library and ignored roots, map them locally, and match stable file identity within the same filesystem. Bound work by scan and inspection budgets. Keep the existing tag-only workflow and change only Torrentarr-owned tags. Preserve existing tags when coverage is incomplete. A link count alone cannot establish which directories contain the links. Report unavailable mounts, unsupported filesystem identity, permission failures and truncated scans as unknown.

- [x] Fixtures cover a library link, a cross-seed-only link, no link, a missing root, and a scan that hits its budget.
- [x] Symlinks and nested roots cannot make the audit silently claim full coverage or exceed its scope.
- [x] Preview explains the proposed tag change and its evidence. Unknown results never grant deletion authorization.
- [x] Supported filesystems and platforms are documented. If reliable inspection is infeasible, leave the option unsupported and close this package as deferred.
- [x] **Ready to ship.**

Root-aware inspection now uses Linux device/inode
identity on supported local filesystems, explicit mapped library/ignored roots,
bounded coverage, and tag ownership. Native fixtures cover library, cross-seed,
missing, symlink, nested-root and budget cases. Maintenance fixtures cover owned
tags, unknown coverage, apply-time revalidation and shared execution budgets.
Other platforms and unsupported filesystems remain explicitly unknown.
Browser checks covered the library and ignored root settings.

## 9. Arr-blocked import rescue

**Goal.** Recover a completed download from a known Arr rejection when the existing media is eligible and Arr exposes one unambiguous import match.

**Build.** Add an opt-in policy per Arr instance, initially supporting only rejection reasons reproduced in fixtures for that app. Use the existing import service, file rules and path mappings. Require a fresh queue entry, stable download identity, matching Arr ownership, a completed and accessible payload, and one eligible match to the intended media. Ambiguous matches and unsupported rejections continue through existing failure handling. Start with one rescue attempt per failure incident and persist the claim across restarts so an uncertain response does not cause a duplicate submission.

Treat an accepted command as pending. Require positive Arr import history correlated to the download and submitted attempt before marking it rescued. While confirmation is pending, hold competing failed outcomes for that incident. If confirmation expires, record the unconfirmed result and re-read Arr and client state before normal handling resumes. A confirmed rescue skips category moves, blocklisting and replacement searches. Rescue itself grants no authority to delete, recycle or overwrite unrelated content.

- [x] A supported rejection with one match imports and records a confirmed result. Multiple matches, missing paths, a changed torrent ID and unsupported rejection reasons submit no rescue.
- [x] A timeout, duplicate observation and worker restart do not submit the same rescue twice. Queue disappearance or command acceptance alone does not count as success.
- [x] Rescue applies file exclusions and allows eligible files to import while protected cleanup is deferred. A confirmed import cannot also be processed as a failed torrent.
- [x] **Ready to ship.**

Source: [Cleanuparr Queue Cleaner](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/queue-cleaner/index.mdx) and [Torrentarr import behavior](../configuration/torrents.md).

Radarr's matched-by-ID blocked import now has an
opt-in rescue through its manual-import API. The shared matcher, mapped owned
files, fresh queue and client snapshots, and one unrejected movie match constrain
submission. SQLite claims precede a single nonredirecting Copy command. Matching
import history and queue clearance confirm success; pending receipts hold competing
removal. Loopback fixtures cover restart, concurrency, timeout, rejected submission,
wrong/ambiguous identity, inaccessible files and shared exclusions. Other Arr
adapters remain explicitly unsupported.

## 10. Failed-torrent category choice

**Goal.** Let operators decide whether a qualifying failed torrent stays under the existing policy or moves to a category they choose. The move option retains the client entry and payload while resolving the associated Arr queue item where safe.

**Build.** Add a per-Arr **Failed torrent action** with **Use existing policy** as the default and **Move to a category and keep files** as the opt-in choice. When move is selected, require a destination for each in-scope qBittorrent client. Show existing categories on the selected client and allow explicit selection of an unmanaged category. Never infer a destination from Arr's post-import setting, create a category implicitly, or silently use another client's category. Show the effective destination and whether it is already managed by an Arr. Reject routes back to the source, special processing categories, or categories that hand ownership to another Arr. Check current categories again at execution.

Use a fresh client snapshot and validate the effective save path and automatic torrent management. If the move can relocate data or change ownership unexpectedly, hold the item and explain why. First verify the Arr adapter supports removing the queue record without deleting the download or files. Change the category through the client, confirm the new category and preserved save path, then perform queue-only removal in Arr. If the Arr step fails, retain the moved torrent and record a reconciliation issue. Persist enough state to retry only the outstanding step after restart. Never fall back to deletion if mapping, movement or queue-only removal fails. Preserve existing protection evidence against eventual deletion. Where configured policy also requests blocklisting or replacement, apply those only after the queue outcome is confirmed and only through operations that preserve the retained torrent.

- [x] Existing configurations behave as before. Operators can opt in independently per Arr and select different destinations for multiple clients.
- [x] No destination, a missing category, an invalid owner, a relocation risk or an unsupported Arr operation leaves the entry and payload intact and explains the hold.
- [x] A valid move preserves client identity, files, path and applicable tracker evidence. The Arr queue entry is cleared without asking Arr to delete the retained download.
- [x] Confirmed import rescue wins over parking. Failed queue removal or a restart between steps resumes reconciliation without repeating a confirmed move, blocklisting twice or falling back to deletion.
- [x] **Ready to ship.** Evidence: [failed torrent action](../configuration/torrents.md#failed-torrent-action),
  per-client destination settings and editor, disposable queue and restart fixtures,
  and retained-torrent protection tests. Both config APIs preserve destinations
  through partial saves and client rename. Browser checks covered the selector at
  390px and 1440px.

## 11. Seeder-count and inactivity retirement guards

**Goal.** Let operators preserve torrents after they meet ordinary time or ratio limits, based on swarm availability or recent upload activity.

**Build.** Add independent, opt-in **Minimum reported seeders** and **Minimum upload inactivity before retirement** settings within the existing seeding policy. Show the effective policy where seeding settings are edited. Apply the guards only to ordinary automatic retirement of completed torrents. Existing ratio/time eligibility and tracker protection still apply. When both guards are enabled, both must clear. A low reported count preserves the torrent. Missing, stale or unsupported counts hold the torrent and produce a reason, while a verified zero is a known count.

Define inactivity as elapsed time since the last verified payload upload. Use a reliable client timestamp if available. Otherwise use a persisted observation of upload counter changes. Start the observation window conservatively and reset it after any verified upload. Do not treat missing observations, counter resets, client identity changes or a re-added torrent as evidence of inactivity. Revalidate enabled guards against fresh observations immediately before removal. Show the blocking reason, observed count or inactivity duration, threshold, and observation age in existing diagnostics. These guards grant no removal authority and do not introduce zero-seeder cleanup.

- [x] Defaults preserve existing retirement behavior. Ratio/time clearance plus unmet tracker protection still holds the torrent regardless of the new guards.
- [x] Test zero, below, equal and above the minimum, plus stale, missing, and unsupported counts. Only fresh evidence meeting the configured minimum clears the seeder guard.
- [x] Recent uploads reset the inactivity window. Missing intervals, resets and re-additions cannot grant clearance. Test threshold boundaries, restart, and both guards enabled together.
- [x] Revalidation holds the torrent when evidence changes before removal. Non-retirement workflows retain their existing safeguards.
- [x] **Ready to ship.** Evidence: [retirement guard settings](../configuration/seeding.md#retirement-guards) and `RetirementGuardTests`.

Source: [Cleanuparr Download Cleaner](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/download-cleaner/index.mdx) and [Torrentarr seeding policy](../configuration/seeding.md).

## 12. Proactive search gates and quiet hours

**Goal.** Give operators control over which library items Torrentarr searches, when proactive searches can run, and how long it waits before retrying an item after an unsuccessful search.

**Build.** Add independent, opt-in controls to each Arr instance's Entry Search settings. Apply them to proactive missing and upgrade searches. Keep replacement searches on their existing path. A candidate must pass every enabled gate. Feed the same decisions into the executor and Package 1 preview. Show the gate that deferred a candidate and its next eligible time when known.

| Control | Behavior |
| --- | --- |
| Include and exclude tags | Match tags on the owning Arr library entry, such as the series for a Sonarr episode. With an include list, require at least one matching tag. Any exclude match takes precedence. An empty list applies no restriction. Offer the controls where the integration can supply the tags. Unavailable tag evidence defers affected candidates rather than treating them as untagged. |
| Radarr availability | Keep current eligibility as the default. When enabled, require Radarr to consider the movie available under its own settings. Use fresh availability evidence rather than inferring availability from a release date. A missing signal defers the candidate with a reason. |
| Quiet hours | Configure one daily local start and end time with an explicit IANA timezone. Use a start-inclusive, end-exclusive interval, support midnight crossing, and reject identical boundaries. Evaluate each instant in the configured zone so repeated local times follow the same rule and nonexistent times require no synthetic run. Resume on the next normal cycle, without replaying missed work. |
| Item rest after an unsuccessful search | Configure a fixed rest period for the same instance, item and search scope. Begin it only after a proactive search completes and reliable Arr evidence shows no grab within the confirmation window. A rejected command, timeout, or unknown result is not a confirmed no-grab outcome. Persist the expiry across restarts. A confirmed grab follows Package 4's recent-grab rule when enabled. Other eligible items can still be searched. |

- [x] Upgrades from existing settings preserve search behavior. Each control can be enabled independently and validated through the editor.
- [x] Include matches, exclude precedence, no-tag entries and unavailable tag evidence produce the expected outcomes. Test an episode inheriting its series tags.
- [x] Radarr release-date and availability evidence can disagree. The opt-in gate follows the app's availability and explains missing data.
- [x] Quiet hours cover both boundaries, midnight crossing, daylight-saving gaps and repeated times. Proactive work resumes on the normal cadence and replacements remain eligible.
- [x] An observed no-grab result starts item rest, a confirmed grab uses the separate cooldown, and an uncertain result starts neither based on that result alone. Verify expiry, restart and continued eligibility of other items.
- [x] Preview and execution agree when several gates apply, and deferrals identify the reason without issuing a search command from the preview.
- [x] **Ready to ship.**

Source: [Cleanuparr Seeker](https://github.com/Cleanuparr/Cleanuparr/blob/v2.11.1/docs/docs/configuration/seeker/index.mdx).

Opt-in include/exclude tag labels, Radarr availability,
and IANA-zone quiet hours now share gates between execution and preview. Fresh
owning-library resources supply tags; unavailable evidence defers. Sonarr uses
per-episode commands with item gates enabled. Boundary, DST, inherited-tag,
availability and replacement-isolation fixtures cover these controls. Persisted
no-grab item rest now uses completed command evidence, a thirty-minute confirmation
window, scoped expiring receipts, and bounded negative-evidence age. Focused
search, no-grab, activity and rename tests pass, as do config partial-save and
frontend checks. The preview now plans from the latest usable synced catalog
with the same planner and gate checks as execution.

## Release validation

The implementation and its follow-up fixes are in
[PR #595](https://github.com/Feramance/Torrentarr/pull/595). At `68fefb04`, the
local Release run passed 2,752 non-live .NET tests across Core, Host, and
Infrastructure. The full WebUI suite passed 516 tests. The OpenAPI drift tool
suite passed 27 tests. The commit hooks checked the .NET and WebUI builds.
[Cross-platform build and test results](https://github.com/Feramance/Torrentarr/actions/runs/45052792672)

The local Docker Compose redeploy of `68fefb04` passed its health check with the
existing config and database. Browser checks at 390px and 1440px covered search
preview, the failed torrent destination selector, file filter preview, webhook
settings, qBittorrent authentication, retirement guards, hardlink roots, and the
Arr editor's search, import rescue, and stall controls.
The search preview returned fresh catalog evidence. The destination selector
showed live categories for the selected client and disabled categories that could
hand ownership to Arr. A search decision opened matching cycle logs and the Logs
page provided a way back to normal browsing. The browser checks ran without saving
configuration changes. Mutation and retention tests used disposable fixtures.

Keep the new automation switches off during upgrade if you want to retain
existing behavior. Enable features per integration or client after reviewing
their configuration prerequisites. Disabling import rescue or failed torrent
moves stops new attempts while recorded operations can still reconcile.
Before rolling back application binaries, stop the service and save current
config and database separately. Restore the pre-upgrade backup only when
needed, as restoring a backup discards newer recorded activity.

## Deferred until a use case is demonstrated

| Idea | What would justify reopening it |
| --- | --- |
| Local TOTP or a dedicated readiness endpoint | A deployment requirement that current OIDC or health checks cannot meet. |

## Excluded from this roadmap

| Idea | Reason |
| --- | --- |
| Additional download clients, Usenet and more Arr-family adapters | Expands compatibility and maintenance scope. Evaluate each separately as a product decision. |
| Full strike engine, parallel rule builder and a strike ledger | Existing automation can gain the specific timing safeguards and decision visibility without duplicating cleanup authority. |
| Native blacklist sync, delete-on-any-match and zero-seeder deletion | Broadens the blast radius of file or tracker signals that do not independently establish safe removal. |
| Global Dry Run | Maintenance already has reviewed previews. Add read-only decision views to existing workflows where candidates are hard to inspect. |
| Separate orphan storage and hardlink-driven retirement | Existing maintenance and retention are the right home for storage actions. A hardlink audit is evidence for tagging and review. |
| A provider catalog, separate Events console and duplicate stats pages | Start with one webhook and improve the existing Issues, Logs, Processes and Recent Runs views. |
| Weighted/random search strategies, rearrangeable cards and custom accents | Do not address the selected problems of troubleshooting, unnecessary searches and premature cleanup. |
| Plex login, trusted-local auth bypass, PostgreSQL and extra packaging | Require a separate deployment or product decision. SQLite compatibility and existing authentication remain sufficient for this roadmap. |
| Multiple log export formats and configurable archive machinery | Prioritize reliable correlation and copyable details alongside the existing file download and rotated search. |
