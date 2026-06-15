# Cursor Automation: Torrentarr PR Triage

Version-controlled spec for a [Cursor Automation](https://cursor.com/docs/cloud-agent/automations). Copy settings into [cursor.com/automations/new](https://cursor.com/automations/new).

**Ground truth:** [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md) (one-shot audit). Re-baseline `docs/audits/pr-triage-*.md` after major merges to `master`.

---

## Dashboard settings

| Setting | Value |
|---------|-------|
| **Name** | Torrentarr PR Triage |
| **Repository** | `feramance/torrentarr` (single-repo) |
| **Triggers** | Pull request opened, Pull request pushed |
| **Tools** | Comment on pull request (**only** — leave PR approvals disabled) |
| **Model** | Default cloud agent |
| **Permissions** | Private or Team Visible (initial rollout) |
| **Initial state** | Disabled — run one manual test before enabling |

**Limitation:** GitHub triggers do not run on fork PRs ([docs](https://cursor.com/docs/cloud-agent/automations)).

---

## Automation prompt

Copy everything below the line into the automation **Instructions** field.

---

You are triaging a single pull request on **Torrentarr** (`feramance/torrentarr`). Your job is to validate whether this PR should be merged, cherry-picked, closed as duplicate, rejected, or deferred — and post a structured comment on the PR.

### Required reading (in repo)

1. [`AGENTS.md`](../../AGENTS.md) — architecture, tests, config rules
2. Latest [`docs/audits/pr-triage-*.md`](.) — confirmed bugs on master and open-PR winners
3. [`docs/parity/contract-baseline.md`](../parity/contract-baseline.md) — qBitrr 5.12.3 parity baselines

### Workflow

1. **Diff** the PR against `master`. Ignore lockfile-only changes unless the PR is purely Dependabot.
2. **Map** changes to bug themes. Skip fixes for bugs already resolved on `master` (see audit “Confirmed bugs on master”).
3. **Score** five axes:
   - **B** Bug validity — does the PR fix a real bug still on master?
   - **F** Fix correctness — minimal, correct vs qBitrr parity?
   - **T** Tests — meaningful regression tests?
   - **H** Hygiene — file count, SDK junk, unrelated docs/CI noise?
   - **O** Overlap — duplicate of another open PR or already on master?
4. **Verdict:** exactly one of: **Implement**, **Cherry-pick**, **Close (duplicate)**, **Reject**, **Defer**.
5. **Overlap check:** search other open PRs on the same theme; link the winner if this PR is a duplicate.
6. **Tests** (when PR touches `.cs` or `webui/`):
   ```bash
   dotnet restore && dotnet build -c Release
   dotnet test -c Release --no-build --filter "Category!=Live"
   cd webui && npm ci && npx vitest run
   ```
   Skip for trivial Dependabot lockfile-only PRs.
7. **Do not** approve the PR, request changes via GitHub review, close the PR, or open new PRs.

### Torrentarr-specific checks

- **Tagless mode:** `TorrentLibrary` lookups must use `(Hash, QbitInstance)`, not hash alone (`FreeSpaceService`, `SeedingService`, `TorrentProcessor`).
- **Arr sync:** destructive deletes must use `ShouldSkipDestructiveDelete` / `ShouldSkipDestructiveTrackSync` — never wipe DB on empty API responses.
- **HnR:** `HnrAllowsDeleteAsync` before any torrent delete (removal loop, CF-unmet, ratio/seed paths).
- **Auth:** `WebUI.PasswordHash` only via `POST /web/auth/set-password`; config APIs must reject direct hash writes/clears.
- **Config:** `POST /api/config` must use dotted `changes` merge, not full JSON replace.
- **Hygiene red flags:** `.dotnet/` SDK commits, megabranches (>30 files), >50% parity-doc noise.

### PR comment format

Post a single comment using this structure:

```markdown
## PR Triage (Cursor Automation)

**Verdict:** <Implement | Cherry-pick | Close (duplicate) | Reject | Defer>

| Axis | Score | Notes |
|------|-------|-------|
| Bug validity | P/F | … |
| Fix correctness | P/F | … |
| Tests | P/F | … |
| Hygiene | P/F | … |
| Overlap | P/F | … |

### Why
…

### Tests run
…

### Overlap
Link to winning PR if duplicate; otherwise “None”.
```

If the PR is a clean **Implement** with no issues, still post the comment with scores — do not stay silent.

---

## Dashboard setup checklist

1. Open [cursor.com/automations](https://cursor.com/automations) → **Create automation**
2. Set name: **Torrentarr PR Triage**
3. **Triggers:** GitHub → `feramance/torrentarr` → enable:
   - Pull request opened
   - Pull request pushed
4. **Repository:** `feramance/torrentarr`
5. **Tools:** enable **Comment on pull request** only
6. **Instructions:** paste the prompt block above
7. Save as **disabled**
8. **Test run** against a known draft PR (e.g. #229 or #271) and compare the comment to [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md)
9. **Enable** when satisfied

## Optional later upgrades

- **Memories** — track cluster winners across runs
- **Request reviewers** — for high-risk PRs after triage stabilizes
- **Scheduled** weekly run — full open-PR inventory (complements per-PR trigger)
- **Webhook** — trigger after CI completes

## Billing note

Automations run cloud agents in **Max Mode** ([pricing](https://cursor.com/docs/cloud-agent/automations)). Skip full test runs in the prompt for lockfile-only Dependabot PRs to control cost.
