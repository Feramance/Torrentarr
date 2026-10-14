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
