# Cursor Automation: Torrentarr PR Triage

Repo-managed automation definition for [Cursor Automations](https://cursor.com/docs/cloud-agent/automations).

| File | Purpose |
|------|---------|
| [`.cursor/automations/torrentarr-pr-triage/automation.yaml`](../../.cursor/automations/torrentarr-pr-triage/automation.yaml) | Triggers, tools, metadata |
| [`.cursor/automations/torrentarr-pr-triage/prompt.md`](../../.cursor/automations/torrentarr-pr-triage/prompt.md) | Instructions to paste into dashboard |
| [`scripts/print-pr-triage-automation-setup.sh`](../../scripts/print-pr-triage-automation-setup.sh) | Prints setup checklist |

**Ground truth:** [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md). Re-baseline after major `master` merges.

> **Note:** Cursor does not yet auto-import `.cursor/automations/` from the repo. Create the automation once in the dashboard using the files above; the prompt stays version-controlled in git.

---

## Quick setup (5 minutes)

```bash
./scripts/print-pr-triage-automation-setup.sh
```

1. Open [cursor.com/automations/new](https://cursor.com/automations/new)
2. **Name:** `Torrentarr PR Triage`
3. **Triggers:** GitHub → `Feramance/Torrentarr` → **Pull request opened** + **Pull request pushed**
4. **Repository:** `Feramance/Torrentarr`
5. **Tools:** **Comment on pull request** only (leave approvals off)
6. **Instructions:** paste entire contents of [`.cursor/automations/torrentarr-pr-triage/prompt.md`](../../.cursor/automations/torrentarr-pr-triage/prompt.md)
7. Save as **disabled**
8. **Test** on PR [#229](https://github.com/Feramance/Torrentarr/pull/229) or [#271](https://github.com/Feramance/Torrentarr/pull/271); compare to [`pr-triage-2026-06-15.md`](pr-triage-2026-06-15.md)
9. **Enable** when satisfied

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
