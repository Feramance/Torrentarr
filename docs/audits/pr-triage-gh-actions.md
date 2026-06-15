# PR queue — merge or close only

Maintainer runs **only** the commands below. Rebases, conflict fixes, and `gh pr ready` are handled by the Cursor automation (or already done).

Refresh live status: `./scripts/pr-queue.sh`

Last updated after #229, #271, #243 merged to `master`; #258 conflicts resolved on branch.

---

## Merge (when CI green)

```bash
gh pr merge 258 --squash --delete-branch
gh pr merge 272 --squash --delete-branch
gh pr merge 273 --squash --delete-branch
gh pr merge 238 --squash --delete-branch
gh pr merge 239 --squash --delete-branch
gh pr merge 231 --squash --delete-branch
```

Recommended order: **258** (api/config guard) → **272** → **273** → dependabot **238/239** → **231** (v5 slice).

---

## Close (duplicates / superseded / conflicting)

```bash
gh pr close 250 --comment "Superseded by #229 on master"
gh pr close 248 --comment "Superseded by #229 + #271 on master"
gh pr close 274 --comment "Superseded by #229 on master"
gh pr close 275 --comment "Superseded by #271 on master"
gh pr close 276 --comment "Superseded by #271 on master"
gh pr close 277 --comment "Superseded by #229 + #271 on master"
gh pr close 264 --comment "Dependabot conflict; reopen after master settles"
gh pr close 269 --comment "Dependabot conflict; reopen after master settles"
```

---

## Optional: watch CI then merge

```bash
gh pr checks 258 --watch && gh pr merge 258 --squash --delete-branch
```
