#!/bin/sh
set -eu

# Match the convention used by the other media-stack containers. These values
# are numeric IDs, not names, because bind mounts resolve ownership by ID.
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
export PUID PGID

validate_id() {
    name="$1"
    value="$2"

    case "${value}" in
        ''|*[!0-9]*)
            echo "Torrentarr: ${name} must be a numeric ID (received '${value}')." >&2
            exit 1
            ;;
    esac

    # Linux IDs are unsigned 32-bit values; reserve the all-ones value.
    normalized="$(printf '%s' "${value}" | sed 's/^0*//')"
    [ -n "${normalized}" ] || normalized=0
    if [ "${#normalized}" -gt 10 ] || {
        [ "${#normalized}" -eq 10 ] && [ "${normalized}" -gt 4294967294 ];
    }; then
        echo "Torrentarr: ${name} must be no greater than 4294967294 (received '${value}')." >&2
        exit 1
    fi
}

validate_id PUID "${PUID}"
validate_id PGID "${PGID}"

# Docker passes image arguments in place of CMD. Preserve the existing CLI
# contract so `docker run image --version` still invokes Torrentarr.Host.
if [ "$#" -eq 0 ]; then
    set -- /app/Torrentarr.Host
else
    case "$1" in
        -*) set -- /app/Torrentarr.Host "$@" ;;
    esac
fi

if [ "$(id -u)" -eq 0 ]; then
    config_dir=/config
    if [ -n "${TORRENTARR_CONFIG:-}" ]; then
        case "${TORRENTARR_CONFIG}" in
            /config|/config/*)
                ;;
            /*)
                config_dir="$(dirname -- "${TORRENTARR_CONFIG}")"
                ;;
            *)
                echo "Torrentarr: TORRENTARR_CONFIG must be an absolute path." >&2
                exit 1
                ;;
        esac
    fi
    [ -n "${config_dir}" ] || config_dir=.
    mkdir -p "${config_dir}"

    data_override="${TORRENTARR_OVERRIDES_DATA_PATH:-${QBITRR_OVERRIDES_DATA_PATH:-}}"
    data_dir="${data_override:-${config_dir}}"
    mkdir -p "${data_dir}"

    needs_chown=1
    if [ "${PUID}" -eq 0 ] && [ "${PGID}" -eq 0 ]; then
        needs_chown=0
    fi

    # Canonicalize before the root guard and recursive ownership repair. This
    # prevents values such as /state/.. or a relative .. from escaping it.
    config_dir="$(readlink -f "${config_dir}")"
    data_dir="$(readlink -f "${data_dir}")"
    if [ "${config_dir}" = "/" ] || [ "${data_dir}" = "/" ]; then
        echo "Torrentarr: config and data directories must not resolve to the filesystem root." >&2
        exit 1
    fi

    # Only the application state volume is normalized. Media/download mounts
    # can be large and are commonly shared with qBittorrent and the Arrs.
    if [ "${needs_chown}" -eq 1 ]; then
        if ! chown -R "${PUID}:${PGID}" "${config_dir}"; then
            echo "Torrentarr: warning: unable to apply ${PUID}:${PGID} ownership to all of ${config_dir}; existing files may still require host-side permission repair." >&2
        fi
    fi

    # /data is a declared volume used by the documented download layouts. Fix
    # only its mountpoint so bind-mounted media trees are never traversed.
    mkdir -p /data
    if [ "${needs_chown}" -eq 1 ]; then
        if ! chown "${PUID}:${PGID}" /data; then
            echo "Torrentarr: warning: unable to apply ${PUID}:${PGID} ownership to /data; host-side permission repair may be required." >&2
        fi
    fi

    if [ "${data_dir}" != "${config_dir}" ] && [ "${needs_chown}" -eq 1 ]; then
        if ! chown -R "${PUID}:${PGID}" "${data_dir}"; then
            echo "Torrentarr: warning: unable to apply ${PUID}:${PGID} ownership to ${data_dir}; host-side permission repair may be required." >&2
        fi
    fi

    if ! gosu "${PUID}:${PGID}" test -w "${config_dir}" || ! gosu "${PUID}:${PGID}" test -w "${data_dir}"; then
        echo "Torrentarr: config/data directories are not writable by ${PUID}:${PGID}. Set PUID/PGID to the owner of the mounted directories or fix their permissions." >&2
        exit 1
    fi

    # tini runs as the requested identity, so the application and all of its
    # children receive signals correctly without retaining root privileges.
    exec gosu "${PUID}:${PGID}" env HOME=/config /usr/bin/tini -- "$@"
fi

# An explicit Docker `user:` override means the entrypoint cannot chown the
# mount or switch identities. Keep that supported and make the mismatch clear.
if [ "$(id -u)" -ne "${PUID}" ]; then
    echo "Torrentarr: running as Docker user $(id -u):$(id -g); PUID/PGID are ignored because the container is already non-root." >&2
fi
export HOME=/config
exec /usr/bin/tini -- "$@"
