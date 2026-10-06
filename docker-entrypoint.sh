#!/bin/sh
set -eu

# Match the convention used by the other media-stack containers. These values
# are numeric IDs, not names, because bind mounts resolve ownership by ID.
PUID="${PUID:-1000}"
PGID="${PGID:-1000}"
export PUID PGID

case "${PUID}" in
    ''|*[!0-9]*)
        echo "Torrentarr: PUID must be a numeric user ID (received '${PUID}')." >&2
        exit 1
        ;;
esac
case "${PGID}" in
    ''|*[!0-9]*)
        echo "Torrentarr: PGID must be a numeric group ID (received '${PGID}')." >&2
        exit 1
        ;;
esac

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
    mkdir -p /config

    # Only the application state volume is normalized. Media/download mounts
    # can be large and are commonly shared with qBittorrent and the Arrs.
    if [ "${PUID}" -ne 0 ]; then
        chown -R "${PUID}:${PGID}" /config 2>/dev/null || true
    fi

    if ! gosu "${PUID}:${PGID}" test -w /config; then
        echo "Torrentarr: /config is not writable by ${PUID}:${PGID}. Set PUID/PGID to the owner of the mounted config directory or fix its permissions." >&2
        exit 1
    fi

    # tini runs as the requested identity, so the application and all of its
    # children receive signals correctly without retaining root privileges.
    exec gosu "${PUID}:${PGID}" /usr/bin/tini -- "$@"
fi

# An explicit Docker `user:` override means the entrypoint cannot chown the
# mount or switch identities. Keep that supported and make the mismatch clear.
if [ "$(id -u)" -ne "${PUID}" ]; then
    echo "Torrentarr: running as Docker user $(id -u):$(id -g); PUID/PGID are ignored because the container is already non-root." >&2
fi
exec /usr/bin/tini -- "$@"
