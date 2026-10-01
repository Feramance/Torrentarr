#!/usr/bin/env python3
"""Compare Torrentarr's OpenAPI contract with a pinned qBitrr specification."""

from __future__ import annotations

import json
import sys
from copy import deepcopy
from pathlib import Path
from typing import Any

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


def resolve_local_refs(spec: dict, value: Any, stack: tuple[str, ...] = ()) -> Any:
    """Resolve local OpenAPI references so payload schemas can be compared by value."""
    if isinstance(value, list):
        return [resolve_local_refs(spec, item, stack) for item in value]
    if not isinstance(value, dict):
        return value

    reference = value.get("$ref")
    if isinstance(reference, str) and reference.startswith("#/"):
        if reference in stack:
            return {"$recursiveRef": len(stack) - stack.index(reference)}
        target: Any = spec
        try:
            for segment in reference[2:].split("/"):
                segment = segment.replace("~1", "/").replace("~0", "~")
                target = target[segment]
        except (KeyError, TypeError):
            return deepcopy(value)
        resolved = resolve_local_refs(spec, target, (*stack, reference))
        siblings = {key: item for key, item in value.items() if key != "$ref"}
        if siblings and isinstance(resolved, dict):
            return {**resolved, **resolve_local_refs(spec, siblings, stack)}
        return resolved

    return {key: resolve_local_refs(spec, item, stack) for key, item in value.items()}


def content(spec: dict, container: dict) -> dict:
    resolved = resolve_local_refs(spec, container)
    return resolved.get("content", {}) if isinstance(resolved, dict) else {}


def normalize_schema(value: Any) -> Any:
    """Normalize unordered schema keywords without rewriting literal values."""
    if not isinstance(value, dict):
        return value

    normalized = deepcopy(value)
    if isinstance(normalized.get("required"), list):
        normalized["required"] = sorted(normalized["required"])
    if isinstance(normalized.get("enum"), list):
        normalized["enum"] = sorted(
            normalized["enum"],
            key=lambda item: json.dumps(item, sort_keys=True, separators=(",", ":")),
        )

    for key in ("items", "additionalProperties", "not", "if", "then", "else", "contains"):
        if isinstance(normalized.get(key), dict):
            normalized[key] = normalize_schema(normalized[key])
    for key in ("allOf", "anyOf", "oneOf", "prefixItems"):
        if isinstance(normalized.get(key), list):
            normalized[key] = [normalize_schema(item) for item in normalized[key]]
    for key in ("properties", "patternProperties", "dependentSchemas"):
        if isinstance(normalized.get(key), dict):
            normalized[key] = {
                name: normalize_schema(schema) for name, schema in normalized[key].items()
            }

    return normalized


def normalized_security(spec: dict, operation: dict) -> list[dict]:
    """Return effective security requirements with order-insensitive alternatives and scopes."""
    security = operation["security"] if "security" in operation else spec.get("security", [])
    normalized = [
        {name: sorted(scopes) for name, scopes in requirement.items()}
        for requirement in security
    ]
    return sorted(normalized, key=lambda item: json.dumps(item, sort_keys=True, separators=(",", ":")))


def resolved_schema(spec: dict, container: dict) -> Any:
    return normalize_schema(resolve_local_refs(spec, container.get("schema")))


def compare_content_schemas(
    errors: list[str],
    torrentarr_spec: dict,
    torrentarr_container: dict,
    qbitrr_spec: dict,
    qbitrr_container: dict,
    context: str,
    missing_media_message: str,
) -> None:
    qb_content = content(qbitrr_spec, qbitrr_container)
    ta_content = content(torrentarr_spec, torrentarr_container)
    for media in sorted(set(qb_content) - set(ta_content)):
        errors.append(missing_media_message.format(media=media))
    for media in sorted(set(qb_content) & set(ta_content)):
        qb_schema = resolved_schema(qbitrr_spec, qb_content[media])
        ta_schema = resolved_schema(torrentarr_spec, ta_content[media])
        if qb_schema != ta_schema:
            errors.append(f"{context} schema differs for {media}")


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
            if normalized_security(qbitrr, qb_operation) != normalized_security(torrentarr, ta_operation):
                errors.append(f"security differs on {method.upper()} {path}")
            qb_parameters = parameters(qbitrr, qb_item, qb_operation)
            ta_parameters = parameters(torrentarr, ta_item, ta_operation)
            for key, required_parameter in qb_parameters.items():
                if key not in ta_parameters:
                    errors.append(f"missing parameter {key[0]}:{key[1]} on {method.upper()} {path}")
                    continue

                torrentarr_parameter = ta_parameters[key]
                if required_parameter.get("required", False) != torrentarr_parameter.get("required", False):
                    errors.append(f"parameter {key[0]}:{key[1]} requiredness differs on {method.upper()} {path}")
                qb_schema = resolved_schema(qbitrr, required_parameter)
                ta_schema = resolved_schema(torrentarr, torrentarr_parameter)
                if qb_schema != ta_schema:
                    errors.append(f"parameter {key[0]}:{key[1]} schema differs on {method.upper()} {path}")

            qb_request = qb_operation.get("requestBody")
            ta_request = ta_operation.get("requestBody")
            if qb_request is not None:
                if ta_request is None:
                    errors.append(f"missing request body on {method.upper()} {path}")
                else:
                    resolved_qb_request = resolve_local_refs(qbitrr, qb_request)
                    resolved_ta_request = resolve_local_refs(torrentarr, ta_request)
                    qb_required = isinstance(resolved_qb_request, dict) and resolved_qb_request.get("required", False)
                    ta_required = isinstance(resolved_ta_request, dict) and resolved_ta_request.get("required", False)
                    if qb_required and not ta_required:
                        errors.append(f"request body is not required on {method.upper()} {path}")
                    compare_content_schemas(
                        errors,
                        torrentarr,
                        ta_request,
                        qbitrr,
                        qb_request,
                        f"request body on {method.upper()} {path}",
                        f"missing request body media {{media}} on {method.upper()} {path}",
                    )

            qb_responses = qb_operation.get("responses", {})
            ta_responses = ta_operation.get("responses", {})
            for status, qb_response in qb_responses.items():
                if status not in ta_responses:
                    errors.append(f"missing response {status} on {method.upper()} {path}")
                    continue
                compare_content_schemas(
                    errors,
                    torrentarr,
                    ta_responses[status],
                    qbitrr,
                    qb_response,
                    f"response {status} on {method.upper()} {path}",
                    f"missing response media {status}:{{media}} on {method.upper()} {path}",
                )
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
