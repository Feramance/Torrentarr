import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).parents[2] / "scripts" / "check-openapi-drift.py"
spec = importlib.util.spec_from_file_location("openapi_drift", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def document(paths, components=None):
    return {"openapi": "3.0.0", "paths": paths, "components": components or {}}


class OpenApiDriftTests(unittest.TestCase):
    def test_accepts_matching_required_operation_contract(self):
        operation = {
            "parameters": [{"in": "query", "name": "q", "required": True}],
            "responses": {"200": {"content": {"application/json": {}}}},
        }
        self.assertEqual(module.compare(document({"/web/items": {"get": operation}}), document({"/web/items": {"get": operation}}), set()), [])

    def test_reports_missing_method_parameter_status_and_media_type(self):
        upstream = document({
            "/web/items": {"get": {
                "parameters": [{"in": "query", "name": "q", "required": True}],
                "responses": {"200": {"content": {"application/json": {}}}, "404": {}},
            }},
            "/web/delete": {"delete": {"responses": {"204": {}}}},
        })
        torrentarr = document({
            "/web/items": {"get": {"parameters": [], "responses": {"200": {"content": {"text/plain": {}}}}}},
            "/web/delete": {"get": {"responses": {"200": {}}}},
        })
        result = "\n".join(module.compare(torrentarr, upstream, set()))
        self.assertIn("missing parameter", result)
        self.assertIn("missing response 404", result)
        self.assertIn("missing response media", result)
        self.assertIn("missing operation DELETE", result)

    def test_rejects_undocumented_extensions_and_stale_allowlist(self):
        result = "\n".join(module.compare(document({"/unexpected": {"get": {"responses": {"200": {}}}}}), document({}), {"/allowed"}))
        self.assertIn("undocumented Torrentarr extension /unexpected", result)
        self.assertIn("stale extension allowlist entry", result)

    def test_compares_request_and_response_schemas_through_references(self):
        operation = {
            "requestBody": {"$ref": "#/components/requestBodies/ItemRequest"},
            "responses": {"200": {"$ref": "#/components/responses/ItemResponse"}},
        }
        upstream = document(
            {"/web/items": {"post": operation}},
            {
                "requestBodies": {
                    "ItemRequest": {
                        "required": True,
                        "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemInput"}}},
                    }
                },
                "responses": {
                    "ItemResponse": {
                        "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemOutput"}}}
                    }
                },
                "schemas": {
                    "ItemInput": {
                        "type": "object",
                        "required": ["name"],
                        "properties": {"name": {"type": "string"}},
                    },
                    "ItemOutput": {"type": "object", "properties": {"id": {"type": "integer"}}},
                },
            },
        )
        torrentarr = document(
            {"/web/items": {"post": operation}},
            {
                "requestBodies": {
                    "ItemRequest": {
                        "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemInput"}}}
                    }
                },
                "responses": {
                    "ItemResponse": {
                        "content": {"application/json": {"schema": {"$ref": "#/components/schemas/ItemOutput"}}}
                    }
                },
                "schemas": {
                    "ItemInput": {"type": "object", "properties": {"name": {"type": "integer"}}},
                    "ItemOutput": {"type": "object", "properties": {"id": {"type": "string"}}},
                },
            },
        )

        result = "\n".join(module.compare(torrentarr, upstream, set()))

        self.assertIn("request body is not required on POST /web/items", result)
        self.assertIn("request body on POST /web/items schema differs for application/json", result)
        self.assertIn("response 200 on POST /web/items schema differs for application/json", result)

    def test_accepts_equivalent_schemas_with_different_reference_names(self):
        upstream = document(
            {
                "/web/items": {
                    "get": {
                        "responses": {
                            "200": {
                                "content": {
                                    "application/json": {
                                        "schema": {"$ref": "#/components/schemas/UpstreamItem"}
                                    }
                                }
                            }
                        }
                    }
                }
            },
            {"schemas": {"UpstreamItem": {"type": "object", "properties": {"id": {"type": "integer"}}}}},
        )
        torrentarr = document(
            {
                "/web/items": {
                    "get": {
                        "responses": {
                            "200": {
                                "content": {
                                    "application/json": {
                                        "schema": {"$ref": "#/components/schemas/TorrentarrItem"}
                                    }
                                }
                            }
                        }
                    }
                }
            },
            {"schemas": {"TorrentarrItem": {"type": "object", "properties": {"id": {"type": "integer"}}}}},
        )

        self.assertEqual(module.compare(torrentarr, upstream, set()), [])


if __name__ == "__main__":
    unittest.main()
