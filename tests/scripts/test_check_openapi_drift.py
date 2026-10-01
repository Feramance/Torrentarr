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

        self.assertIn("request body requiredness differs on POST /web/items", result)
        self.assertIn("request body on POST /web/items schema differs for application/json", result)
        self.assertIn("response 200 on POST /web/items schema differs for application/json", result)

    def test_reports_request_body_requiredness_differences_in_both_directions(self):
        for upstream_required, torrentarr_required in ((True, False), (False, True)):
            with self.subTest(
                upstream_required=upstream_required,
                torrentarr_required=torrentarr_required,
            ):
                upstream_operation = {
                    "requestBody": {
                        "required": upstream_required,
                        "content": {"application/json": {}},
                    },
                    "responses": {"200": {}},
                }
                torrentarr_operation = {
                    "requestBody": {
                        "required": torrentarr_required,
                        "content": {"application/json": {}},
                    },
                    "responses": {"200": {}},
                }

                result = module.compare(
                    document({"/web/items": {"post": torrentarr_operation}}),
                    document({"/web/items": {"post": upstream_operation}}),
                    set(),
                )

                self.assertIn("request body requiredness differs on POST /web/items", result)

    def test_treats_missing_request_body_as_optional_for_requiredness(self):
        upstream_operation = {"responses": {"200": {}}}
        required_torrentarr_operation = {
            "requestBody": {"required": True, "content": {"application/json": {}}},
            "responses": {"200": {}},
        }
        optional_torrentarr_operation = {
            "requestBody": {"content": {"application/json": {}}},
            "responses": {"200": {}},
        }

        required_result = module.compare(
            document({"/web/items": {"post": required_torrentarr_operation}}),
            document({"/web/items": {"post": upstream_operation}}),
            set(),
        )
        optional_result = module.compare(
            document({"/web/items": {"post": optional_torrentarr_operation}}),
            document({"/web/items": {"post": upstream_operation}}),
            set(),
        )

        self.assertIn("request body requiredness differs on POST /web/items", required_result)
        self.assertEqual(optional_result, [])

    def test_preserves_missing_request_body_error(self):
        upstream_operation = {
            "requestBody": {"content": {"application/json": {}}},
            "responses": {"200": {}},
        }
        torrentarr_operation = {"responses": {"200": {}}}

        result = module.compare(
            document({"/web/items": {"post": torrentarr_operation}}),
            document({"/web/items": {"post": upstream_operation}}),
            set(),
        )

        self.assertIn("missing request body on POST /web/items", result)
        self.assertNotIn("request body requiredness differs on POST /web/items", result)

    def test_accepts_matching_request_body_requiredness(self):
        for required in (False, True):
            with self.subTest(required=required):
                operation = {
                    "requestBody": {
                        "required": required,
                        "content": {"application/json": {}},
                    },
                    "responses": {"200": {}},
                }

                self.assertEqual(
                    module.compare(
                        document({"/web/items": {"post": operation}}),
                        document({"/web/items": {"post": operation}}),
                        set(),
                    ),
                    [],
                )

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

    def test_reports_parameter_schema_differences(self):
        upstream_operation = {
            "parameters": [
                {"in": "query", "name": "page", "schema": {"type": "integer", "format": "int32"}}
            ],
            "responses": {"200": {}},
        }
        torrentarr_operation = {
            "parameters": [{"in": "query", "name": "page", "schema": {"type": "string"}}],
            "responses": {"200": {}},
        }

        result = module.compare(
            document({"/web/items": {"get": torrentarr_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )

        self.assertIn("parameter query:page schema differs on GET /web/items", result)

    def test_compares_effective_parameter_serialization(self):
        upstream_operation = {
            "parameters": [
                {
                    "in": "query",
                    "name": "tag",
                    "schema": {"type": "array", "items": {"type": "string"}},
                }
            ],
            "responses": {"200": {}},
        }
        matching_operation = {
            "parameters": [
                {
                    "in": "query",
                    "name": "tag",
                    "style": "form",
                    "explode": True,
                    "schema": {"type": "array", "items": {"type": "string"}},
                }
            ],
            "responses": {"200": {}},
        }
        mismatching_operation = {
            "parameters": [
                {
                    "in": "query",
                    "name": "tag",
                    "style": "form",
                    "explode": False,
                    "schema": {"type": "array", "items": {"type": "string"}},
                }
            ],
            "responses": {"200": {}},
        }

        matching_result = module.compare(
            document({"/web/items": {"get": matching_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )
        mismatching_result = module.compare(
            document({"/web/items": {"get": mismatching_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )

        self.assertEqual(matching_result, [])
        self.assertIn("parameter query:tag on GET /web/items serialization differs", mismatching_result)

    def test_compares_parameter_content_schemas(self):
        upstream_operation = {
            "parameters": [
                {
                    "in": "query",
                    "name": "filter",
                    "content": {"application/json": {"schema": {"type": "object"}}},
                }
            ],
            "responses": {"200": {}},
        }
        torrentarr_operation = {
            "parameters": [
                {
                    "in": "query",
                    "name": "filter",
                    "content": {"application/json": {"schema": {"type": "string"}}},
                }
            ],
            "responses": {"200": {}},
        }

        result = module.compare(
            document({"/web/items": {"get": torrentarr_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )

        self.assertIn(
            "parameter query:filter on GET /web/items schema differs for application/json",
            result,
        )

    def test_reports_parameter_requiredness_differences_in_both_directions(self):
        for upstream_required, torrentarr_required in ((True, False), (False, True)):
            upstream_operation = {
                "parameters": [
                    {"in": "query", "name": "q", "required": upstream_required, "schema": {"type": "string"}}
                ],
                "responses": {"200": {}},
            }
            torrentarr_operation = {
                "parameters": [
                    {"in": "query", "name": "q", "required": torrentarr_required, "schema": {"type": "string"}}
                ],
                "responses": {"200": {}},
            }

            result = module.compare(
                document({"/web/items": {"get": torrentarr_operation}}),
                document({"/web/items": {"get": upstream_operation}}),
                set(),
            )

            self.assertIn("parameter query:q requiredness differs on GET /web/items", result)

    def test_rejects_only_torrentarr_only_required_parameters(self):
        upstream_operation = {"responses": {"200": {}}}
        required_torrentarr_operation = {
            "parameters": [
                {"in": "query", "name": "required", "required": True, "schema": {"type": "string"}}
            ],
            "responses": {"200": {}},
        }
        optional_torrentarr_operation = {
            "parameters": [
                {"in": "query", "name": "optional", "required": False, "schema": {"type": "string"}}
            ],
            "responses": {"200": {}},
        }

        required_result = module.compare(
            document({"/web/items": {"get": required_torrentarr_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )
        optional_result = module.compare(
            document({"/web/items": {"get": optional_torrentarr_operation}}),
            document({"/web/items": {"get": upstream_operation}}),
            set(),
        )

        self.assertIn("unexpected required parameter query:required on GET /web/items", required_result)
        self.assertEqual(optional_result, [])

    def test_accepts_reordered_required_and_enum_schema_values(self):
        upstream_schema = {
            "type": "object",
            "required": ["id", "kind"],
            "properties": {
                "kind": {"type": "string", "enum": ["movie", "series"]},
                "coordinates": {"type": "array", "default": [1, 2]},
            },
        }
        torrentarr_schema = {
            "type": "object",
            "required": ["kind", "id"],
            "properties": {
                "kind": {"type": "string", "enum": ["series", "movie"]},
                "coordinates": {"type": "array", "default": [1, 2]},
            },
        }

        upstream = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": upstream_schema}}}}}
            }
        })
        torrentarr = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": torrentarr_schema}}}}}
            }
        })

        self.assertEqual(module.compare(torrentarr, upstream, set()), [])

    def test_preserves_order_for_literal_array_values(self):
        upstream_schema = {"type": "array", "default": [1, 2]}
        torrentarr_schema = {"type": "array", "default": [2, 1]}
        upstream = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": upstream_schema}}}}}
            }
        })
        torrentarr = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": torrentarr_schema}}}}}
            }
        })

        result = module.compare(torrentarr, upstream, set())

        self.assertIn("response 200 on GET /web/items schema differs for application/json", result)

    def test_preserves_required_array_order_inside_object_enum_literals(self):
        upstream_schema = {
            "type": "object",
            "enum": [{"required": ["id", "name"]}],
        }
        torrentarr_schema = {
            "type": "object",
            "enum": [{"required": ["name", "id"]}],
        }
        upstream = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": upstream_schema}}}}}
            }
        })
        torrentarr = document({
            "/web/items": {
                "get": {"responses": {"200": {"content": {"application/json": {"schema": torrentarr_schema}}}}}
            }
        })

        result = module.compare(torrentarr, upstream, set())

        self.assertIn("response 200 on GET /web/items schema differs for application/json", result)

    def test_compares_effective_operation_security(self):
        upstream = document({
            "/web/items": {"get": {"responses": {"200": {}}}}
        })
        upstream["security"] = [{"bearerAuth": []}]
        torrentarr = document({
            "/web/items": {"get": {"security": [], "responses": {"200": {}}}}
        })

        result = module.compare(torrentarr, upstream, set())

        self.assertIn("security differs on GET /web/items", result)

    def test_compares_referenced_security_scheme_definitions(self):
        operation = {
            "security": [{"bearerAuth": []}],
            "responses": {"200": {}},
        }
        upstream = document(
            {"/web/items": {"get": operation}},
            {
                "securitySchemes": {
                    "bearerAuth": {"type": "http", "scheme": "bearer"},
                    "unused": {"type": "apiKey", "in": "header", "name": "X-Unused"},
                }
            },
        )
        torrentarr = document(
            {"/web/items": {"get": operation}},
            {
                "securitySchemes": {
                    "bearerAuth": {"type": "apiKey", "in": "cookie", "name": "token"},
                    "unused": {"type": "http", "scheme": "basic"},
                }
            },
        )

        result = module.compare(torrentarr, upstream, set())

        self.assertIn("security scheme definitions differ on GET /web/items", result)

    def test_ignores_unreferenced_security_scheme_differences(self):
        operation = {"responses": {"200": {}}}
        upstream = document(
            {"/web/items": {"get": operation}},
            {"securitySchemes": {"unused": {"type": "http", "scheme": "basic"}}},
        )
        torrentarr = document(
            {"/web/items": {"get": operation}},
            {
                "securitySchemes": {
                    "unused": {"type": "apiKey", "in": "query", "name": "token"}
                }
            },
        )

        self.assertEqual(module.compare(torrentarr, upstream, set()), [])


if __name__ == "__main__":
    unittest.main()
