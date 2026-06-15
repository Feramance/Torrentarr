# PR triage — `gh` actions (2026-06-15 audit)

Copy-paste commands for maintainer follow-up after [pr-triage-2026-06-15.md](pr-triage-2026-06-15.md). Run from any clone with `gh` authenticated to `Feramance/Torrentarr`.

## Phase A — Ready winners for CI

```bash
for n in 229 271 258 243; do gh pr ready $n; done
```

## Phase B — Merge winners (in order; wait for CI green between each)

```bash
gh pr merge 229 --squash --delete-branch
gh pr merge 271 --squash --delete-branch
gh pr merge 258 --squash --delete-branch
gh pr merge 243 --squash --delete-branch
```

If a PR needs rebase before merge:

```bash
gh pr checkout 271 && git fetch origin master && git rebase origin/master && git push --force-with-lease
```

## Phase C — Close duplicates (still open as of audit)

```bash
gh pr close 250 --comment "Superseded by #229 (tagless instance scoping). See docs/audits/pr-triage-2026-06-15.md"
gh pr close 248 --comment "Superseded by #229 + #271. See docs/audits/pr-triage-2026-06-15.md"
```

If these were already closed, `gh` will report that — no harm.

Historical duplicates (likely already closed): #127, #188, #197, #247, #251, #253, #254, #255, #256, #260, #265, #266, #267, #268, #270.

Bulk close template if any reopen:

```bash
gh pr close <N> --comment "Superseded by audit winner — see docs/audits/pr-triage-2026-06-15.md"
```

## Phase D — Cherry-pick CF-unmet HnR guard from #255

```bash
git fetch origin pull/255/head:pr-255-ref
git checkout -b cursor/cf-unmet-hnr-guard-e585 origin/master
git log pr-255-ref --oneline -- src/Torrentarr.Infrastructure/Services/TorrentProcessor.cs | head -5
# Cherry-pick the commit that adds HnrAllowsDeleteAsync before CF-unmet delete, then:
dotnet test --filter "Category!=Live"
git push -u origin cursor/cf-unmet-hnr-guard-e585
gh pr create --base master --title "fix: HnR guard before CF-unmet torrent delete" --body "Cherry-pick from #255. See docs/audits/pr-triage-2026-06-15.md"
```

## Phase E — Defer (merge later, after critical fixes)

```bash
# No action now. When ready:
gh pr merge 234 --squash --delete-branch   # vitest coverage-v8
gh pr merge 238 --squash --delete-branch   # tailwind postcss
gh pr merge 239 --squash --delete-branch   # react-hook-form
gh pr merge 264 --squash --delete-branch   # Extensions.Http
gh pr merge 269 --squash --delete-branch   # Logging.Abstractions
```

## Phase F — Partial / follow-up

```bash
# #231 — merge only after #229 lands
gh pr ready 231
gh pr checkout 231 && git fetch origin master && git rebase origin/master && git push --force-with-lease
gh pr merge 231 --squash --delete-branch
```

## Phase G — Land audit + automation docs

```bash
for n in 272 273; do gh pr ready $n; done
gh pr merge 272 --squash --delete-branch
gh pr merge 273 --squash --delete-branch
```

## Inspect before acting

```bash
gh pr list --state open --limit 50
gh pr checks 229
gh pr view 229 --json mergeable,mergeStateStatus,statusCheckRollup
```
