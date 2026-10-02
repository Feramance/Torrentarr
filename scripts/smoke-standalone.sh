#!/usr/bin/env bash
set -euo pipefail

binary=${1:?usage: smoke-standalone.sh <published executable>}
port=${2:-6969}
work_dir=$(mktemp -d)
cleanup() {
  if [[ -n "${pid:-}" ]]; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  rm -rf "$work_dir"
}
trap cleanup EXIT

extension=""
if [[ "$binary" == *.exe ]]; then
  extension=".exe"
fi
app="$work_dir/torrentarr$extension"
cp "$binary" "$app"
chmod +x "$app" 2>/dev/null || true

cat > "$work_dir/config.toml" <<CONFIG
[Settings]
ConfigVersion = "6.12.4"
LoopSleepTimer = 5
FailedCategory = "failed"
RecheckCategory = "recheck"
PingURLS = ["one.one.one.one"]

[WebUI]
Host = "127.0.0.1"
Port = $port
Token = "standalone-smoke-token"
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
LiveArr = false
CONFIG

touch "$work_dir/torrentarr.db"
TORRENTARR_CONFIG="$work_dir/config.toml" \
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work_dir/extract" \
  "$app" --repair-database

TORRENTARR_CONFIG="$work_dir/config.toml" \
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work_dir/extract" \
  "$app" >"$work_dir/torrentarr.log" 2>&1 &
pid=$!
base_url="http://127.0.0.1:$port"

for _ in $(seq 1 60); do
  if curl --fail --silent "$base_url/health" >/dev/null 2>&1; then
    break
  fi
  sleep 0.5
done

curl --fail --silent "$base_url/health" | grep -q '"healthy"'
index=$(curl --fail --silent --location "$base_url/ui/")
printf '%s\n' "$index" | grep -q '<html'
asset=$(printf '%s\n' "$index" | sed -n 's/.*src="\(\/assets\/[^" ]*\.js\)".*/\1/p' | head -n 1)
test -n "$asset"
curl --fail --silent --location "$base_url$asset" >/dev/null
curl --fail --silent -H 'Authorization: Bearer standalone-smoke-token' \
  "$base_url/api/openapi.json" | node -e 'JSON.parse(require("fs").readFileSync(0, "utf8"));'
