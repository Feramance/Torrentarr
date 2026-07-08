#!/usr/bin/env bash
# Compare Torrentarr documented OpenAPI paths against qBitrr latest master (behavioral drift check).
set -euo pipefail

QBITRR_REF="${QBITRR_OPENAPI_REF:-master}"
TORRENTARR_SPEC="${1:-docs/assets/openapi.json}"
TMP_QBITRR="$(mktemp)"

curl -fsSL "https://raw.githubusercontent.com/Feramance/qBitrr/${QBITRR_REF}/qBitrr/openapi.json" -o "$TMP_QBITRR"

python3 - <<'PY' "$TORRENTARR_SPEC" "$TMP_QBITRR"
import json, sys
ta_path, qb_path = sys.argv[1], sys.argv[2]
ta = json.load(open(ta_path))
qb = json.load(open(qb_path))
ta_paths = set(ta.get("paths", {}))
qb_paths = set(qb.get("paths", {}))

missing_in_ta = sorted(qb_paths - ta_paths)
extensions = sorted(ta_paths - qb_paths)

if missing_in_ta:
    print(f"Torrentarr OpenAPI missing {len(missing_in_ta)} qBitrr path(s):")
    for p in missing_in_ta:
        print(" ", p)
    sys.exit(1)

print(f"OK: {len(ta_paths)} Torrentarr paths cover all {len(qb_paths)} qBitrr paths.", end="")
if extensions:
    print(f" (+{len(extensions)} Torrentarr extensions: {', '.join(extensions)})")
else:
    print()
PY

rm -f "$TMP_QBITRR"
