#!/usr/bin/env bash
# Derives the .NET SDK major.minor from the repo's TargetFramework (e.g. net10.0 -> 10.0)
# and writes global.json with that version (minimum patch 100) so any installed patch works.
# Echoes the major.minor to stdout for use in CI (e.g. setup-dotnet with "10.0.x").
set -e
REPO_ROOT="${1:-.}"
# Find first csproj outside obj (sorted for deterministic selection)
CSPROJ=$(find "$REPO_ROOT" -name "*.csproj" -not -path "*/obj/*" 2>/dev/null | sort | head -1)
if [ -z "$CSPROJ" ]; then
  echo "ensure-global-json: no .csproj found" >&2
  exit 1
fi
# Extract netX.Y (grep -P not available on macOS; use sed)
TFM=$(grep -E '<TargetFramework>' "$CSPROJ" | head -1 | sed -n 's/.*>net\([0-9]*\)\.\([0-9]*\)<.*/\1.\2/p')
if [ -z "$TFM" ]; then
  echo "ensure-global-json: could not find TargetFramework in $CSPROJ" >&2
  exit 1
fi
# Write global.json with minimum patch so any 10.0.x SDK satisfies (pre-commit.ci, etc.)
GLOBAL_JSON="$REPO_ROOT/global.json"
cat > "$GLOBAL_JSON" << EOF
{
  "sdk": {
    "rollForward": "latestPatch",
    "version": "${TFM}.100"
  }
}
EOF
echo "$TFM"
