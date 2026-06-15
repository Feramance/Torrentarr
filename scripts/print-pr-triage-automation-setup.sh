#!/usr/bin/env bash
# Prints Cursor Automation setup instructions and validates repo files exist.
# Usage: ./scripts/print-pr-triage-automation-setup.sh

set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
AUTO_DIR="$ROOT/.cursor/automations/torrentarr-pr-triage"

echo "=== Torrentarr PR Validation — Cursor Automation setup ==="
echo ""
echo "Validates PRs (merge conflicts + build/tests) and comments Merge or Close."
echo "Not a vulnerability scanner."
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
echo "Name:        Torrentarr PR Validation"
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
echo "3. Expect Merge or Close comment with gate results"
echo "Maintainer queue (merge/close only): ./scripts/pr-queue.sh"
echo ""
echo "Prompt length: $(wc -c < "$AUTO_DIR/prompt.md") bytes"
