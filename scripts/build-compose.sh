#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(bash scripts/build-version.sh)
VERSION="$version" docker compose up -d --build "$@"
