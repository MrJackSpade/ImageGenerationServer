import argparse
import importlib.util
import json
import pathlib
import sys
import tempfile
import unittest
from unittest import mock


ROOT = pathlib.Path(__file__).resolve().parents[2]


def load_tool(filename, module_name):
    spec = importlib.util.spec_from_file_location(module_name, ROOT / "tools" / filename)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


MIGRATE = load_tool("migrate-production-config.py", "migrate_production_config")
CAPTURE = load_tool("capture-tagmodel-parity.py", "capture_tagmodel_parity")


class DatabaseProviderPolicyTests(unittest.TestCase):
    def test_recognizes_sqlite_file_forms(self):
        self.assertEqual("Sqlite", MIGRATE.infer_database_provider("Data Source=../imagegen.db;Cache=Shared"))
        self.assertEqual("Sqlite", MIGRATE.infer_database_provider("Filename=/var/lib/imagegen.sqlite"))
        self.assertEqual("Sqlite", MIGRATE.infer_database_provider("Data Source=:memory:"))
        self.assertEqual("Sqlite", MIGRATE.infer_database_provider('Data Source="imagegen.db"'))

    def test_recognizes_sql_server_forms(self):
        self.assertEqual(
            "SqlServer",
            MIGRATE.infer_database_provider("Server=db.example;Initial Catalog=ImageGen;Integrated Security=true"))
        self.assertEqual(
            "SqlServer",
            MIGRATE.infer_database_provider("Data Source=tcp:db.example,1433;Initial Catalog=ImageGen"))

    def test_rejects_unknown_or_ambiguous_forms(self):
        for connection_string in ("", "not a connection string", "Data Source=database-host"):
            with self.subTest(connection_string=connection_string):
                with self.assertRaises(ValueError):
                    MIGRATE.infer_database_provider(connection_string)

    def test_ambiguous_provider_stops_without_writing_configuration(self):
        with tempfile.TemporaryDirectory() as directory:
            path = pathlib.Path(directory) / "appsettings.Production.json"
            original = {"ConnectionStrings": {"ImageGen": "Data Source=database-host"}}
            path.write_text(json.dumps(original), encoding="utf-8")

            with mock.patch.object(sys, "argv", ["migrate-production-config.py", str(path)]):
                with self.assertRaises(SystemExit):
                    MIGRATE.main()

            self.assertEqual(original, json.loads(path.read_text(encoding="utf-8")))
            self.assertFalse(path.with_suffix(path.suffix + ".new").exists())
            self.assertFalse(path.with_suffix(path.suffix + ".bak").exists())


class CaptureTimeoutPolicyTests(unittest.TestCase):
    def test_timeout_must_be_positive_operator_input(self):
        self.assertEqual(12.5, CAPTURE.positive_seconds("12.5"))
        with self.assertRaises(argparse.ArgumentTypeError):
            CAPTURE.positive_seconds("0")
        capture = (ROOT / "tools" / "capture-tagmodel-parity.py").read_text(encoding="utf-8")
        self.assertIn('"--timeout-seconds", type=positive_seconds, required=True', capture)


class ScriptContractTests(unittest.TestCase):
    def source(self, relative_path):
        return (ROOT / relative_path).read_text(encoding="utf-8")

    def test_destructive_model_sweep_is_complete_and_refuses_links(self):
        migrate = self.source("tools/migrate-models-to-comfy-layout.ps1")
        self.assertIn("$comfyContents = @(Get-ChildItem -LiteralPath $comfyModels -Force -Recurse -ErrorAction Stop)", migrate)
        self.assertIn("nested junction(s) or symbolic link(s)", migrate)
        self.assertNotIn("$weights = @(Get-ChildItem $comfyModels", migrate)

    def test_integrity_gate_distinguishes_bad_files_from_failed_enumeration(self):
        integrity = self.source("tools/check-model-integrity.ps1")
        ready = self.source("tools/ui-smoke-ready.ps1")
        self.assertIn("Get-ChildItem -LiteralPath $Root -Force -Recurse -File -ErrorAction Stop", integrity)
        self.assertIn("exit 2", integrity)
        self.assertIn("$integrityExit = $LASTEXITCODE", ready)
        self.assertIn("$integrityExit -notin 0, 1", ready)

    def test_local_release_requires_payload_markers_and_checks_retag(self):
        release = self.source("tools/local-release.ps1")
        self.assertIn("function Assert-ExistingReleaseRoot", release)
        self.assertIn("imagegen\\bin\\ImageGen.Web.dll", release)
        self.assertIn("Assert-ExistingReleaseRoot $Root", release)
        self.assertIn("if ($LASTEXITCODE -ne 0) { throw \"could not move local tag", release)
        self.assertNotIn("gh release delete $Tag", release.split("$deleteOutput =", 1)[0])

    def test_workflow_rerun_clobbers_assets_and_keeps_bare_rid_artifacts(self):
        workflow = self.source(".github/workflows/release.yml")
        self.assertIn('gh release view "$version"', workflow)
        self.assertIn('gh release upload "$version" artifacts/*', workflow)
        self.assertIn("--clobber", workflow)
        self.assertIn("name: ${{ matrix.rid }}", workflow)


if __name__ == "__main__":
    unittest.main()
