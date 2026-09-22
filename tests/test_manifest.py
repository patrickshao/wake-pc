import json
import unittest
from pathlib import Path


class ManifestTests(unittest.TestCase):
    def test_release_manifest_and_version(self) -> None:
        manifest = json.loads(Path("plugin.json").read_text(encoding="utf-8"))
        package = json.loads(Path("package.json").read_text(encoding="utf-8"))
        source = Path("src/index.tsx").read_text(encoding="utf-8")
        self.assertEqual([], manifest["flags"])
        self.assertEqual(manifest["api_version"], 1)
        self.assertIn(
            f'const PLUGIN_VERSION = "{package["version"]}";', source
        )

    def test_backend_helpers_use_deckys_python_module_path(self) -> None:
        self.assertTrue(Path("py_modules/wakepc.py").is_file())
        self.assertFalse(Path("wakepc.py").exists())

    def test_quick_access_ui_stays_focused_on_one_pc(self) -> None:
        source = Path("src/index.tsx").read_text(encoding="utf-8")
        for removed_ui in (
            'label="PC name"',
            "PowerIcon",
            "RefreshIcon",
            "SaveIcon",
            "CopyIcon",
        ):
            with self.subTest(removed_ui=removed_ui):
                self.assertNotIn(removed_ui, source)
        for concise_action in (
            "Wake PC",
            "Refresh status",
            "Sleep PC",
            "Confirm sleep",
            "Sleep PC",
            "Pairing code",
            "Pair for Sleep",
            "Advanced settings",
            "Save changes",
            "Re-pair this device",
            "Cancel re-pairing",
            "Decky update URL",
            "Copy update URL",
            'label="Decky plugin"',
            'label="Windows companion"',
            "Older companion — version unavailable",
            "Not reachable — check saved IP",
            "open Setup & status on Windows",
        ):
            with self.subTest(concise_action=concise_action):
                self.assertIn(concise_action, source)
        self.assertNotIn('label="Pairing key"', source)
        self.assertNotIn("Six digits from the Windows installer", source)
        self.assertNotIn("Sleep accepted\", body: \"Waiting", source)
        self.assertNotIn("sleep_secret: draft", source)
        section_order = [
            source.index('<PanelSection title="Gaming PC">'),
            source.index('<PanelSection title="Power">'),
            source.index('<PanelSection title="Settings">'),
            source.index('<PanelSection title="Sleep PC">'),
            source.index('<PanelSection title="Update">'),
        ]
        self.assertEqual(section_order, sorted(section_order))


if __name__ == "__main__":
    unittest.main()
