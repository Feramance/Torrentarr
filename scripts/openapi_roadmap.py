"""Torrentarr search activity, filter, destination and issue alert APIs."""

ROUTES = (
    ("get", "/history", "Read worker action history",
     "Returns entries, workers (including retained former workers), types and nextCursor. Filter by exact worker instance and action type. Newest first; pass nextCursor as cursor for older entries. Retention defaults to 30 days via Settings.HistoryRetentionDays. Entries distinguish worker actions from observed item changes and preserve submitted commands until completion evidence is available.",
     (("worker", "query", False), ("type", "query", False), ("cursor", "query", False), ("pageSize", "query", False)), {"400": "Invalid type, cursor or page size"}),
    ("get", "/processes/{instance}/search-activity", "Preview proactive search and read decision history",
     "The preview uses the latest usable catalog and current search observations. Null means no usable preview. History distinguishes submission, completion, grabs and confirmed no-grab windows.",
     (("instance", "path", True),), {"404": "Unknown integration"}),
    ("get", "/file-filters/preview", "Preview configured file filters",
     "Returns availability, the matched reason and named list freshness for a sample path.",
     (("instance", "query", True), ("path", "query", False)), {"400": "Invalid path or evaluation timed out", "404": "Unknown integration"}),
    ("get", "/config/failed-torrent-destinations", "List existing failed torrent destination categories",
     "Returns one result per enabled qBittorrent client with its existing categories, Arr ownership and unavailable reasons.",
     (("category", "query", False),), {}),
    ("post", "/issues/alerts/test", "Send a test issue webhook",
     "Sends one test event to the saved destination. The test creates no issue transition.",
     (), {"400": "No destination configured", "502": "Delivery was rejected"}),
)
EXTENSION_PATHS = {prefix + path for prefix in ("/web", "/api") for _, path, *_ in ROUTES}


def apply(spec):
    for prefix in ("/web", "/api"):
        for method, path, summary, description, parameters, failures in ROUTES:
            spec["paths"][prefix + path] = {method: {
                "tags": ["Torrentarr automation"],
                "summary": summary,
                "description": description,
                "parameters": [
                    {"name": name, "in": location, "required": required, "schema": ({"type": "integer", "minimum": 1, **({"maximum": 100, "default": 50} if name == "pageSize" else {})} if name in ("cursor", "pageSize") else {"type": "string"})}
                    for name, location, required in parameters
                ],
                "responses": {
                    "200": {"description": "OK"},
                    "401": {"description": "Authentication required"},
                    **{status: {"description": failure} for status, failure in failures.items()},
                },
            }}
