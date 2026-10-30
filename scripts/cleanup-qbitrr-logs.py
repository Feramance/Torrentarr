#!/usr/bin/env python3
"""Remove verified, inactive qBitrr logs; preview by default."""
import argparse
import re
import shutil
import subprocess
from pathlib import Path

LEGACY_NAMES = {
    "All", "Main", "Manager", "WebUI", "Autobrr", "Failed", "Recheck",
    "TorrentPolicyManager", "Lidarr", "Radarr-1080", "Radarr-4K", "Radarr-Anime",
    "Sonarr-1080", "Sonarr-4K", "Sonarr-Anime",
}
ANSI = re.compile(r"\x1b\[[0-9;]*m")


def verified(path):
    if path.is_symlink() or not path.is_file():
        return False
    if path.name not in {name + suffix for name in LEGACY_NAMES for suffix in (".log", ".log.old")}:
        return False
    found = False
    with path.open(errors="replace") as source:
        for line in source:
            plain = ANSI.sub("", line)
            if re.search(r"(?:Torrentarr|\[\s*(?:INF|WRN|ERR|DBG|VRB|FTL)\s*\])", plain):
                return False
            found |= bool(re.search(r"^\[[^\]]+\]\s*(?:TRACE|DEBUG|INFO|NOTICE|WARNING|ERROR|CRITICAL|SUCCESS|FATAL)\s*:\s*qBitrr(?:\.[^:\s]+)?\s*:", plain))
    return found


def inactive(path):
    result = subprocess.run(["lsof", "-t", "--", str(path)], capture_output=True, text=True)
    if result.returncode not in (0, 1) or result.stderr.strip():
        raise RuntimeError(f"Cannot verify open handles for {path}: {result.stderr.strip()}")
    return result.returncode == 1 and not result.stdout.strip()


def cleanup(root, apply=False):
    if not shutil.which("lsof"):
        raise RuntimeError("lsof is required to verify that no process holds a log open")
    removed = []
    for path in sorted(root.iterdir()):
        if not verified(path) or not inactive(path):
            continue
        before = path.stat()
        if apply:
            if not verified(path) or not inactive(path) or path.stat() != before:
                raise RuntimeError(f"Log changed during verification: {path}")
            path.unlink()
        removed.append(path.name)
    return removed


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path)
    parser.add_argument("--apply", action="store_true")
    args = parser.parse_args()
    for name in cleanup(args.logs, args.apply):
        print(("Removed " if args.apply else "Would remove ") + name)


if __name__ == "__main__":
    main()
