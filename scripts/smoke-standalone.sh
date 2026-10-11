#!/usr/bin/env bash
set -euo pipefail

binary=${1:?usage: smoke-standalone.sh <published executable>}
port=${2:-6969}
worker_binary=${3:?usage: smoke-standalone.sh <host> <port> <worker>}
script_dir=$(cd -- "$(dirname -- "$0")" && pwd)
work_dir=$(mktemp -d)
cleanup() {
  result=$?
  if [[ "$result" != 0 ]]; then
    [[ ! -f "$work_dir/torrentarr.log" ]] || tail -80 "$work_dir/torrentarr.log"
    find "$work_dir/logs" -type f -name '*.log' -exec tail -30 {} \; 2>/dev/null || true
  fi
  if [[ -n "${pid:-}" ]]; then
    kill "$pid" 2>/dev/null || true
    wait "$pid" 2>/dev/null || true
  fi
  if [[ -n "${stub_pid:-}" ]]; then kill "$stub_pid" 2>/dev/null || true; wait "$stub_pid" 2>/dev/null || true; fi
  rm -rf "$work_dir"
}
trap cleanup EXIT

extension=""
if [[ "$binary" == *.exe ]]; then
  extension=".exe"
fi
app="$work_dir/torrentarr$extension"
cp "$binary" "$app"
worker="$work_dir/torrentarr-worker$extension"
cp "$worker_binary" "$worker"
chmod +x "$app" "$worker" 2>/dev/null || true
node "$script_dir/smoke-arr.mjs" "$work_dir/arr-port" &
stub_pid=$!
for _ in $(seq 1 60); do [[ -s "$work_dir/arr-port" ]] && break; sleep 0.1; done
arr_port=$(cat "$work_dir/arr-port")

cat > "$work_dir/config.toml" <<CONFIG
[Settings]
ConfigVersion = "6.14.6"
FFprobeAutoUpdate = false
LoopSleepTimer = 5
FailedCategory = "failed"
RecheckCategory = "recheck"
PingURLS = ["http://127.0.0.1:$arr_port"]

[WebUI]
Host = "127.0.0.1"
Port = $port
Token = "standalone-smoke-token"
AuthDisabled = true
LocalAuthEnabled = false
OIDCEnabled = false
LiveArr = false

[qBit]
Disabled = true

[Radarr-Smoke]
Managed = true
URI = "http://127.0.0.1:$arr_port"
APIKey = "smoke"
Category = "smoke"
SearchOnly = true
[Radarr-Smoke.Search]
SearchMissing = false
CONFIG

touch "$work_dir/torrentarr.db"
TORRENTARR_CONFIG="$work_dir/config.toml" \
TORRENTARR_WORKER_PATH="$worker" \
DOTNET_BUNDLE_EXTRACT_BASE_DIR="$work_dir/extract" \
  "$app" --repair-database

TORRENTARR_CONFIG="$work_dir/config.toml" \
TORRENTARR_WORKER_PATH="$worker" \
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

validate_json() {
  if command -v node >/dev/null 2>&1; then
    node -e 'JSON.parse(require("fs").readFileSync(0, "utf8"));'
  elif command -v pwsh >/dev/null 2>&1; then
    pwsh -NoLogo -NoProfile -NonInteractive \
      -Command '$json = [Console]::In.ReadToEnd(); $null = $json | ConvertFrom-Json'
  elif command -v powershell.exe >/dev/null 2>&1; then
    powershell.exe -NoLogo -NoProfile -NonInteractive \
      -Command '$json = [Console]::In.ReadToEnd(); $null = $json | ConvertFrom-Json'
  else
    echo "No JSON validation runtime found (node, pwsh, or powershell.exe)" >&2
    return 127
  fi
}

curl --fail --silent -H 'Authorization: Bearer standalone-smoke-token' \
  "$base_url/api/openapi.json" | validate_json

status_file="$work_dir/workers/Radarr-Smoke.json"
wait_for_worker() {
  for _ in $(seq 1 60); do
    if node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1])); if(!s.torrent?.lastSuccess || !s.search?.lastSuccess) process.exit(1)' "$status_file" 2>/dev/null; then return 0; fi
    sleep 0.5
  done
  echo "Worker did not complete both processing loops" >&2
  return 1
}
wait_for_worker
worker_pid=$(node -e 'const s=JSON.parse(require("fs").readFileSync(process.argv[1])); if(s.instance!=="Radarr-Smoke" || s.pid===Number(process.argv[2]) || !s.torrent || !s.search) process.exit(1); console.log(s.pid)' "$status_file" "$pid")
kill -0 "$worker_pid"
curl --fail --silent -X POST "$base_url/web/processes/Radarr-Smoke/worker/restart" >/dev/null
for _ in $(seq 1 60); do
  next_pid=$(node -e 'console.log(JSON.parse(require("fs").readFileSync(process.argv[1])).pid)' "$status_file")
  [[ "$next_pid" != "$worker_pid" ]] && break
  sleep 0.5
done
[[ "$next_pid" != "$worker_pid" ]]
wait_for_worker
if kill -0 "$worker_pid" 2>/dev/null; then echo "Old worker survived restart" >&2; exit 1; fi
worker_pid=$next_pid
kill "$pid"
wait "$pid" || true
pid=""
for _ in $(seq 1 60); do kill -0 "$worker_pid" 2>/dev/null || break; sleep 0.5; done
if kill -0 "$worker_pid" 2>/dev/null; then
  kill "$worker_pid" || true
  echo "Worker survived Host shutdown" >&2
  exit 1
fi
echo "Standalone Host/worker startup, restart and shutdown passed"
