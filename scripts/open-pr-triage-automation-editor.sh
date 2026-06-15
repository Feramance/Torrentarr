#!/usr/bin/env bash
# Opens the Cursor Automations editor pre-filled via marketplace bootstrap template.
# After the page loads: set repo, disable Slack, paste prompt (see HTML launcher), Create.
set -euo pipefail

# Bootstrap: PR opened + PR pushed + PR Comment (same triggers/tools as our spec).
# Replace the template security prompt with .cursor/automations/torrentarr-pr-triage/prompt.md
EDITOR_URL="https://cursor.com/automations/new?templateId=find-vulnerabilities"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
HTML="$ROOT/docs/audits/open-pr-triage-automation.html"

echo "Torrentarr PR Triage — open Automations editor"
echo ""
echo "  $EDITOR_URL"
echo ""
echo "After the editor opens:"
echo "  1. Name: Torrentarr PR Triage"
echo "  2. Repository: Feramance/Torrentarr"
echo "  3. Tools: disable Slack (keep PR Comment only)"
echo "  4. Instructions: paste from .cursor/automations/torrentarr-pr-triage/prompt.md"
echo "  5. Save disabled → test on #229 or #271 → enable"
echo ""

if command -v xdg-open >/dev/null 2>&1; then
  xdg-open "$EDITOR_URL" 2>/dev/null || true
elif command -v open >/dev/null 2>&1; then
  open "$EDITOR_URL" 2>/dev/null || true
fi

if [[ -f "$HTML" ]]; then
  echo "Local launcher (copy prompt + checklist): file://$HTML"
fi
