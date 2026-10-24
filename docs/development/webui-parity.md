# Canonical qBitrr frontend replacement — 2026-10-19

This report supersedes the 2026-10-18 hybrid-overhaul acceptance claims. The reference
is `/home/feramance/qBitrr`, pinned at `be5cb670`; this continuation starts from
Torrentarr `9b2b7f3` in PR #520. No dependency upgrades or database resets are involved.

## Implementation and intentional boundaries

The canonical shell, authentication boundary, theme bootstrap, contexts, composed
configuration modules, catalog shell, tables and styles now form one frontend.
The old monolithic App and ConfigView implementations have been replaced.
[Every upstream frontend path and Torrentarr-only file is reconciled here](webui-source-reconciliation.md).
Formatting-only classifications are source comparisons, not claims of identical
runtime behavior. Python registry generation is replaced by Torrentarr's existing
.NET schema. Upstream-only tests not copied are explicitly recorded.

Navigation derives Arr/client availability from configured instances. Offline or
disabled instances remain visible; absent types do not mount or fetch. Successful
availability survives refresh failures. Saves, visibility restoration and periodic
status refresh update navigation; disappearing active tabs return to Processes.
Visited catalog state is retained.

Processes uses the canonical card/category layout. One physical worker has two
Search/Torrent activity rows, one count and one Restart worker action. Structured
heartbeats pass through both Host process APIs. Connectivity is separate from
process counts; standalone WebUI reports missing supervision truthfully. Stopped
workers override stale loop phases. Category navigation carries client identity.

Canonical configuration dialogs save only the selected section, including renames,
and preserve unrelated drafts, redacted secrets and failed edits. The .NET schema
owns defaults, constraints, units and restart guidance. Additional torrent clients
use the shared dialog; structured maintenance arrays remain structured JSON with
array validation. Focus trapping and inactive-modal scroll cleanup remain intact.

The shared catalog and StableTable consume the row store, including fresh cell
accessors and order-only changes. API requests, aggregate keys and selections use
instance identity rather than ambiguous categories. Authenticated posters, safe
links, cancellation, detail focus handling and Readarr's shared workflow are retained.

Structured .NET log tails retain older windows, rotation, bounded history, search,
independent Live/Follow and SSE/polling fallback. Maintenance keeps its safeguards.
Root/prefixed SPA aliases resolve relative assets through an injected base. Deploy
cache versions derive from bundle content; API/authenticated responses are excluded
from persistent SW caching. Docker guidance instructs rebuilding/recreating the
container instead of replacing a binary.

## Evidence and limits

`scripts/webui-parity-preview.py` serves synthetic fixtures to both production
frontends on 16970/16969 without contacting live services. `webui/fixtures/parity.json`
fixes record identities and timestamps. Scenarios include empty, absent Readarr,
offline, large catalogs, missing images, login, setup and updates.

The screenshot walkthrough covers all top-level views at 1440×900, 768×1024 and
390×844 in Dark/Light and Comfortable/Compact. Additional captures cover four Arr
list/detail workflows and eight configuration dialogs in each viewport/theme/density
combination. Captures are local in `/tmp/torrentarr-parity/screenshots`; deduplicated
DOM observations are [recorded in JSON](webui-visual-checks.json). These are walkthrough
observations, not pixel-diff assertions. Some captures precede final text/worker-state
corrections; representative final deployed states are checked separately.

The reference itself overflows mobile comfortable Config to 448px. Torrentarr fixes
that grid and modal sizing rather than reproducing the defect. Mobile artist detail
poster and track rows were rechecked: dialog/table scroll width equals client width.
Offline/empty/login/setup/large/missing-image scenarios are checked independently.
Different physical-process counts and Maintenance/additional-client controls are
intentional architectural differences.

Automated regressions cover navigation availability/state retention, real section
saves/renames/secrets/drafts, completion fractions, extra clients, stale worker phases,
status-only updates, category targets, row-store updates, catalog totals, authenticated
media, log reconnect/rotation, URL-base aliases and service-worker caching.

The table below records the original parity verification run. Later branch-review fixes and their current validation results are recorded in [PR #520 review follow-up](pr-520-review-follow-up.md); those results supersede these historical test counts.

| Check | Result |
| --- | --- |
| Frontend | 287 passed across 42 files |
| Core non-live | 271 passed |
| Infrastructure non-live | 852 passed |
| Host non-live | 324 passed |
| Lint | 0 errors, 13 existing warnings |
| Build/hooks | Production TypeScript/Vite, Release .NET build and required hooks passed; JSON formatting correction included on retry |
| Source inventory | All 171 upstream frontend paths classified; Torrentarr-only paths listed separately |

The async UI query deadline is three seconds for production-sized lazy routes on
constrained build hosts. An earlier concurrent run hit one-second query deadlines;
the complete isolated rerun above passed. Production assets are verified by comparing
served bytes to the packaged build, then checking live navigation, worker activities,
category drill-down and log controls. The container retains its config/data volumes;
Docker Compose working bytes and staged/unstaged patches are compared before/after.
Full real-provider OIDC redirects and live destructive configuration/maintenance/update
mutations are outside this verification. No claim is made that every nested record or
all combinations of error/provider states have been visually certified.
