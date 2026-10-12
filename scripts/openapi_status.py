"""Torrentarr status contract: every client is named; none is selected implicitly."""

STATUS_SCHEMA = {
    "type": "object",
    "required": [
        "qbitInstances",
        "arrs",
        "ready"
    ],
    "properties": {
        "qbitInstances": {
            "type": "object",
            "additionalProperties": {
                "type": "object",
                "required": [
                    "alive",
                    "host",
                    "port",
                    "version"
                ],
                "properties": {
                    "alive": {
                        "type": "boolean"
                    },
                    "host": {
                        "type": "string"
                    },
                    "port": {
                        "type": "integer"
                    },
                    "version": {
                        "type": "string",
                        "nullable": True
                    }
                }
            }
        },
        "arrs": {
            "type": "array",
            "items": {
                "type": "object",
                "additionalProperties": True
            }
        },
        "ready": {
            "type": "boolean"
        }
    }
}
