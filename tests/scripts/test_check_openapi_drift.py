import importlib.util
import pathlib
import unittest

SCRIPT = pathlib.Path(__file__).parents[2] / "scripts" / "check-openapi-drift.py"
spec = importlib.util.spec_from_file_location("openapi_drift", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


def document(paths):
    return {"openapi": "3.0.0", "paths": paths}


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


if __name__ == "__main__":
    unittest.main()
