"""Portable tests of the release evidence gate itself (not Windows IME tests)."""
import copy
import importlib.util
import json
from datetime import datetime, timezone
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("windows_acceptance", ROOT / "scripts/windows-acceptance.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class AcceptanceTests(unittest.TestCase):
    def make_approved(self):
        data = module.template("0.1.0")
        data.update(tester="tester", reviewedAtUtc=datetime.now(timezone.utc).isoformat())
        data["environment"].update(osBuild="26100", displayDpi="96,144", inputMethods="Pinyin, Japanese, Korean, US-Intl")
        for case in data["cases"]:
            case["status"] = "PASS"
        return data

    def test_template_is_never_automatically_approved(self):
        data = module.template("0.1.0")
        self.assertGreater(len(data["cases"]), 15)
        self.assertTrue(all(case["status"] == "NOT_RUN" for case in data["cases"]))
        self.assertNotEqual(module.verify(data, "0.1.0"), [])

    def test_valid_complete_manual_report(self):
        self.assertEqual(module.verify(self.make_approved(), "0.1.0"), [])

    def test_missing_failed_duplicate_and_retired_cases_rejected(self):
        data = self.make_approved()
        data["cases"].pop()
        self.assertTrue(any("missing case" in v for v in module.verify(data, "0.1.0")))
        data = self.make_approved()
        data["cases"][0]["status"] = "SKIP"
        self.assertTrue(any("not passed" in v for v in module.verify(data, "0.1.0")))
        data = self.make_approved()
        data["cases"].append(copy.deepcopy(data["cases"][0]))
        self.assertTrue(any("duplicate case" in v for v in module.verify(data, "0.1.0")))
        data = self.make_approved()
        data["cases"].append({"id": "ime/runtime-backend-switch", "status": "PASS"})
        self.assertTrue(any("unknown/retired" in v for v in module.verify(data, "0.1.0")))

    def test_fingerprint_includes_build_scripts(self):
        import tempfile
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            (root / "scripts").mkdir()
            build = root / "scripts/build-windows.ps1"
            build.write_text("A", encoding="utf-8")
            before = module.source_fingerprint(root)
            build.write_text("B", encoding="utf-8")
            self.assertNotEqual(module.source_fingerprint(root), before)

    def test_fingerprint_ignores_generated_artifacts_and_approval_records(self):
        before = module.source_fingerprint()
        transient = ROOT / "reference/windows-acceptance/approved/temporary-test.json"
        transient.write_text('{"status": "not-approved"}', encoding="utf-8")
        try:
            self.assertEqual(module.source_fingerprint(), before)
        finally:
            transient.unlink()

    def test_version_source_os_and_reviewer_required(self):
        for change in (lambda d: d.update(releaseVersion="0.2.0"),
                       lambda d: d.update(sourceSha256="wrong"),
                       lambda d: d["environment"].update(os="Linux"),
                       lambda d: d.update(tester="")):
            data = self.make_approved()
            change(data)
            self.assertNotEqual(module.verify(data, "0.1.0"), [])

    def test_matrices_no_longer_request_textbox_proxy(self):
        matrix = json.loads((ROOT / "reference/ime-interactive-matrix.json").read_text())
        self.assertNotIn("runtime-backend-switch", [case["id"] for case in matrix["cases"]])
        rendered = json.dumps(matrix).lower()
        self.assertNotIn("textbox proxy", rendered)
        self.assertNotIn("bounded proxy", rendered)


if __name__ == "__main__":
    unittest.main()
