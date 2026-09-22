import asyncio
import logging
import sys
import tempfile
import types
import unittest
from unittest.mock import patch

from wakepc import PairingCredential


fake_decky = types.ModuleType("decky")
fake_decky.DECKY_PLUGIN_SETTINGS_DIR = ""
fake_decky.logger = logging.getLogger("wake-pc-test")
fake_decky.logger.disabled = True
sys.modules.setdefault("decky", fake_decky)

from main import Plugin


class PluginLifecycleTests(unittest.TestCase):
    def test_settings_calls_initialize_store_before_main_hook(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()

            loaded = asyncio.run(plugin.get_config())
            self.assertEqual(loaded["pc_name"], "Gaming PC")
            self.assertFalse(loaded["sleep_paired"])
            self.assertNotIn("sleep_secret", loaded)
            self.assertNotIn("sleep_device_id", loaded)

            result = asyncio.run(
                plugin.save_config(
                    {
                        **loaded,
                        "mac_address": "00-11-22-33-44-55",
                        "host_ip": "192.0.2.197",
                    }
                )
            )
            self.assertTrue(result["ok"])
            self.assertEqual(result["config"]["mac_address"], "00:11:22:33:44:55")

    def test_public_methods_return_bounded_errors_for_incomplete_settings(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()

            wake_result = asyncio.run(plugin.send_wake())
            self.assertFalse(wake_result["ok"])
            self.assertIn("Invalid MAC address", wake_result["message"])

            disabled = asyncio.run(
                plugin.save_config(
                    {
                        "mac_address": "00:11:22:33:44:55",
                        "host_ip": "",
                        "status_enabled": False,
                    }
                )
            )
            self.assertTrue(disabled["ok"])
            status = asyncio.run(plugin.check_status())
            self.assertTrue(status["ok"])
            self.assertFalse(status["enabled"])

    def test_sleep_control_uses_saved_pairing_key_without_exposing_it(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()
            secret = "cd" * 32
            saved = asyncio.run(
                plugin.save_config(
                    {
                        "mac_address": "00:11:22:33:44:55",
                        "host_ip": "192.0.2.197",
                        "sleep_enabled": True,
                        "sleep_port": 47653,
                        "sleep_secret": secret,
                    }
                )
            )
            self.assertTrue(saved["ok"])
            self.assertTrue(saved["config"]["sleep_paired"])
            self.assertNotIn("sleep_secret", saved["config"])

            with patch("main.send_sleep_request", return_value="Sleep accepted") as request:
                result = asyncio.run(plugin.send_sleep())
            self.assertTrue(result["ok"])
            request.assert_called_once_with(
                "192.0.2.197", 47653, "primary", secret
            )

    def test_sleep_control_is_optional(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()
            result = asyncio.run(plugin.send_sleep())
            self.assertFalse(result["ok"])
            self.assertIn("not configured", result["message"])
            bridge = asyncio.run(plugin.check_sleep_bridge())
            self.assertTrue(bridge["ok"])
            self.assertFalse(bridge["enabled"])

    def test_paired_bridge_reports_its_authenticated_version(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()
            secret = "bc" * 32
            plugin._get_store().save(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "192.0.2.197",
                    "sleep_enabled": False,
                    "sleep_device_id": "primary",
                    "sleep_secret": secret,
                }
            )
            with patch(
                "main.request_bridge_version", return_value="1.6.5"
            ) as request:
                bridge = asyncio.run(plugin.check_sleep_bridge())

            self.assertTrue(bridge["ok"])
            self.assertTrue(bridge["enabled"])
            self.assertTrue(bridge["online"])
            self.assertEqual(bridge["version"], "1.6.5")
            request.assert_called_once_with(
                "192.0.2.197", 47653, "primary", secret
            )

    def test_short_code_pairing_saves_secret_and_enables_sleep(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()
            config = asyncio.run(plugin.get_config())
            config.update(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "192.0.2.197",
                    "sleep_port": 47653,
                }
            )
            secret = "ef" * 32
            credential = PairingCredential(
                device_id="0123456789abcdef0123456789abcdef",
                secret_hex=secret,
            )
            with patch(
                "main.request_pairing_key", return_value=credential
            ) as request:
                result = asyncio.run(plugin.pair_sleep_bridge(config, "012345"))

            self.assertTrue(result["ok"])
            self.assertTrue(result["config"]["sleep_enabled"])
            self.assertTrue(result["config"]["sleep_paired"])
            self.assertNotIn("sleep_secret", result["config"])
            self.assertNotIn("sleep_device_id", result["config"])
            request.assert_called_once_with(
                "192.0.2.197", 47653, "012345", ""
            )

    def test_pair_again_replaces_current_device_credential(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            fake_decky.DECKY_PLUGIN_SETTINGS_DIR = directory
            plugin = Plugin()
            old_secret = "ab" * 32
            plugin._get_store().save(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "192.0.2.197",
                    "sleep_enabled": True,
                    "sleep_device_id": "primary",
                    "sleep_secret": old_secret,
                }
            )
            visible = asyncio.run(plugin.get_config())
            replacement = PairingCredential(
                device_id="primary",
                secret_hex="cd" * 32,
            )
            with patch(
                "main.request_pairing_key", return_value=replacement
            ) as request:
                result = asyncio.run(
                    plugin.pair_sleep_bridge(visible, "654321")
                )
            self.assertTrue(result["ok"])
            request.assert_called_once_with(
                "192.0.2.197", 47653, "654321", "primary"
            )
            stored = plugin._get_store().load()
            self.assertEqual(stored["sleep_device_id"], "primary")
            self.assertEqual(stored["sleep_secret"], "cd" * 32)


if __name__ == "__main__":
    unittest.main()
