#!/usr/bin/env bash
set -euo pipefail

if [[ ${1:-} != [1-9]* || ${1:-} == *[!0-9]* ]]; then
    echo "Usage: bash scripts/build-pr.sh PR_NUMBER" >&2
    exit 2
fi

cd "$(dirname "$0")/.."
project_version=$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' Directory.Build.props)
state_dir="$(git rev-parse --absolute-git-dir)/torrentarr"
mkdir -p "$state_dir"
state_file="$state_dir/${project_version}-pr.$1.cfg"
if [[ ! -f "$state_file" ]]; then
    cp .bumpversion-pr.cfg "$state_file"
    printf 'current_version = %s-pr.%s.0.0\n' "$project_version" "$1" >> "$state_file"
fi

bump2version --allow-dirty --config-file "$state_file" attempt
version=$(sed -n 's/^current_version = //p' "$state_file")
VERSION="$version" docker compose up -d --build
