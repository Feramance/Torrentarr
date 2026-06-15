#!/usr/bin/env bash
# Prints Cursor Automation setup instructions and validates repo files exist.
# Usage: ./scripts/print-pr-triage-automation-setup.sh

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
AUTO_DIR="$ROOT/.cursor/automations/torrentarr-pr-triage"

echo "=== Torrentarr PR Triage — Cursor Automation setup ==="
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

echo ""
echo "--- Dashboard settings ---"
echo "Name:        Torrentarr PR Triage"
echo "Repository:  Feramance/Torrentarr"
echo "Triggers:    Pull request opened, Pull request pushed"
echo "Tools:       Comment on pull request (only)"
echo "Permissions: Private (or Team Visible)"
echo "Initial:     Disabled — test before enabling"
echo ""
echo "--- Instructions field ---"
echo "Paste the contents of:"
echo "  .cursor/automations/torrentarr-pr-triage/prompt.md"
echo ""
echo "Or from GitHub after merge:"
echo "  https://github.com/Feramance/Torrentarr/blob/master/.cursor/automations/torrentarr-pr-triage/prompt.md"
echo ""
echo "--- Test ---"
echo "1. Save automation as DISABLED"
echo "2. Run manual test against PR #229 or #271"
echo "3. Compare comment to docs/audits/pr-triage-2026-06-15.md"
echo "4. Enable automation"
echo ""
echo "Prompt length: $(wc -c < "$AUTO_DIR/prompt.md") bytes"
