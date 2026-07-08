#!/usr/bin/env python3
"""Merge qBitrr OpenAPI pin into docs/assets/openapi.json with Torrentarr extensions."""
from __future__ import annotations

import json
import sys
import urllib.request
from copy import deepcopy

QBITRR_REF = "master"
OUT_PATH = "docs/assets/openapi.json"

EXTENSION_PATHS = {
    "/api/qbit/categories": {
        "get": {
            "summary": "qBittorrent managed categories (/api)",
            "tags": ["WebUI"],
            "security": [{"bearerAuth": []}],
            "responses": {
                "200": {
                    "description": "OK",
                    "content": {
                        "application/json": {
                            "schema": {"type": "object", "additionalProperties": True}
                        }
                    },
                },
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
    "/web/torrents/distribution": {
        "get": {
            "summary": "Torrent distribution by category (/web)",
            "tags": ["WebUI"],
            "security": [{"bearerAuth": []}],
            "responses": {
                "200": {
                    "description": "OK",
                    "content": {
                        "application/json": {
                            "schema": {"type": "object", "additionalProperties": True}
                        }
                    },
                },
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
    "/web/lidarr/{category}/tracks": {
        "get": {
            "summary": "Lidarr tracks browse (/web)",
            "tags": ["WebUI"],
            "parameters": [
                {"name": "category", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "q", "in": "query", "schema": {"type": "string"}},
                {"name": "page", "in": "query", "schema": {"type": "integer"}},
                {"name": "page_size", "in": "query", "schema": {"type": "integer"}},
            ],
            "security": [{"bearerAuth": []}],
            "responses": {
                "200": {
                    "description": "OK",
                    "content": {
                        "application/json": {
                            "schema": {"type": "object", "additionalProperties": True}
                        }
                    },
                },
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
    "/api/lidarr/{category}/tracks": {
        "get": {
            "summary": "Lidarr tracks browse (/api)",
            "tags": ["WebUI"],
            "parameters": [
                {"name": "category", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "q", "in": "query", "schema": {"type": "string"}},
                {"name": "page", "in": "query", "schema": {"type": "integer"}},
                {"name": "page_size", "in": "query", "schema": {"type": "integer"}},
            ],
            "security": [{"bearerAuth": []}],
            "responses": {
                "200": {
                    "description": "OK",
                    "content": {
                        "application/json": {
                            "schema": {"type": "object", "additionalProperties": True}
                        }
                    },
                },
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
    "/web/arr/{category}/open/{kind}/{entryId}": {
        "get": {
            "summary": "Redirect to Arr UI for movie/series/artist (/web)",
            "tags": ["WebUI"],
            "parameters": [
                {"name": "category", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "kind", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "entryId", "in": "path", "required": True, "schema": {"type": "integer"}},
            ],
            "security": [{"bearerAuth": []}],
            "responses": {
                "302": {"description": "Redirect to Arr"},
                "404": {"description": "Not found"},
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
    "/api/arr/{category}/open/{kind}/{entryId}": {
        "get": {
            "summary": "Redirect to Arr UI for movie/series/artist (/api)",
            "tags": ["WebUI"],
            "parameters": [
                {"name": "category", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "kind", "in": "path", "required": True, "schema": {"type": "string"}},
                {"name": "entryId", "in": "path", "required": True, "schema": {"type": "integer"}},
            ],
            "security": [{"bearerAuth": []}],
            "responses": {
                "302": {"description": "Redirect to Arr"},
                "404": {"description": "Not found"},
                "401": {"$ref": "#/components/responses/Unauthorized"},
            },
        }
    },
}


def main() -> int:
    url = f"https://raw.githubusercontent.com/Feramance/qBitrr/{QBITRR_REF}/qBitrr/openapi.json"
    with urllib.request.urlopen(url) as resp:
        spec = json.load(resp)

    spec = deepcopy(spec)
    spec["info"] = {
        "title": "Torrentarr API",
        "version": "v1",
        "description": (
            "API for qBittorrent + Arr automation (Torrentarr — C# port of qBitrr). "
            f"Aligned with qBitrr {QBITRR_REF}. When WebUI.AuthDisabled is false, most routes "
            "require a Bearer token (WebUI.Token) or a valid browser session after login. "
            "Interactive docs: GET /web/docs or GET /api/docs."
        ),
    }
    spec["paths"].update(EXTENSION_PATHS)

    out = sys.argv[1] if len(sys.argv) > 1 else OUT_PATH
    with open(out, "w", encoding="utf-8") as f:
        json.dump(spec, f, indent=2)
        f.write("\n")

    print(f"Wrote {len(spec['paths'])} paths to {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
