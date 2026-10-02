#!/usr/bin/env bash
set -euo pipefail

QBITRR_REF="${QBITRR_OPENAPI_REF:-v5.14.5-1}"
TORRENTARR_SPEC="${1:-docs/assets/openapi.json}"
TMP_QBITRR="$(mktemp)"
trap 'rm -f "$TMP_QBITRR"' EXIT

curl -fsSL "https://raw.githubusercontent.com/Feramance/qBitrr/${QBITRR_REF}/qBitrr/openapi.json" -o "$TMP_QBITRR"
python3 scripts/check-openapi-drift.py "$TORRENTARR_SPEC" "$TMP_QBITRR"
