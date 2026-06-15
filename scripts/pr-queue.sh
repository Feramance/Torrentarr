#!/usr/bin/env bash
# Prints merge/close commands only — no rebase, ready, or cherry-pick.
# Usage: ./scripts/pr-queue.sh [--json]
set -euo pipefail

REPO="${GITHUB_REPOSITORY:-Feramance/Torrentarr}"

# Static verdicts (automation updates pr-triage-gh-actions.md; this is the live overlay)
declare -A MERGE_REASON CLOSE_REASON

MERGE_REASON[258]="/api/config dotted-only (unique vs master)"
MERGE_REASON[272]="audit report"
MERGE_REASON[273]="PR validation automation spec"
MERGE_REASON[238]="dependabot tailwind postcss patch"
MERGE_REASON[239]="dependabot react-hook-form patch"
MERGE_REASON[231]="v5 import readiness (after #229 on master)"

CLOSE_REASON[250]="superseded by #229 on master"
CLOSE_REASON[248]="superseded by #229 + #271 on master"
CLOSE_REASON[274]="superseded by #229 on master"
CLOSE_REASON[275]="superseded by #271 on master"
CLOSE_REASON[276]="superseded by #271 on master"
CLOSE_REASON[277]="superseded by #229 + #271 on master"
CLOSE_REASON[264]="dependabot conflict"
CLOSE_REASON[269]="dependabot conflict"

json_mode=false
[[ "${1:-}" == "--json" ]] && json_mode=true

mapfile -t OPEN < <(gh pr list --repo "$REPO" --state open --limit 100 --json number --jq '.[].number')

merge_cmds=()
close_cmds=()

for n in "${OPEN[@]}"; do
  if [[ -n "${MERGE_REASON[$n]:-}" ]]; then
    merge_cmds+=("gh pr merge $n --squash --delete-branch  # ${MERGE_REASON[$n]}")
  elif [[ -n "${CLOSE_REASON[$n]:-}" ]]; then
    close_cmds+=("gh pr close $n --comment \"${CLOSE_REASON[$n]}\"")
  fi
done

if $json_mode; then
  printf '{"merge":[%s],"close":[%s]}\n' \
    "$(printf '"%s",' "${merge_cmds[@]}" | sed 's/,$//')" \
    "$(printf '"%s",' "${close_cmds[@]}" | sed 's/,$//')"
  exit 0
fi

echo "=== Torrentarr PR queue (merge or close only) ==="
echo ""
echo "## Merge"
if ((${#merge_cmds[@]})); then
  printf '%s\n' "${merge_cmds[@]}"
else
  echo "(none)"
fi
echo ""
echo "## Close"
if ((${#close_cmds[@]})); then
  printf '%s\n' "${close_cmds[@]}"
else
  echo "(none)"
fi
echo ""
echo "Open PRs not in queue: $(comm -23 <(printf '%s\n' "${OPEN[@]}" | sort) <(printf '%s\n' "${!MERGE_REASON[@]}" "${!CLOSE_REASON[@]}" | sort -u) | tr '\n' ' ')"
echo ""
echo "Full list: docs/audits/pr-triage-gh-actions.md"
