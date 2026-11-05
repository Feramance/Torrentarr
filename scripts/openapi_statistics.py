"""Torrentarr console statistics API extensions."""
from copy import deepcopy

EXTENSION_PATHS = {"/web/statistics/transfers", "/web/statistics/catalog", "/web/statistics/arr-activity"}


def apply(spec):
    number = {"type": "number", "format": "double", "nullable": True}
    bucket = {"type": "object", "properties": {
        "timestamp": {"type": "string", "format": "date-time"},
        "downloadRate": number, "uploadRate": number,
        "samples": {"type": "integer"}, "expectedSamples": {"type": "integer"}}}
    series = {"type": "object", "properties": {"instance": {"type": "string"},
        "points": {"type": "array", "items": bucket}}}
    history = {"type": "object", "properties": {
        "range": {"type": "string", "enum": ["1h", "24h", "7d"]},
        "bucketMinutes": {"type": "integer"}, "generatedAt": {"type": "string", "format": "date-time"},
        "series": {"type": "array", "items": series}}}
    catalog = {"type": "object", "properties": {"unit": {"type": "string"},
        **{name: {"type": "integer"} for name in ("total", "available", "monitored", "missing")}}}
    for path, summary, parameters, schema in [
        ("/web/statistics/transfers", "Persistent whole-client transfer history", [
            {"name": "instance", "in": "query", "schema": {"type": "string"}, "description": "Enabled client name; omitted or all selects all enabled clients."},
            {"name": "range", "in": "query", "schema": {"type": "string", "enum": ["1h", "24h", "7d"], "default": "24h"}}], history),
        ("/web/statistics/catalog", "Complete catalog availability rollup", [
            {"name": "kind", "in": "query", "required": True, "schema": {"type": "string", "enum": ["radarr", "sonarr", "lidarr", "readarr"]}},
            {"name": "instance", "in": "query", "schema": {"type": "string"}, "description": "Arr section name; omitted or aggregate selects all instances of this kind."}], catalog),
    ]:
        spec["paths"][path] = {"get": {"tags": ["Torrentarr Statistics"], "summary": summary,
            "description": "Follows configured WebUI authentication. Transfer rates are bytes/second, collected once per minute and retained seven days. Completed buckets use 1, 5, or 30 minutes. Null rates indicate no observations; samples below expectedSamples indicate partial coverage. Catalog availability is monitored media with files; missing is monitored minus available. Catalog counts ignore page and text filters.",
            "parameters": parameters,
            "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": deepcopy(schema)}}},
                "400": {"description": "Invalid range, kind, or instance"}, "401": {"description": "Authentication required"}}}}
    point = {"type": "object", "properties": {
        "timestamp": {"type": "string", "format": "date-time"},
        "queueDepth": {"type": "number", "nullable": True},
        **{name: {"type": "integer", "nullable": True} for name in ("searchesCompleted", "importsConfirmed")},
        **{name: {"type": "integer"} for name in ("queueSamples", "completionSamples", "expectedSamples")}}}
    spec["paths"]["/web/statistics/arr-activity"] = {"get": {
        "tags": ["Torrentarr Statistics"], "summary": "Persistent Arr queue and confirmed activity history",
        "description": "Follows WebUI authentication. Worker-level series retain seven days; no backfill. Queue depth averages fresh heartbeats. Completions count only tracked Torrentarr submissions: searches require successful command completion; imports require positive correlated download-import history after submission. Counts are timestamped when confirmed, not when requested. Missing/partial coverage is explicit. Buckets are 1, 5, or 30 minutes. Disabled/deleted workers can remain in retained history.",
        "parameters": [
            {"name": "range", "in": "query", "schema": {"type": "string", "enum": ["1h", "24h", "7d"], "default": "24h"}},
            {"name": "worker", "in": "query", "schema": {"type": "string"}, "description": "Optional configured Arr section name."}],
        "responses": {"200": {"description": "OK", "content": {"application/json": {"schema": {
            "type": "object", "properties": {"range": {"type": "string"}, "bucketMinutes": {"type": "integer"},
                "generatedAt": {"type": "string", "format": "date-time"}, "series": {"type": "array", "items": {
                    "type": "object", "properties": {"instance": {"type": "string"}, "type": {"type": "string"},
                    "points": {"type": "array", "items": point}}}}}}}}},
            "400": {"description": "Invalid range or worker"}, "401": {"description": "Authentication required"}}}}
    overview = spec.get("paths", {}).get("/web/qbit/overview", {}).get("get")
    if overview:
        overview["description"] = "Includes optional summaries per available client: instance, torrentCount, totalSize, downloading, seeding, stopped, other. These monitored-category totals are computed before the display cap and deduplicated by client and torrent hash. Clients without an inventory response are omitted."
