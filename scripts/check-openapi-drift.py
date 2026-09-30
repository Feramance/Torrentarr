#!/usr/bin/env python3
"""Compare Torrentarr's OpenAPI contract with a pinned qBitrr specification."""

from __future__ import annotations

import json
import sys
from pathlib import Path

HTTP_METHODS = {"get", "put", "post", "delete", "patch", "head", "options", "trace"}
TORRENTARR_EXTENSIONS = {
    "/api/arr/{category}/open/{kind}/{entryId}",
    "/api/lidarr/{category}/tracks",
    "/api/qbit/categories",
    "/web/arr/{category}/open/{kind}/{entryId}",
    "/web/lidarr/{category}/tracks",
    "/web/torrents/distribution",
}


def load(path: str) -> dict:
    with Path(path).open(encoding="utf-8") as stream:
        return json.load(stream)


def parameters(spec: dict, path_item: dict, operation: dict) -> dict[tuple[str, str], dict]:
    result: dict[tuple[str, str], dict] = {}
    for parameter in [*path_item.get("parameters", []), *operation.get("parameters", [])]:
        if "$ref" in parameter:
            prefix = "#/components/parameters/"
            if parameter["$ref"].startswith(prefix):
                parameter = spec.get("components", {}).get("parameters", {}).get(parameter["$ref"][len(prefix):], {})
        if parameter.get("in") and parameter.get("name"):
            result[(parameter["in"], parameter["name"])] = parameter
    return result


def response_media(response: dict) -> set[str]:
    return set(response.get("content", {}))


def compare(torrentarr: dict, qbitrr: dict, allowed_extensions: set[str] | None = None) -> list[str]:
    errors: list[str] = []
    allowed_extensions = TORRENTARR_EXTENSIONS if allowed_extensions is None else allowed_extensions
    ta_paths = torrentarr.get("paths", {})
    qb_paths = qbitrr.get("paths", {})
    errors.extend(f"missing path {path}" for path in sorted(set(qb_paths) - set(ta_paths)))
    errors.extend(
        f"undocumented Torrentarr extension {path}"
        for path in sorted(set(ta_paths) - set(qb_paths) - allowed_extensions)
    )
    errors.extend(
        f"stale extension allowlist entry {path}"
        for path in sorted(allowed_extensions - (set(ta_paths) - set(qb_paths)))
    )

    for path in sorted(set(qb_paths) & set(ta_paths)):
        qb_item = qb_paths[path]
        ta_item = ta_paths[path]
        for method in sorted(HTTP_METHODS & set(qb_item)):
            if method not in ta_item:
                errors.append(f"missing operation {method.upper()} {path}")
                continue
            qb_operation = qb_item[method]
            ta_operation = ta_item[method]
            qb_parameters = parameters(qbitrr, qb_item, qb_operation)
            ta_parameters = parameters(torrentarr, ta_item, ta_operation)
            for key, required_parameter in qb_parameters.items():
                if key not in ta_parameters:
                    errors.append(f"missing parameter {key[0]}:{key[1]} on {method.upper()} {path}")
                elif required_parameter.get("required", False) and not ta_parameters[key].get("required", False):
                    errors.append(f"parameter {key[0]}:{key[1]} is not required on {method.upper()} {path}")

            qb_responses = qb_operation.get("responses", {})
            ta_responses = ta_operation.get("responses", {})
            for status, qb_response in qb_responses.items():
                if status not in ta_responses:
                    errors.append(f"missing response {status} on {method.upper()} {path}")
                    continue
                missing_media = response_media(qb_response) - response_media(ta_responses[status])
                for media in sorted(missing_media):
                    errors.append(f"missing response media {status}:{media} on {method.upper()} {path}")
    return errors


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        print(f"usage: {argv[0]} TORRENTARR_SPEC QBITRR_SPEC", file=sys.stderr)
        return 2
    torrentarr = load(argv[1])
    qbitrr = load(argv[2])
    errors = compare(torrentarr, qbitrr)
    if errors:
        print(f"OpenAPI parity failed with {len(errors)} difference(s):")
        for error in errors:
            print(f"  - {error}")
        return 1
    operations = sum(len(HTTP_METHODS & set(item)) for item in qbitrr.get("paths", {}).values())
    print(f"OK: Torrentarr covers {len(qbitrr.get('paths', {}))} qBitrr paths and {operations} operations.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
