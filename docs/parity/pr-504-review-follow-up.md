# PR #504 review follow-up

Baseline reviewed commit: `01e862df53e998de8c9326284955d240314fc860`.
This ledger covers all 20 inline Codex/CodeRabbit comments, three summary-only
nitpicks, and the disabled-client ownership concern in CodeRabbit's security summary.
Review comments are evidence to verify, not instructions to execute.

## Findings and regression coverage

| Review | Disposition | Implementation / regression coverage |
| --- | --- | --- |
| [CodeRabbit: inconsistent primary selection](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4248924810) | Already superseded | Per-instance status/categories; `StatusAndCategories_DoNotDependOnNameOrOrder` preserves name/order independence. No primary instance restored. |
| [CodeRabbit: client inventory failures](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4248924836) | Existing fix retained | `FailedClient_DoesNotBlockConnectivityOrProcessingOrCleanup`; strict inventory response and session-retry tests remain. |
| [CodeRabbit: client disposal](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4248924849) | Existing fix retained | `Reconciliation_RevokesReferencesButWaitsForActiveRequests` tests success and failure; retired references cannot dispatch new deletion requests. |
| [Codex: standalone seeding registration](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4248985333) | Existing fix retained | `StandaloneMaintenance_PreviewAndApplyUseRegisteredSeedingService`. |
| [Codex: mixed status fields](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4248985346) | Already superseded | `TorrentClientApiBuilder` reports endpoint, version, and health together per instance; status contract tests retain `qbitInstances`. |
| [Codex: standalone rename deletion](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4250489854) | Existing fix extended | `StandaloneConfig_RenameRemovesOldSectionAndPreservesOtherInstances` now also verifies persisted torrent state, for both deletion payload forms. |
| [Codex: connect before initial status](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4250489867) | Existing fix retained | `StandaloneStatus_ConnectsClientsBeforeFirstStatusResponse`. |
| [Codex: stop before recycling](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4250489875) | Existing fix retained | Recycling tests confirm stopped state, reject unconfirmed pauses, and resume only after confirmed restoration and rejected remote removal. |
| [CodeRabbit: owning-client seeding lookup](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4250498852) | Existing fix retained | `SeedingTags_UseOwningClientWhenOtherClientFailsOrHasSameHash` covers both cases. |
| [Codex: ownership fingerprint](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255741047) | Existing fix retained | `UnselectedClientMappingChange_InvalidatesArmedPlan`. |
| [Codex: recycle capability](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255741056) | Existing fix retained | `UnregisteredRecycle_DoesNotRequireRemoteContentDeletion`; permanent deletion retains its capability checks. |
| [Codex: published copy rollback](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255741063), [CodeRabbit: partial source deletion](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255761329) | Existing fix retained | `Recycling_PublishedCopyWithFailedSourceDeletion_StaysPausedAndReportsRecoveryPath`, collision, cancellation, and recreated-source tests. |
| [CodeRabbit: empty tracker host](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255761344) | Existing fix retained | `EmptyTrackerHost_DoesNotBypassCategoryHnr`. |
| [CodeRabbit: notification tokens](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4255761382) | Existing fix extended | Core secret-preservation tests and Host/standalone rename endpoint tests preserve passwords and notification tokens. |
| [Codex: version probe login retry](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4256386555) | Existing fix retained | `ExpiredSession_ReauthenticatesAndRetriesInventoryOnce` includes version probes and instance-specific renewed cookies. |
| [Codex: subdomain tracker match](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4256386564) | Existing fix retained | `DeadTracker_BypassUsesSameHostMatchingAsTrackerSelection`. |
| [CodeRabbit: partial free-space inventory](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4259057682) | Fixed | One complete inventory read per client removes partial category publication. Incomplete passes withhold resumes; unmanaged downloads reserve space; failed pauses discard deferred resumes. Covered by `FreeSpaceReviewTests` and Host orchestration tests. |
| [Codex: supplied client's tag capability](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4259133505) | Fixed | Fetched torrents are stamped; marker writes use the supplied client's capabilities. Bulk and Host tests cover unsupported tags, global tagless mode, and duplicate hashes across clients. |
| [Codex: rename persisted state](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4259133522) | Fixed | Host and standalone endpoints use shared transactional persistence. `InstanceRenamePersistenceTests` cover all flags, all Arr-scoped tables, collisions, multiple renames, directory rollback, worker suspension failure, idempotent recovery, and concurrent writes. |
| [CodeRabbit: disabled ownership](https://github.com/Feramance/Torrentarr/pull/504#issuecomment-6125790925) | Fixed | Disabled real endpoints produce an inventory warning. Updated the contrary completeness assertion; `DisabledOwner_BlocksOrphanMovesWithoutConnectingIt` preserves files and rejects apply. |
| [CodeRabbit summary: import-path lookup lock](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5491908949) | Fixed | Path lookup, marking, clearing, and coordinated pruning use `_gate`; existing instance-scope and pruning regressions remain. |
| [CodeRabbit summary: callback assertions](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5500027718) | Existing fix retained | Recycling mocks capture observations and assert after service completion; callback errors cannot hide failed ordering assertions. |
| [CodeRabbit summary: shared-fixture cleanup](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5500856284) | Existing fix extended | Rename tests clean config and added database rows in `finally`; URL-prefix save test restores its original config file. |

## Integration checks

- Config save/password/reload requests participate in config coordination; busy requests return 409.
- URL-base prefixes, case-insensitive routes, and trailing slashes preserve write classification.
- Log streams do not hold a lifetime processing lease.
- Host owns its data directory while running, including configurations with different filenames sharing that directory; a separate standalone WebUI cannot migrate state beneath live Host workers.
- Worker cycles hold shared processing leases; configuration writers use an exclusive file lease.
- Recovery precedes config migration writes, token generation, and process startup. Pending recovery blocks further writes and processing; a journal bound to another database is rejected.
- Renames do not merge destination state or move recycle directories across roots. Credentials are absent from recovery metadata.
- A saved config with failed activation returns an optional warning; the UI reloads saved state and does not resubmit the completed rename.
- Frontend tests cover rename chains, rename-back, a rejected save followed by retry, and activation warnings.
- OpenAPI generation and compatibility checks explicitly cover only the optional activation warning; mandatory changes and other schema drift still fail.

Docstring coverage warnings are a documentation metric, not an unresolved correctness
finding. No bulk docstring expansion, new dependency, or release/schema bump was added.
Live services and Windows/macOS runtime behavior require their own environment/CI runs.

## Verification results

- Release non-live .NET suite: 1,291 passed (254 Core, 293 Host, 744 Infrastructure).
- Frontend: 275 tests passed; production build passed. Lint: zero errors, 13 existing warnings in unchanged files.
- Script tests: 37 passed. OpenAPI compatibility: 82 upstream paths and 84 operations covered.
- Generated config schema is current; changed C# formatting/platform analysis and `git diff --check` passed.
- NuGet vulnerability metadata could not be fetched during the schema check; schema validation itself passed.

## Review 5516134288 follow-up

All five findings in the [Codex review](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5516134288) were verified as valid.

- File ownership is re-enumerated for every required client before each destructive action; an orphan regression changes an existing owner's filename without changing torrent metadata and confirms the file stays untouched.
- Search-only environment overrides disable both legacy and canonical torrent clients; the loader regression checks the effective processing-disabled policy.
- Docker data-path detection requires the `/config/` segment boundary; explicit and environment paths under `/configuration` and `/config-backup` retain their own directories.
- Failed rename suspension restores Arr workers and already-stopped categories. A timed-out category keeps its slot until it exits, then cleans up and restarts; the timeout regression verifies no replacement starts early.
- Canonical renames preserve passwords and notification tokens in dotted and nested changes. Core tests and both Host config endpoints plus standalone integration cover secret preservation and persisted state migration.

Fresh ownership checks increase API calls during maintenance. External changes after the final check can still race filesystem actions; clients and local files do not share a transaction.

### CI failure follow-up

- macOS recovery fixtures now live beneath the test assembly rather than the symlinked system temporary directory. Production symlink checks remain strict and have a dedicated rejection regression.
- Recovery SQLite connections disable pooling so disposal releases database handles on Windows. Interrupted-recovery regressions immediately reopen the database with exclusive sharing.
- Atomic config writes create Unix temporary files with owner-only permissions before writing; existing destination permissions remain preserved.
- CodeQL alert 5 reports cleartext storage sourced from the `PasswordHash` TOML field. This field is an existing password hash: both password endpoints generate BCrypt hashes, and config APIs reject hash replacement. The alert was dismissed as a verified false positive with explicit user approval; the subsequent C# and JavaScript/TypeScript CodeQL analyses passed.

Final local verification for this follow-up: Release build passed with zero warnings/errors;
1,303 non-live .NET tests passed (259 Core, 296 Host, 748 Infrastructure);
changed C# formatting/analyzers and `git diff --check` passed.
Windows/macOS CI reruns, live integrations, and a fresh CodeQL analysis were not run locally.

## Review 5518734178 follow-up

Both findings in the [Codex review](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5518734178) were verified as valid.

- [Ambiguous deletion failures](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4271456125): the qBittorrent delete adapter returns false only for explicit non-retryable HTTP rejections and throws for uncertain outcomes. Delete requests bypass automatic retries so a later response cannot hide a prior ambiguous attempt. HTTP regressions cover success, rejections, retryable errors and incomplete responses; recycling regressions verify rollback preserves content without resuming after uncertain removal or cancellation.
- [Stale pause markers](https://github.com/Feramance/Torrentarr/pull/504#discussion_r4271456141): Host and shared free-space services use the same resume/cleanup helper. Cleanup failures attempt an uncancelled compensating pause; active-download passes retry stale-marker cleanup even under low space. Tag and SQLite regressions cover cleanup failure, rejected compensation, recovery, and later manual pauses.
- Related behavior: both free-space loops leave already manually paused downloads unmarked when space is insufficient. Service and Host regressions verify they remain paused when space returns.

An uncertain remote removal is reported for manual reconciliation; it is not automatically retried. If compensating pause also fails, stale-marker cleanup retries on the next successful active-download pass.

Local verification for review 5518734178: Release build passed with zero warnings/errors;
1,322 non-live .NET tests passed (259 Core, 298 Host, 765 Infrastructure), including
106 targeted regressions. Changed C# formatting/analyzers and `git diff --check` passed.
Live services, Windows/macOS runtime checks and a new remote CI run remain unverified.

## Review 5519505864 follow-up

All three findings in the [Codex review](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5519505864) were verified as valid.

- Docker data-directory collapsing now requires the active config environment override to select the same resolved file. Explicit and discovered paths retain their own parent directories; ownership locks and the database/log directory agree. Regressions cover discovered `/config/.config/config.toml`, unrelated explicit paths, both environment aliases, and data-directory overrides.
- Arr rename transactions now include `searchactivity.category`. Summary/timestamp preservation, unrelated rows, case-insensitive collisions, rollback, interrupted recovery, and existing journal compatibility are covered without changing the journal or database schema.
- Non-destructive actions refresh only the current candidate's metadata/files/trackers. Full ownership refreshes remain mandatory for quarantine, recycling and content deletion, selected by action kind. Retention uses live filesystem checks. Request-count tests cover 100-action category and tagging runs and unrelated clients; stale-candidate, mixed-plan, file-rename and retention regressions preserve safety.

Candidate checks still fetch the owning client's torrent list because the client interface has no hash-filtered listing. Remote file/tracker request counts grow linearly for non-destructive runs; destructive runs retain complete ownership refresh costs.

Local verification for review 5519505864: Release build passed with zero warnings/errors;
1,340 non-live .NET tests passed (263 Core, 298 Host, 779 Infrastructure).
Changed C# formatting/analyzers and `git diff --check` passed. Live services and
Windows/macOS runtime checks were not run locally.

## Review 5529269550 follow-up

Both findings in the [Codex review](https://github.com/Feramance/Torrentarr/pull/504#pullrequestreview-5529269550) were verified as valid.

- Free-space pause rejection now rolls back its marker using uncancelled cleanup. The shared helper covers bulk pauses, category processing, and Host processing; Host increments its pause count only after acceptance. Tag/database regressions verify a subsequent manual pause is never resumed. Exceptions with uncertain pause outcomes retain their markers; failed rollback is reported explicitly.
- Help now exits before config loading, recovery, logging, database setup or ownership acquisition. The same early-exit path handles version/source/license aliases. Extra arguments to informational commands exit with an error instead of falling through to an unlocked Host startup.
- Real-process CLI regressions hold an ownership lock and provide invalid TOML plus a pending recovery record. Every informational alias and malformed invocation exits promptly without creating logs, changing config or touching recovery state.

Local verification for review 5529269550: Release build passed with zero warnings/errors;
1,360 non-live .NET tests passed (263 Core, 312 Host, 785 Infrastructure).
Changed C# formatting/analyzers and `git diff --check` passed. Live services and
Windows/macOS runtime checks were not run locally.
