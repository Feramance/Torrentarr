#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."

version=$(sed -n 's/^current_version = //p' .bumpversion.cfg)
revision=$(git rev-parse HEAD)
test -n "$version"
printf '%s+%s\n' "$version" "${revision:0:12}"
