#!/usr/bin/env bash
# Derives the .NET SDK major.minor from the repo's TargetFramework (e.g. net10.0 -> 10.0)
# and writes global.json with that version (minimum patch 100) so any installed patch works.
# Echoes the major.minor to stdout for use in CI (e.g. setup-dotnet with "10.0.x").
set -e
REPO_ROOT="${1:-.}"
# Normalize for Windows (Git Bash): convert backslashes to forward slashes so find works
REPO_ROOT="${REPO_ROOT//\\//}"
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
# Write global.json only if content would change (avoids dirty working tree from pre-commit).
# Normalize line endings (strip CR) so CRLF vs LF does not cause unnecessary overwrites.
GLOBAL_JSON="$REPO_ROOT/global.json"
NEW_CONTENT="{
  \"sdk\": {
    \"rollForward\": \"latestMinor\",
    \"version\": \"${TFM}.100\"
  }
}
"
if [ -f "$GLOBAL_JSON" ]; then
  CURRENT=$(tr -d '\r' < "$GLOBAL_JSON")
  NEW_NORM=$(echo "$NEW_CONTENT" | tr -d '\r')
  if [ "$CURRENT" = "$NEW_NORM" ]; then
    : # no change
  else
    echo "$NEW_CONTENT" > "$GLOBAL_JSON"
  fi
else
  echo "$NEW_CONTENT" > "$GLOBAL_JSON"
fi
echo "$TFM"
