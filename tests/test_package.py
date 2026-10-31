"""Exercise release archive validation, including regressions in shipped source."""

import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
import zipfile


ROOT = Path(__file__).resolve().parents[1]
VALIDATOR = ROOT / "scripts/validate_package.py"
spec = importlib.util.spec_from_file_location("package_validator", VALIDATOR)
validator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(validator)


class PackageTests(unittest.TestCase):
    def test_checksum_sidecar_uses_exact_download_name(self):
        with tempfile.TemporaryDirectory() as directory:
            download = Path(directory) / "wake-pc-latest.zip"
            sidecar = download.with_suffix(".zip.sha256")
            download.write_bytes(b"abc")
            subprocess.run(
                [sys.executable, str(ROOT / "scripts/write_sha256.py"),
                 str(download), str(sidecar)], check=True,
            )
            self.assertEqual(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
                "  wake-pc-latest.zip\n",
                sidecar.read_text(),
            )

    def validate(self, *, omitted=None, flags=None, extra=None, sources=None, main_py=None, static=False):
        version = json.loads((ROOT / "package.json").read_text())["version"]
        with tempfile.TemporaryDirectory() as directory:
            archive = Path(directory) / f"wake-pc-v{version}.zip"
            with zipfile.ZipFile(archive, "w") as package:
                for name in validator.EXPECTED_FILES | {"wake-pc/package.json"}:
                    if name == omitted:
                        continue
                    relative = name.removeprefix("wake-pc/")
                    source = ROOT / relative.removeprefix("source/")
                    contents = source.read_bytes() if source.is_file() else b"fixture"
                    if name == "wake-pc/plugin.json" and flags is not None:
                        manifest = json.loads(contents)
                        manifest["flags"] = flags
                        contents = json.dumps(manifest).encode()
                    if name == "wake-pc/dist/index.js.map":
                        contents = json.dumps({"sources": sources or [
                            "decky://decky/plugin/Wake%20PC/node_modules/.pnpm/@decky+api@1.1.3/node_modules/@decky/api/dist/index.js",
                            "decky://decky/plugin/Wake%20PC/src/index.tsx",
                        ]}).encode()
                    if name == "wake-pc/main.py" and main_py is not None:
                        contents = main_py
                    package.writestr(name, contents)
                if extra:
                    package.writestr(extra, b"unexpected")
            return subprocess.run(
                [sys.executable, str(VALIDATOR), *(["--static"] if static else []), str(archive)],
                capture_output=True, text=True,
            )

    def test_plugin_validates_without_a_windows_installer(self):
        result = self.validate()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("Package validated", result.stdout)

    def test_static_validation_does_not_run_packaged_code(self):
        planted = b"raise SystemExit(7)\n"
        self.assertNotEqual(0, self.validate(main_py=planted).returncode)
        result = self.validate(main_py=planted, static=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("without running it", result.stdout)

    def test_static_validation_still_checks_the_package(self):
        result = self.validate(flags=["root"], static=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn("must not enable", result.stderr)

    def test_only_first_party_source_and_decky_api_may_be_bundled(self):
        for source in (
            "decky://decky/plugin/Wake%20PC/node_modules/.pnpm/glob@10.5.0/node_modules/glob/index.js",
            "decky://decky/plugin/Wake%20PC/scripts/build.js",
        ):
            with self.subTest(source=source):
                result = self.validate(sources=[source])
                self.assertNotEqual(0, result.returncode)
                self.assertIn("unexpected source", result.stderr)

    def test_incomplete_frontend_source_is_rejected(self):
        result = self.validate(omitted="wake-pc/source/src/runtime.ts")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("missing required files", result.stderr)

    def test_development_and_privilege_flags_are_rejected(self):
        for flag in ("debug", "root", "_root"):
            with self.subTest(flag=flag):
                result = self.validate(flags=[flag])
                self.assertNotEqual(0, result.returncode)
                self.assertIn("must not enable", result.stderr)

    def test_archive_cannot_escape_plugin_directory(self):
        result = self.validate(extra="wake-pc/../outside.txt")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("exactly one wake-pc/ root", result.stderr)
