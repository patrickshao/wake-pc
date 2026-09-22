"""Focused Mac workflow tests; network protocol primitives have their own suite."""
import contextlib
import importlib.util
import io
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "mac_wake", Path(__file__).resolve().parents[1] / "mac" / "wake_pc.py"
)
mac = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(mac)


class MacWakeTests(unittest.TestCase):
    def setUp(self):
        self.config = mac.validate_config({
            "mac_address": "00:11:22:33:44:55", "host_ip": "192.0.2.10",
            "broadcast_address": "192.0.2.255",
        })
        self.output = io.StringIO()
        self.stdout = contextlib.redirect_stdout(self.output)
        self.stdout.__enter__()
        self.addCleanup(self.stdout.__exit__, None, None, None)

    def test_wake_sends_before_bridge_check_and_uses_shared_sender(self):
        events = []
        with patch.object(mac, "send_magic_packets", side_effect=lambda *a: events.append(a) or 3), \
             patch.object(mac, "readiness", side_effect=lambda *a: events.append("checked") or "Bridge responded"):
            self.assertEqual(mac.wake(self.config, 10), 0)
        self.assertEqual(events, [("00:11:22:33:44:55", "192.0.2.255", 9), "checked"])

    def test_send_only_does_not_claim_wake_or_probe(self):
        with patch.object(mac, "send_magic_packets", return_value=3), patch.object(mac, "readiness") as probe:
            self.assertEqual(mac.wake(self.config, 0), 0)
        probe.assert_not_called()
        self.assertIn("does not confirm the PC woke", self.output.getvalue())

    def test_timeout_reports_unconfirmed_exit_code(self):
        with patch.object(mac, "send_magic_packets", return_value=3), \
             patch.object(mac, "readiness", return_value=None), \
             patch.object(mac.time, "monotonic", side_effect=[0, 0, 0, 2, 2]):
            self.assertEqual(mac.wake(self.config, 1), 2)
        self.assertIn("response not confirmed", self.output.getvalue())

    def test_pairing_adds_mac_credential_without_replacing_other_clients(self):
        with tempfile.TemporaryDirectory() as directory:
            store = mac.ConfigStore(directory)
            store.save(self.config)
            credential = mac.request_pairing_key.__globals__["PairingCredential"]("a" * 32, "b" * 64)
            with patch.object(mac.getpass, "getpass", return_value="123456"), \
                 patch.object(mac, "request_pairing_key", return_value=credential) as pair:
                self.assertEqual(mac.main(["--config-dir", directory, "pair"]), 0)
            pair.assert_called_once_with("192.0.2.10", 47653, "123456", "")
            self.assertEqual(store.load()["sleep_device_id"], "a" * 32)
            if os.name != "nt":
                self.assertEqual(store.path.stat().st_mode & 0o777, 0o600)
            self.assertNotIn("b" * 64, self.output.getvalue())

    def test_authenticated_bridge_status_is_preferred(self):
        self.config.update(sleep_device_id="a" * 32, sleep_secret="b" * 64)
        with patch.object(mac, "request_bridge_version", return_value="1.7.0"), \
             patch.object(mac, "tcp_reachable") as tcp:
            self.assertIn("authenticated", mac.readiness(self.config))
        tcp.assert_not_called()

    def test_bridge_down_falls_back_to_steam_status(self):
        self.config.update(sleep_device_id="a" * 32, sleep_secret="b" * 64)
        with patch.object(mac, "request_bridge_version", side_effect=mac.SleepBridgeConnectionError("offline")), \
             patch.object(mac, "tcp_reachable", return_value=True) as tcp:
            self.assertIn("27036", mac.readiness(self.config))
        tcp.assert_called_once_with("192.0.2.10", 27036, 1.0)

    def test_rejected_pairing_is_not_reported_as_sleeping(self):
        self.config.update(sleep_device_id="a" * 32, sleep_secret="b" * 64)
        with patch.object(mac, "request_bridge_version", side_effect=mac.SleepBridgeError("rejected key")):
            with self.assertRaisesRegex(mac.SleepBridgeError, "rejected key"):
                mac.readiness(self.config)

    def test_missing_setup_never_prompts_or_sends(self):
        with tempfile.TemporaryDirectory() as directory:
            for command in ("wake", "status", "launch"):
                with patch("builtins.input") as prompt, patch.object(mac, "wake") as wake, contextlib.redirect_stderr(io.StringIO()) as errors:
                    self.assertEqual(mac.main(["--config-dir", directory, command]), 1)
                prompt.assert_not_called()
                wake.assert_not_called()
                self.assertIn("wake-pc setup", errors.getvalue())
            self.assertFalse(Path(directory, "config.json").exists())

    def test_explicit_setup_does_not_wake(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("builtins.input", side_effect=["Desk PC", "001122334455", "192.0.2.10", ""]), patch.object(mac, "wake") as wake:
                self.assertEqual(mac.main(["--config-dir", directory, "setup"]), 0)
            wake.assert_not_called()
            self.assertEqual(mac.ConfigStore(directory).load()["pc_name"], "Desk PC")

    def test_sleep_requires_confirmation_and_pairing_without_prompting(self):
        with tempfile.TemporaryDirectory() as directory:
            store = mac.ConfigStore(directory)
            store.save(self.config)
            with patch.object(mac, "send_sleep_request") as send, patch("builtins.input") as prompt, contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(mac.main(["--config-dir", directory, "sleep"]), 1)
                self.assertEqual(mac.main(["--config-dir", directory, "sleep", "--yes"]), 1)
                send.assert_not_called()
                prompt.assert_not_called()

    def test_confirmed_sleep_sends_once_using_saved_credential(self):
        with tempfile.TemporaryDirectory() as directory:
            self.config.update(sleep_device_id="a" * 32, sleep_secret="b" * 64)
            mac.ConfigStore(directory).save(self.config)
            with patch.object(mac, "send_sleep_request", return_value="Sleep accepted") as send, patch.object(mac, "wake") as wake:
                self.assertEqual(mac.main(["--config-dir", directory, "sleep", "--yes"]), 0)
            send.assert_called_once_with("192.0.2.10", 47653, "a" * 32, "b" * 64)
            wake.assert_not_called()
            self.assertNotIn("b" * 64, self.output.getvalue())
            self.assertIn("not been verified", self.output.getvalue())

    def test_failed_sleep_is_not_retried_or_reported_as_accepted(self):
        with tempfile.TemporaryDirectory() as directory:
            self.config.update(sleep_device_id="a" * 32, sleep_secret="b" * 64)
            mac.ConfigStore(directory).save(self.config)
            with patch.object(mac, "send_sleep_request", side_effect=mac.SleepBridgeError("Request failed")) as send, contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(mac.main(["--config-dir", directory, "sleep", "--yes"]), 1)
            self.assertEqual(send.call_count, 1)
            self.assertNotIn("accepted", self.output.getvalue())


if __name__ == "__main__":
    unittest.main()
