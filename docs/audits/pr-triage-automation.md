# Cursor Automation: Torrentarr PR Triage

Repo-managed automation definition for [Cursor Automations](https://cursor.com/docs/cloud-agent/automations).

| File | Purpose |
|------|---------|
| [`.cursor/automations/torrentarr-pr-triage/automation.yaml`](../../.cursor/automations/torrentarr-pr-triage/automation.yaml) | Triggers, tools, metadata |
| [`.cursor/automations/torrentarr-pr-triage/prompt.md`](../../.cursor/automations/torrentarr-pr-triage/prompt.md) | Instructions to paste into dashboard |
| [`scripts/open-pr-triage-automation-editor.sh`](../../scripts/open-pr-triage-automation-editor.sh) | Opens pre-filled editor URL |
| [`scripts/print-pr-triage-automation-setup.sh`](../../scripts/print-pr-triage-automation-setup.sh) | Prints setup checklist |
| [`docs/audits/open-pr-triage-automation.html`](open-pr-triage-automation.html) | Browser launcher + copy prompt |

**Ground truth:** [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md). Re-baseline after major `master` merges.

> **Note:** Cursor does not yet auto-import `.cursor/automations/` from the repo. Create the automation once in the dashboard using the files above; the prompt stays version-controlled in git.

---

## Quick setup (5 minutes)

**One click (recommended):** open the pre-filled editor, then paste the prompt:

[**Open Automations editor (pre-filled)**](https://cursor.com/automations/new?templateId=find-vulnerabilities)

Or locally: open [`docs/audits/open-pr-triage-automation.html`](open-pr-triage-automation.html) in a browser (copy prompt + checklist).

```bash
./scripts/open-pr-triage-automation-editor.sh
```

The bootstrap template `find-vulnerabilities` pre-selects **PR opened**, **PR pushed**, and **PR Comment** — same triggers/tools as this spec. You still replace the template prompt with ours and disable Slack.

1. Click the pre-filled editor link above (sign in to Cursor if prompted)
2. **Name:** `Torrentarr PR Triage`
3. **Repository:** `Feramance/Torrentarr` (triggers PR opened + pushed should already be set)
4. **Tools:** disable **Slack**; keep **Comment on pull request** only
5. **Instructions:** replace template text — paste from [prompt.md](../../.cursor/automations/torrentarr-pr-triage/prompt.md) or use **Copy Torrentarr prompt** in the HTML launcher
6. Save as **disabled**
7. **Test** on PR [#229](https://github.com/Feramance/Torrentarr/pull/229) or [#271](https://github.com/Feramance/Torrentarr/pull/271); compare to [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md)
8. **Enable** when satisfied

---

## Dashboard settings reference

| Setting | Value |
|---------|-------|
| **Name** | Torrentarr PR Triage |
| **Repository** | `Feramance/Torrentarr` (single-repo) |
| **Triggers** | Pull request opened, Pull request pushed |
| **Tools** | Comment on pull request (**only**) |
| **Model** | Default cloud agent |
| **Permissions** | Private or Team Visible |
| **Initial state** | Disabled |

**Limitation:** GitHub triggers do not run on fork PRs ([docs](https://cursor.com/docs/cloud-agent/automations)).

---

## Updating the automation

When `prompt.md` changes on `master`, re-copy it into the dashboard Instructions field (until Cursor supports config-as-code import).

---

## Optional later upgrades

- **Memories** — track cluster winners across runs
- **Request reviewers** — for high-risk PRs after triage stabilizes
- **Scheduled** weekly run — full open-PR inventory
- **Webhook** — trigger after CI completes

## Billing

Automations run cloud agents in **Max Mode**. Skip full test runs for lockfile-only Dependabot PRs (already in prompt).
