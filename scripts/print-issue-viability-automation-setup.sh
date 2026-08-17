#!/usr/bin/env bash
# Prints Cursor Automation setup instructions and validates repo files exist.
# Usage: ./scripts/print-issue-viability-automation-setup.sh

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
AUTO_DIR="$ROOT/.cursor/automations/torrentarr-issue-viability"

echo "=== Torrentarr Issue Viability — Cursor Automation setup ==="
echo ""
echo "Researches issues on open/comment; implements viable bugs; opens a non-draft PR;"
echo "repairs CI until green; then requests human review."
echo ""
echo "Cursor Automations are created in the dashboard (no public create API yet)."
echo "Open: https://cursor.com/automations/new"
echo ""

for f in automation.yaml prompt.md; do
  if [[ -f "$AUTO_DIR/$f" ]]; then
    echo "✓ $AUTO_DIR/$f"
  else
    echo "✗ missing: $AUTO_DIR/$f" >&2
    exit 1
  fi
done

if [[ -f "$ROOT/.github/workflows/issue-viability.yml" ]]; then
  echo "✓ $ROOT/.github/workflows/issue-viability.yml"
else
  echo "✗ missing: $ROOT/.github/workflows/issue-viability.yml" >&2
  exit 1
fi

echo ""
echo "--- Dashboard settings ---"
echo "Name:        Torrentarr Issue Viability"
echo "Repository:  Feramance/Torrentarr"
echo "Triggers:    Issue comment, Issue label changed (cursor-viability),"
echo "             Webhook, Workflow run completed"
echo "Tools:       Pull request creation, Request reviewers, Memories"
echo "Permissions: Private (or Team Visible)"
echo "Initial:     Disabled — test before enabling"
echo ""
echo "--- Issue-comment filters (if the dashboard supports them) ---"
echo "Ignore authors: cursor, cursor[bot], github-actions[bot], dependabot[bot]"
echo "Ignore body:    <!-- torrentarr-issue-viability -->"
echo ""
echo "--- Instructions field ---"
echo "Paste the contents of:"
echo "  .cursor/automations/torrentarr-issue-viability/prompt.md"
echo ""
echo "Or from GitHub after merge:"
echo "  https://github.com/Feramance/Torrentarr/blob/master/.cursor/automations/torrentarr-issue-viability/prompt.md"
echo ""
echo "--- GitHub Action bridge ---"
echo "On issue opened, .github/workflows/issue-viability.yml adds the"
echo "cursor-viability label (Issue label changed trigger)."
echo "Optional webhook fallback (when the label trigger is missing in the UI):"
echo "  CURSOR_AUTOMATION_WEBHOOK_URL"
echo "  CURSOR_AUTOMATION_WEBHOOK_KEY"
echo "Copy the webhook URL and Bearer token from the automation after first save."
echo ""
echo "--- Test ---"
echo "1. Confirm Cursor GitHub App is connected: https://cursor.com/dashboard/integrations"
echo "2. Save automation as DISABLED"
echo "3. Open a test bug issue; expect cursor-viability label + research comment"
echo "4. For a viable bug, expect a non-draft PR (Build and Test skips drafts)"
echo "5. When CI is green, expect a reviewer request and a human-review comment"
echo "6. Enable the automation. Keep Cloud Agent \"Automatically fix CI Failures\" on"
echo ""
echo "Prompt length: $(wc -c < "$AUTO_DIR/prompt.md") bytes"
