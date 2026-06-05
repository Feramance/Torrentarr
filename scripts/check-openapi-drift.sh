#!/usr/bin/env bash
# Compare Torrentarr documented OpenAPI paths against qBitrr 5.12.3 (behavioral drift check).
set -euo pipefail

QBITRR_REF="${QBITRR_OPENAPI_REF:-5.12.3}"
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
# Torrentarr may document a subset; fail only when Torrentarr declares a path qBitrr dropped.
missing_upstream = sorted(ta_paths - qb_paths)
if missing_upstream:
    print("Torrentarr OpenAPI paths not present in qBitrr pin:")
    for p in missing_upstream:
        print(" ", p)
    sys.exit(1)
print(f"OK: {len(ta_paths)} Torrentarr paths are a subset of {len(qb_paths)} qBitrr paths.")
PY

rm -f "$TMP_QBITRR"
