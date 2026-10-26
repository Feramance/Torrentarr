# Manage verification

All ten original maintenance tools are available through Manage. Current behavior and compatibility policy are in the
[operator guide](../features/torrent-maintenance.md).

## Automated checks

```bash
dotnet test --filter 'Category!=Live'
dotnet run --project tools/Torrentarr.ConfigSchemaGenerator -- --check
cd webui
npm test -- --run
npm run build
npm run lint
```

Run script tests with `python3 -m unittest discover -s tests/scripts -p 'test_*.py'`.
Build docs with `TORRENTARR_DOCS_GIT_DATES=false mkdocs build --strict` when checking
uncommitted files, then run `python3 scripts/check-docs.py`. Git dates are disabled
only to avoid warnings for files with no Git history.

The infrastructure suite includes real independent-process recovery claims against
a temporary SQLite database. Its explicit process-probe entry point is used only
by that test. No real qBittorrent or Arr service is contacted.

## Required safety cases

- Saved previews are single-use, expire, and invalidate after configuration changes.
- All ten tools support Manual and Automatic execution. Legacy arm/disarm routes return 410; draft previews cannot execute. Session configuration migrates to Manual without losing schedules or other settings.
- Disabled/unavailable owners and inaccessible or unsafe paths block orphan moves.
- Zero action budgets do not mutate files or expand scan scope.
- Rechecks are bounded, survive restart, and cannot be duplicated across processes.
- Missing-files recovery never falls through into automatic removal.
- Deliberate pauses remain paused; protection runs with cleanup/recovery disabled.
- Recycling records intent before moves. Uncertain removals and failed rollback
  preserve recovery paths and remain excluded from purging after restart.
- Retention starts at committed recycling, including directories with old contents.
- Legacy contents receive a reviewed registration and a full retention period.
- Actual-file purge caps, changed-file rejection, shared bins, and symlinks are safe.
- Client rename preserves recovery/protection evidence and updates recycle paths.

## Visual verification

Use disposable synthetic API fixtures with the real React components and CSS.
Check desktop and a narrow mobile viewport: no page overflow, clear partial/blocked
states, separate recycled/reclaimed bytes, cancellation, and disabled repeated apply.
Opening Manage must not trigger a scan. Manage is the only maintenance editor, with one tool card expanded at a time, shared settings in one place, and advanced controls collapsed. Client shortcuts select the saved client and preserve unrelated drafts.

Do not test deletion against production downloads. Live-service checks require
explicitly disposable torrents and temporary storage.
