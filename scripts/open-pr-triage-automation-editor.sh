#!/usr/bin/env bash
# Opens the Cursor Automations editor for Torrentarr PR Validation setup.
set -euo pipefail

EDITOR_URL="https://cursor.com/automations/new"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HTML="$ROOT/docs/audits/open-pr-triage-automation.html"
PROMPT="$ROOT/.cursor/automations/torrentarr-pr-triage/prompt.md"

echo "Torrentarr PR Validation — open Automations editor"
echo ""
echo "  $EDITOR_URL"
echo ""
echo "After the editor opens:"
echo "  1. Name: Torrentarr PR Validation"
echo "  2. Repository: Feramance/Torrentarr"
echo "  3. Triggers: Pull request opened + Pull request pushed"
echo "  4. Tools: Comment on pull request only"
echo "  5. Instructions: paste from .cursor/automations/torrentarr-pr-triage/prompt.md"
echo "  6. Save disabled → test on #229 or #271 → enable"
echo ""

if [[ -f "$PROMPT" ]]; then
  echo "Prompt: $PROMPT ($(wc -c < "$PROMPT") bytes)"
  echo ""
fi

if command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$EDITOR_URL" 2>/dev/null || true
elif command -v open >/dev/null 2>&1; then
  open "$EDITOR_URL" 2>/dev/null || true
fi

if [[ -f "$HTML" ]]; then
  echo "Local launcher (copy prompt + checklist): file://$HTML"
fi
