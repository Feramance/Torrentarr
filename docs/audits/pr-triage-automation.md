# Cursor Automation: Torrentarr PR Validation

Repo-managed automation that validates pull requests against `master`, checks merge conflicts, runs build/tests, and comments with a **Merge** or **Close** recommendation.

| File | Purpose |
|------|---------|
| [`.cursor/automations/torrentarr-pr-triage/automation.yaml`](../../.cursor/automations/torrentarr-pr-triage/automation.yaml) | Triggers, tools, metadata |
| [`.cursor/automations/torrentarr-pr-triage/prompt.md`](../../.cursor/automations/torrentarr-pr-triage/prompt.md) | Instructions to paste into dashboard |
| [`scripts/open-pr-triage-automation-editor.sh`](../../scripts/open-pr-triage-automation-editor.sh) | Opens Automations editor |
| [`scripts/print-pr-triage-automation-setup.sh`](../../scripts/print-pr-triage-automation-setup.sh) | Prints setup checklist |
| [`docs/audits/open-pr-triage-automation.html`](open-pr-triage-automation.html) | Browser launcher + copy prompt |

**Ground truth:** [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md). Re-baseline after major `master` merges.

> **Note:** Cursor does not yet auto-import `.cursor/automations/` from the repo. Create the automation once in the dashboard using the files above; the prompt stays version-controlled in git.

This automation is **PR validation and triage**, not a vulnerability scanner.

---

## Quick setup (5 minutes)

**Open the Automations editor:**

[**Create new automation**](https://cursor.com/automations/new)

Or locally:

```bash
./scripts/open-pr-triage-automation-editor.sh
```

Or open [`docs/audits/open-pr-triage-automation.html`](open-pr-triage-automation.html) in a browser (copy prompt + checklist).

### Dashboard steps

1. Sign in at [cursor.com/automations](https://cursor.com/automations) if prompted
2. **Name:** `Torrentarr PR Validation`
3. **Repository:** `Feramance/Torrentarr`
4. **Triggers:** Pull request opened, Pull request pushed
5. **Tools:** Comment on pull request **only** (no Slack, no approve/request-changes)
6. **Instructions:** paste from [prompt.md](../../.cursor/automations/torrentarr-pr-triage/prompt.md)
7. Save as **disabled**
8. **Test** on PR [#229](https://github.com/Feramance/Torrentarr/pull/229) or [#271](https://github.com/Feramance/Torrentarr/pull/271)
9. **Enable** when satisfied

---

## What the automation does

On each PR open or push:

1. Fetches `master` and the PR branch
2. Attempts merge with `master`; resolves conflicts locally when safe
3. Runs `dotnet build`, `dotnet test --filter "Category!=Live"`, and `npx vitest run` when source changes warrant it
4. Validates purpose, correctness, tests, hygiene, and overlap with other open PRs
5. **Takes all prep work** (rebase, conflict fix, push, `gh pr ready`, tests); **auto-closes** duplicates
6. Maintainer only runs **`gh pr merge`** or **`gh pr close`** — see [`pr-triage-gh-actions.md`](pr-triage-gh-actions.md) or `./scripts/pr-queue.sh`

---

## Dashboard settings reference

| Setting | Value |
|---------|-------|
| **Name** | Torrentarr PR Validation |
| **Repository** | `Feramance/Torrentarr` |
| **Triggers** | Pull request opened, Pull request pushed |
| **Tools** | Comment on pull request (only) |
| **Model** | Default cloud agent |
| **Permissions** | Private or Team Visible |
| **Initial state** | Disabled |

**Limitation:** GitHub triggers do not run on fork PRs ([docs](https://cursor.com/docs/cloud-agent/automations)).

---

## Updating the automation

When `prompt.md` changes on `master`, re-copy it into the dashboard Instructions field (until Cursor supports config-as-code import).

---

## Optional later upgrades

- **CI completed** trigger — re-validate after GitHub Actions finish
- **Memories** — track cluster winners across runs
- **Request reviewers** — for high-risk PRs after validation stabilizes
- **Scheduled** weekly run — full open-PR inventory

## Billing

Automations run cloud agents in **Max Mode**. Skip full test runs for lockfile-only Dependabot PRs (already in prompt).
