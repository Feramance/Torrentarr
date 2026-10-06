#!/usr/bin/env bash
set -euo pipefail

image="${1:-torrentarr:permission-smoke}"
container="torrentarr-permission-smoke-$$"
state_dir="$(mktemp -d)"
config_dir="${state_dir}/config"
host_uid="$(id -u)"
host_gid="$(id -g)"
mkdir -p "${config_dir}"

cleanup() {
    docker rm -f "${container}" >/dev/null 2>&1 || true
    docker run --rm --user 0:0 --entrypoint /bin/sh \
        -v "${state_dir}:/cleanup" "${image}" \
        -c "chown -R ${host_uid}:${host_gid} /cleanup" >/dev/null 2>&1 || true
    rm -rf "${state_dir}"
}
trap cleanup EXIT

if [[ "${TORRENTARR_SMOKE_SKIP_BUILD:-0}" != "1" ]]; then
    docker build --tag "${image}" .
fi

uid=12345
gid=12346
docker run --detach --name "${container}" \
    --env "PUID=${uid}" \
    --env "PGID=${gid}" \
    --volume "${config_dir}:/config" \
    "${image}" >"${state_dir}/container-id" 2>"${state_dir}/startup-error" || {
    cat "${state_dir}/startup-error"
    echo "Docker permission smoke test: container failed to start" >&2
    exit 1
}

ready=0
for _ in $(seq 1 60); do
    if docker exec "${container}" curl --fail --silent http://127.0.0.1:6969/health >/dev/null 2>&1; then
        ready=1
        break
    fi
    sleep 1
done
if [[ "${ready}" != "1" ]]; then
    docker logs "${container}"
    echo "Docker permission smoke test: /health did not become ready" >&2
    exit 1
fi

ids="$(docker exec "${container}" sh -c "awk '/^Uid:/{u=\$2} /^Gid:/{g=\$2} END{print u \" \" g}' /proc/1/status")" || {
    docker logs "${container}"
    echo "Docker permission smoke test: unable to inspect PID 1 identity" >&2
    exit 1
}
[[ "${ids}" == "${uid} ${gid}" ]] || {
    echo "Expected PID 1 to run as ${uid}:${gid}, got ${ids}" >&2
    exit 1
}

for path in /config /config/config.toml /config/torrentarr.db /config/logs /config/data-protection-keys /data; do
    docker exec "${container}" test -e "${path}" || {
        docker logs "${container}"
        echo "Expected ${path} to exist" >&2
        exit 1
    }
    owner="$(docker exec "${container}" stat -c '%u:%g' "${path}")" || {
        docker logs "${container}"
        echo "Docker permission smoke test: unable to inspect ${path}" >&2
        exit 1
    }
    [[ "${owner}" == "${uid}:${gid}" ]] || {
        echo "Expected ${path} to be owned by ${uid}:${gid}, got ${owner}" >&2
        exit 1
    }
done

docker restart "${container}" >/dev/null
restarted_ready=0
for _ in $(seq 1 60); do
    if docker exec "${container}" curl --fail --silent http://127.0.0.1:6969/health >/dev/null 2>&1; then
        restarted_ready=1
        break
    fi
    sleep 1
done
if [[ "${restarted_ready}" != "1" ]]; then
    docker logs "${container}"
    echo "Docker permission smoke test: restart lost /health" >&2
    exit 1
fi

version_output="$(docker run --rm "${image}" --version)"
[[ -n "${version_output}" ]]

generated_dir="${state_dir}/generated"
mkdir -p "${generated_dir}"
docker run --rm --env "PUID=${uid}" --env "PGID=${gid}" \
    --volume "${generated_dir}:/config" "${image}" --gen-config >/dev/null
test -s "${generated_dir}/config.toml"

docker rm -f "${container}" >/dev/null
docker run --rm --env PUID=not-a-number --volume "${config_dir}:/config" "${image}" --version \
    >"${state_dir}/invalid-output" 2>&1 && {
    cat "${state_dir}/invalid-output"
    echo "Invalid PUID unexpectedly succeeded" >&2
    exit 1
}
grep -q 'PUID must be a numeric ID' "${state_dir}/invalid-output"

docker run --rm --env PUID=4294967295 --volume "${config_dir}:/config" "${image}" --version \
    >"${state_dir}/out-of-range-output" 2>&1 && {
    cat "${state_dir}/out-of-range-output"
    echo "Out-of-range PUID unexpectedly succeeded" >&2
    exit 1
}
grep -q 'PUID must be no greater than 4294967294' "${state_dir}/out-of-range-output"

readonly_dir="${state_dir}/readonly"
mkdir -p "${readonly_dir}"
docker run --rm --env "PUID=${uid}" --env "PGID=${gid}" \
    --volume "${readonly_dir}:/config:ro" "${image}" --version \
    >"${state_dir}/readonly-output" 2>&1 && {
    cat "${state_dir}/readonly-output"
    echo "Read-only /config unexpectedly succeeded" >&2
    exit 1
}
grep -q 'config/data directories are not writable' "${state_dir}/readonly-output"

echo "Docker permission smoke test passed for ${uid}:${gid}"
