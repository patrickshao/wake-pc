"""Standalone launcher tests: no live network or real user settings."""
import importlib.util
import os
import subprocess
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SPEC = importlib.util.spec_from_file_location("mac_install", ROOT / "mac/install.py")
installer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(installer)


class InstallTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "Mac launcher uses a POSIX shell")
    def test_standalone_command_and_reversible_install(self):
        with tempfile.TemporaryDirectory(prefix="wake pc ") as temporary:
            prefix = Path(temporary) / "local"
            config = Path(temporary) / "settings"
            config.mkdir()
            sentinel = config / "keep.txt"
            sentinel.write_text("preserve")
            installer.install(prefix)
            command = prefix / "bin/wake-pc"
            runtime = prefix / "share/wake-pc"
            self.assertNotIn(str(ROOT), command.read_text())
            self.assertEqual((runtime / "py_modules/wakepc.py").read_bytes(), (ROOT / "py_modules/wakepc.py").read_bytes())
            result = subprocess.run([str(command), "--help"], cwd="/", capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(command), "--config-dir", str(config), "wake"], cwd="/", stdin=subprocess.DEVNULL, capture_output=True, text=True, timeout=5)
            self.assertEqual(result.returncode, 1)
            self.assertIn("wired Ethernet MAC", result.stderr)
            self.assertNotIn("Sent 3", result.stdout)
            with self.assertRaisesRegex(ValueError, "Already installed"):
                installer.install(prefix)
            installer.install(prefix, uninstall=True)
            self.assertFalse(command.exists())
            self.assertFalse(runtime.exists())
            self.assertEqual(sentinel.read_text(), "preserve")

    def test_existing_command_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            prefix = Path(temporary)
            command = prefix / "bin/wake-pc"
            command.parent.mkdir()
            command.write_text("existing command")
            with self.assertRaisesRegex(ValueError, "Existing command differs"):
                installer.install(prefix)
            self.assertEqual(command.read_text(), "existing command")
            self.assertFalse((prefix / "share/wake-pc").exists())
