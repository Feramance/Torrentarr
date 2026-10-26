# PR #520 review follow-up

Baseline: `0f5c809212026ab2df7e0ed02d67fb3037f9e101`. Reviewed 49 inline threads (32 CodeRabbit, 17 Codex), including older and duplicate findings. The CodeRabbit review-body select-style suggestion is already addressed by the shared helper.

These dispositions describe the local implementation. GitHub threads remain open until the changes are published and their checks pass; no reviewer replies or new review requests were sent.

## Thread dispositions

| # | Reviewer | Finding | Disposition and evidence |
| --- | --- | --- | --- |
| 1 | Codex | [Readarr missing filter](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307357786) | Implemented: API serialization regression |
| 2 | Codex | [Native log-level formats](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307357790) | Implemented: Native log-level regression |
| 3 | Codex | [History after live appends](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307357792) | Implemented: Byte-boundary history regression |
| 4 | Codex | [HnR percentage range](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307357794) | Already addressed: Existing schema and tracker bounds remain 0–100 |
| 5 | CodeRabbit | [Docker binary restriction text](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374047) | Implemented: Installation-specific release metadata |
| 6 | CodeRabbit | [Update route admission result](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374050) | Implemented: Both update routes and client reject unsuccessful admission |
| 7 | CodeRabbit | [Failed-save toast assertion](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374053) | Implemented: Assertion matches the actual success text |
| 8 | CodeRabbit | [Displayed percentage rounding](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374056) | Implemented: Two-decimal percentage conversion |
| 9 | CodeRabbit | [Dismissed toast deduplication](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374062) | Implemented: Immediate redisplay regression |
| 10 | CodeRabbit | [Readarr author totals](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374065) | Implemented: Separate author rollup from book metrics |
| 11 | CodeRabbit | [HnR percentage range (duplicate)](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374075) | Already addressed: Same existing correction as #4 |
| 12 | CodeRabbit | [Search-match scroll target](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374082) | Implemented: Server start_line determines scroll position |
| 13 | CodeRabbit | [Stable history paging](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374085) | Implemented: Same backward cursor as #3 |
| 14 | CodeRabbit | [Obsolete delta responses](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374087) | Implemented: Selection/window epoch guards |
| 15 | CodeRabbit | [Capped category aggregates](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374092) | Implemented: Server aggregates retained on category matches |
| 16 | CodeRabbit | [Disabled rate-limit display](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4307374099) | Already addressed: The obsolete display was removed from the current branch |
| 17 | Codex | [Root index URL prefix](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310625868) | Implemented: URL-prefix regression matrix |
| 18 | Codex | [Overview selection race](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310625875) | Implemented: Deferred overview response regression |
| 19 | Codex | [Canonical TorrentClient document section](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310625880) | Implemented: Canonical fixtures, save paths and backend round-trip tests |
| 20 | Codex | [Inactive catalog portals](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310625886) | Implemented: Clear modal selection when active becomes false |
| 21 | CodeRabbit | [Reported validation counts](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663350) | Already addressed: Historical runs retained; latest results below supersede earlier counts |
| 22 | CodeRabbit | [Binary update support metadata](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663354) | Implemented: Metadata derives from CanApplyBinaries |
| 23 | CodeRabbit | [Normalized channel metadata](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663358) | Implemented: Both metadata shapes use normalized channels |
| 24 | CodeRabbit | [Asynchronous no-POST assertion](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663363) | Implemented: Wait for validation feedback before asserting |
| 25 | CodeRabbit | [Invalidated in-flight configuration GET](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663374) | Implemented: Late-response and newer-request cleanup regression |
| 26 | CodeRabbit | [Trailing-slash URL prefix](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663381) | Implemented: URL-prefix regression matrix |
| 27 | CodeRabbit | [Search-only environment variable tooltip](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663389) | Implemented: Use QBITRR_OVERRIDES_SEARCH_ONLY |
| 28 | CodeRabbit | [Storage failure guards](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663395) | Implemented: Guarded storage and session-token fallback regression |
| 29 | CodeRabbit | [Legacy browse preferences](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663400) | Implemented: Legacy-key preference regression |
| 30 | CodeRabbit | [Inactive catalog portals (duplicate)](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663404) | Implemented: Same deactivation fix as #20 |
| 31 | CodeRabbit | [Accessible Instance select](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663406) | Implemented: Per-catalog matching id/htmlFor |
| 32 | CodeRabbit | [Catalog chunk failure](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663409) | Implemented: Rejected-import reload regression |
| 33 | CodeRabbit | [Secure credential randomness](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663411) | Implemented: 128-bit getRandomValues regression |
| 34 | CodeRabbit | [Field overlay properties](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663422) | Implemented: Merge overlays, then compose validation |
| 35 | CodeRabbit | [Edited Arr connection settings and stored secret](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663426) | Implemented: Modal payload and backend edited-URI regressions |
| 36 | CodeRabbit | [Category-affecting section changes](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663429) | Implemented: Nested section category-change regression |
| 37 | CodeRabbit | [Restartability filter](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663433) | Implemented: Send requests only for restartable items |
| 38 | CodeRabbit | [Duplicate client process cards](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663439) | Implemented: A client-owned process belongs only in the client bucket |
| 39 | CodeRabbit | [Overview selection race (duplicate)](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663444) | Implemented: Same request-generation fix as #18 |
| 40 | CodeRabbit | [Processes category focus](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310663447) | Implemented: Wait for the matching overview before consuming focus keys |
| 41 | Codex | [Edited TLS settings](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310807560) | Implemented: Modal regression covers explicit TLS in both secret branches |
| 42 | Codex | [Overlapping log delta polls](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4310807565) | Implemented: Only one delta poll runs at a time |
| 43 | Codex | [Log history beyond 8 MiB](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4311216349) | Implemented: 10 MiB history regression |
| 44 | Codex | [Obsolete delta responses (duplicate)](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4314027781) | Implemented: Same epoch guard as #14 |
| 45 | Codex | [Search-to-history boundary](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4314027796) | Implemented: UTF-8 search/history boundary regression |
| 46 | Codex | [Renamed Arr stored credentials](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4314027810) | Implemented: resolveSectionDiskKey supplies the persisted name |
| 47 | Codex | [Catalog chunk failure (duplicate)](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4314027816) | Implemented: Same rejected-import fix as #32 |
| 48 | Codex | [Configuration event endpoint](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4322469946) | Implemented: Host/standalone stream, auth, status and real listener-rebind regressions |
| 49 | Codex | [Delayed removed-worker acknowledgment](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4322469952) | Implemented: Delayed acknowledgment and restored-instance regressions |

## Validation

Final local validation on 2026-10-20:

| Check | Result |
| --- | --- |
| Frontend | 329 passed across 48 files |
| Core, Release, non-live | 287 passed |
| Infrastructure, Release, non-live | 910 passed |
| Host, Release, non-live | 365 passed |
| Non-live .NET total | 1,562 passed |
| Workflow/parity script tests | 37 passed |
| Production TypeScript/Vite build | Passed |
| Frontend lint | 0 errors, 13 existing warnings |
| Generated configuration schema | Current (`--check`) |
| OpenAPI parity vs qBitrr v5.14.6-1 | Passed |
| Required pre-commit hooks | Passed (tracked and new files; user Docker Compose edits excluded) |
| Published Linux standalone smoke | Passed: embedded assets, both worker loops, restart, shutdown |

The full frontend run was isolated from builds after concurrent build/test runs hit existing five-second test deadlines. No test deadlines were increased. The modal orchestration regression stubs generated field controls; catalog behavior tests preload their definition separately from the explicit rejected-import test. Endpoint tests cover authenticated stream access restrictions, initial snapshots in both server modes, component acknowledgments without a held configuration lease, and a real listener rebind. Log regressions include a 10 MiB file, appends between pages, UTF-8/CRLF boundaries, rotation, overlapping polls, and late responses from an obsolete selection.

## Follow-up to review of `a028afa`

The 2026-10-20 CI run passed Linux, frontend tests, lint, CodeQL, documentation, pre-commit, and standalone smoke tests on all three platforms. The .NET stage failed on macOS and Windows:

- macOS: temporary directories use a `/var` symlink. Path safety correctly rejects symlink ancestors, so maintenance fixtures now resolve their temporary root to its physical path. Production path validation is unchanged.
- Windows: a search-window fixture wrote platform CRLF while asserting LF. It now writes explicit LF; the existing UTF-8/CRLF boundary regression remains intact.

Six additional Codex threads were verified and addressed:

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 50 | [Live qBit category worker reconciliation](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380186) | Runtime reconciles category workers before acknowledging each revision; regression covers category addition and removal. |
| 51 | [Refresh drops live log events](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380198) | Tail generation controls stream replacement; regression rejects old-stream events and accepts the new append after Refresh. |
| 52 | [Reused instance names inherit historical renames](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380207) | Snapshot rename history is revision-scoped for workers and processes. Tests cover A → B, new A, repeated reconciliation and B → C; reused names get distinct heartbeat paths while the renamed worker remains alive. |
| 53 | [Stale cookie paths after a URL-base change](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380216) | Host and standalone invalidate cached cookie options and route after live prefix stripping; regressions check paths and cookie-only login/token access after rebasing. |
| 54 | [Cleared URL base retains startup HTML prefix](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380229) | Both entry points read current configuration; regressions clear a nonempty prefix and verify the root document base. |
| 55 | [Overview ignores Arr subcategory overrides](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4325380241) | Overview uses the existing owner-specific helper; regressions cover true/false overrides and inherited defaults with aggregate counts. |

CodeRabbit again skipped its incremental review; no new CodeRabbit findings were posted. No bot review requests or replies were sent.

Follow-up local validation:

| Check | Result |
| --- | --- |
| Frontend | 330 passed across 48 files |
| Core, Release, non-live | 287 passed |
| Infrastructure, Release, non-live | 916 passed |
| Host, Release, non-live | 367 passed |
| Non-live .NET total | 1,570 passed |
| Workflow/parity script tests | 37 passed |
| Production TypeScript/Vite build | Passed |
| Frontend lint | 0 errors, 13 existing warnings |
| Generated configuration schema | Current (`--check`) |

Remote Windows/macOS validation and review-thread resolution follow publication. No test deadlines were increased and no safety guards were relaxed.

## Follow-up to review of `10a7a82`

Linux and Windows completed the complete build/test and standalone-smoke matrix successfully. macOS failed three App lifecycle assertions because a real lazy Config route escaped the mock boundary. The harness now preloads lazy routes while its mocks are in scope, stubs unrelated route/background work, and retains all lifecycle assertions and the existing deadlines. The failure was reproduced locally before the corrected focused run passed.

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 56 | [Worker application acknowledgments](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4327076629) | Supervisor forwards validated heartbeat revisions to the reloader. Regression rejects mismatched process identities, keeps older revisions pending, reaches applied on acknowledgment, and prevents removed components from returning during a pinned operation. Standalone WebUI does not wait for workers it does not launch. |
| 57 | [Reload after live URL-base changes](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4327076636) | Base/listener changes use one document navigation so relative lazy chunks load from the new prefix. Regressions preserve query/hash, clear prefixes, respect proxies, combine listener/base changes, and leave theme-only changes in place. |

Latest local validation: 335 frontend tests across 49 files; the full non-live .NET run passed 287 Core, 917 Infrastructure and 367 Host tests (1,571 total). The final standalone-only worker-tracking guard adds one Host regression and passed all 18 selected standalone/event endpoint tests. The production frontend build passed, and lint reported 0 errors with the same 13 existing warnings. The full frontend pass ran alone after a concurrent .NET/frontend attempt exceeded an existing Config test deadline; no deadlines changed. Remote validation and thread resolution follow publication.

## Follow-up to review of `115ce4a`

All checks on the merged-master baseline passed. The remaining Codex finding was verified:

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 58 | [Busy worker revision acknowledgments](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4359846544) | Both loops acknowledge their pinned configuration revision at the completed-cycle boundary, even when work leaves no sleep time. The regression runs both loops without sleep and publishes a newer revision during each cycle, verifying that the heartbeat acknowledges only the completed revision. It fails against the original loops and passes with the fix. |

## Follow-up to review of `794505a`

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 59 | [Listener readiness before browser navigation](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4362628040) | Browsers probe the new listener without credentials before navigating, retrying refused or timed-out connections while retaining the running document. Superseded probes and unmounts abort navigation. Regressions cover delayed readiness, a second prefix change during rebinding, stale responses, and unmounting. Prefix-only changes remain immediate; proxy behavior is preserved. |

Local validation: all 338 frontend tests across 49 files passed; lint has 0 errors and the same 13 existing warnings. The preceding worker fix passed all 1,573 non-live .NET tests (287 Core, 918 Infrastructure, 368 Host). Required hooks run at each commit.

## Follow-up to review of `c8c9e6b`

All CI checks passed on the preceding revision. Two further Codex findings were verified:

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 60 | [Worker snapshots across Arr renames](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4362852506) | Host and standalone publish the replacement snapshot while the rename barrier remains exclusive. Workers refresh their snapshot after acquiring a processing lease and discard a cycle pinned to an older revision. Tests suspend connectivity in both loops, publish a rename, and verify old-identity work is skipped. Publication-barrier tests cover success and failure; failed publication retains the recovery journal and blocks processing. |
| 61 | [IPv6 navigation hostnames](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4362852518) | Explicit IPv6 bind addresses are bracketed before assigning URL.hostname. Regression verifies both the readiness probe and document navigation use `[::1]`. Wildcard bind handling is unchanged. |

Latest local validation: 1,577 non-live .NET tests passed (287 Core, 922 Infrastructure, 368 Host), plus the strengthened two-loop race assertion. All 339 frontend tests across 49 files passed; lint has 0 errors and the same 13 existing warnings. Release and production frontend builds passed.

## Follow-up to review of `397030a`

| # | Finding | Disposition and evidence |
| --- | --- | --- |
| 62 | [Inactive Arr configuration portal](https://github.com/Feramance/Torrentarr/pull/520#discussion_r4366985140) | The existing Arr dialog now uses the same active-panel guard as the other configuration dialogs. The lifecycle regression covers both Web Settings and Arr dialogs, including scroll-lock release while inactive, restoration on return, and Escape dismissal. |

## Deferred existing concerns

Thumbnail redirect-origin hardening and atomic cache publication predate this branch and remain separate follow-ups. Live provider OIDC, real destructive maintenance/update operations, and Windows/macOS standalone execution require their appropriate environments. Existing Docker Compose bytes and staging are preserved.
