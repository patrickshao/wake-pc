import hashlib
import hmac
import http.server
import socket
import json
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import MagicMock, patch

from wakepc import (
    ConfigError,
    ConfigStore,
    PairingCredential,
    SleepBridgeError,
    build_magic_packet,
    build_sleep_signature,
    normalize_mac,
    normalize_pairing_code,
    public_config,
    request_bridge_version,
    request_pairing_key,
    send_magic_packets,
    send_sleep_request,
    tcp_reachable,
)


class WakePcTests(unittest.TestCase):
    def test_normalizes_common_mac_formats(self) -> None:
        expected = "00:11:22:33:44:55"
        for value in (
            "00:11:22:33:44:55",
            "00-11-22-33-44-55",
            "0011.2233.4455",
            "001122334455",
        ):
            with self.subTest(value=value):
                self.assertEqual(normalize_mac(value), expected)

    def test_rejects_malformed_mac(self) -> None:
        for value in ("", "00:11:22:33:44", "not-a-mac", "00:11-22:33-44:55"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ConfigError, "Invalid MAC address"):
                    normalize_mac(value)

    def test_magic_packet_is_standard_102_bytes(self) -> None:
        packet = build_magic_packet("00:11:22:33:44:55")
        self.assertEqual(len(packet), 102)
        self.assertEqual(packet[:6], b"\xff" * 6)
        self.assertEqual(packet[6:], bytes.fromhex("001122334455") * 16)

    def test_udp_sender_delivers_three_magic_packets(self) -> None:
        receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        receiver.bind(("127.0.0.1", 0))
        receiver.settimeout(1)
        port = receiver.getsockname()[1]

        sent = send_magic_packets(
            "00:11:22:33:44:55", "127.0.0.1", port, 3, 0
        )
        packets = [receiver.recvfrom(256)[0] for _ in range(3)]
        receiver.close()

        self.assertEqual(sent, 3)
        self.assertTrue(all(packet == build_magic_packet("001122334455") for packet in packets))

    def test_tcp_reachability(self) -> None:
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.bind(("127.0.0.1", 0))
        server.listen(1)
        port = server.getsockname()[1]

        accepted = threading.Event()

        def accept_once() -> None:
            connection, _ = server.accept()
            connection.close()
            accepted.set()

        thread = threading.Thread(target=accept_once, daemon=True)
        thread.start()
        self.assertTrue(tcp_reachable("127.0.0.1", port, 0.5))
        self.assertTrue(accepted.wait(1))
        server.close()
        thread.join(timeout=1)

        closed_socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        closed_socket.bind(("127.0.0.1", 0))
        closed_port = closed_socket.getsockname()[1]
        closed_socket.close()
        self.assertFalse(tcp_reachable("127.0.0.1", closed_port, 0.1))

    def test_configuration_round_trip_is_normalized(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = ConfigStore(directory)
            saved = store.save(
                {
                    "pc_name": "  Living Room PC ",
                    "mac_address": "00-11-22-33-44-55",
                    "host_ip": "192.168.1.50",
                    "broadcast_address": "192.168.1.255",
                    "wake_port": "9",
                    "status_port": "27036",
                    "status_enabled": True,
                }
            )
            self.assertEqual(saved["pc_name"], "Living Room PC")
            self.assertEqual(saved["mac_address"], "00:11:22:33:44:55")
            self.assertEqual(store.load(), saved)

    def test_sleep_protocol_signature_matches_published_vector(self) -> None:
        secret = "000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f"
        signature = build_sleep_signature(
            secret, 1784304000, "AQIDBAUGBwgJCgsMDQ4PEA"
        )
        self.assertEqual(
            signature,
            "daf5287d35424fcadab946bd2fa473f9af70c836eef68064d51c72d43b2f0683",
        )

    def test_pairing_code_requires_exactly_six_digits(self) -> None:
        self.assertEqual(normalize_pairing_code(" 012345 "), "012345")
        for value in ("12345", "1234567", "abcdef", "12 3456"):
            with self.subTest(value=value):
                with self.assertRaisesRegex(ConfigError, "exactly 6 digits"):
                    normalize_pairing_code(value)

    def test_pairing_exchanges_short_code_for_full_key(self) -> None:
        received: dict[str, object] = {}
        secret = "ef" * 32

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                received["path"] = self.path
                received["code"] = self.headers["X-Wake-PC-Pairing-Code"]
                received["protocol"] = self.headers["X-Wake-PC-Protocol"]
                payload = json.dumps(
                    {
                        "ok": True,
                        "device_id": "0123456789abcdef0123456789abcdef",
                        "secret_hex": secret,
                    }
                ).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format: str, *args: object) -> None:
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request, daemon=True)
        thread.start()
        try:
            returned = request_pairing_key(
                "127.0.0.1", server.server_port, "012345"
            )
        finally:
            server.server_close()
            thread.join(timeout=1)

        self.assertEqual(
            returned,
            PairingCredential(
                device_id="0123456789abcdef0123456789abcdef",
                secret_hex=secret,
            ),
        )
        self.assertEqual(received["path"], "/v1/pair")
        self.assertEqual(received["code"], "012345")
        self.assertEqual(received["protocol"], "2")

    def test_pairing_with_older_bridge_defaults_to_primary_device(self) -> None:
        response = MagicMock()
        response.status = 200
        response.read.return_value = json.dumps(
            {"ok": True, "secret_hex": "ab" * 32}
        ).encode()
        connection = MagicMock()
        connection.getresponse.return_value = response
        with patch(
            "wakepc.http.client.HTTPConnection", return_value=connection
        ):
            credential = request_pairing_key(
                "127.0.0.1", 47653, "123456"
            )
        self.assertEqual(credential.device_id, "primary")
        self.assertEqual(credential.secret_hex, "ab" * 32)

    def test_pairing_expiration_has_actionable_error(self) -> None:
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                self.send_response(410)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, format: str, *args: object) -> None:
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request, daemon=True)
        thread.start()
        try:
            with self.assertRaisesRegex(SleepBridgeError, "expired"):
                request_pairing_key("127.0.0.1", server.server_port, "123456")
        finally:
            server.server_close()
            thread.join(timeout=1)

    def test_pairing_timeout_identifies_endpoint_and_firewall(self) -> None:
        with patch("wakepc.http.client.HTTPConnection") as connection_type:
            connection_type.return_value.request.side_effect = socket.timeout()
            with self.assertRaisesRegex(
                SleepBridgeError,
                r"Timed out reaching Sleep Bridge at 192\.0\.2\.144:47653.*Firewall",
            ):
                request_pairing_key("192.0.2.144", 47653, "123456")
            connection_type.return_value.close.assert_called_once_with()

    def test_sleep_request_is_signed_and_has_no_body(self) -> None:
        received: dict[str, object] = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                received["path"] = self.path
                received["headers"] = dict(self.headers)
                received["body"] = self.rfile.read(int(self.headers["Content-Length"]))
                self.send_response(202)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, format: str, *args: object) -> None:
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request, daemon=True)
        thread.start()
        secret = "11" * 32
        nonce = "AQIDBAUGBwgJCgsMDQ4PEA"
        timestamp = 1784304000
        try:
            result = send_sleep_request(
                "127.0.0.1",
                server.server_port,
                "primary",
                secret,
                timestamp=timestamp,
                nonce=nonce,
            )
        finally:
            server.server_close()
            thread.join(timeout=1)

        headers = received["headers"]
        self.assertEqual(result, "Sleep accepted")
        self.assertEqual(received["path"], "/v1/sleep")
        self.assertEqual(received["body"], b"")
        self.assertEqual(headers["X-Wake-PC-Timestamp"], str(timestamp))
        self.assertEqual(headers["X-Wake-PC-Nonce"], nonce)
        self.assertEqual(headers["X-Wake-PC-Device-ID"], "primary")
        canonical = f"POST\n/v1/sleep\n{timestamp}\n{nonce}\n".encode()
        expected = hmac.new(bytes.fromhex(secret), canonical, hashlib.sha256).hexdigest()
        self.assertEqual(headers["X-Wake-PC-Signature"], expected)

    def test_sleep_request_maps_authentication_failure(self) -> None:
        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                self.send_response(401)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, format: str, *args: object) -> None:
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request, daemon=True)
        thread.start()
        try:
            with self.assertRaisesRegex(SleepBridgeError, "rejected the pairing key"):
                send_sleep_request(
                    "127.0.0.1",
                    server.server_port,
                    "primary",
                    "22" * 32,
                )
        finally:
            server.server_close()
            thread.join(timeout=1)

    def test_bridge_version_request_is_authenticated(self) -> None:
        received: dict[str, object] = {}

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                received["path"] = self.path
                received["headers"] = dict(self.headers)
                payload = json.dumps(
                    {"ok": True, "message": "reachable", "version": "1.6.5"}
                ).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            def log_message(self, format: str, *args: object) -> None:
                pass

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.handle_request, daemon=True)
        thread.start()
        secret = "33" * 32
        nonce = "AQIDBAUGBwgJCgsMDQ4PEL"
        timestamp = 1784304000
        try:
            version = request_bridge_version(
                "127.0.0.1",
                server.server_port,
                "primary",
                secret,
                timestamp=timestamp,
                nonce=nonce,
            )
        finally:
            server.server_close()
            thread.join(timeout=1)

        headers = received["headers"]
        canonical = f"POST\n/v1/status\n{timestamp}\n{nonce}\n".encode()
        expected = hmac.new(bytes.fromhex(secret), canonical, hashlib.sha256).hexdigest()
        self.assertEqual(version, "1.6.5")
        self.assertEqual(received["path"], "/v1/status")
        self.assertEqual(headers["X-Wake-PC-Signature"], expected)

    def test_older_bridge_can_remain_paired_without_reporting_version(self) -> None:
        response = MagicMock()
        response.status = 404
        response.read.return_value = b""
        connection = MagicMock()
        connection.getresponse.return_value = response
        with patch(
            "wakepc.http.client.HTTPConnection", return_value=connection
        ):
            version = request_bridge_version(
                "127.0.0.1", 47653, "primary", "44" * 32
            )
        self.assertIsNone(version)

    def test_pairing_key_is_preserved_but_never_returned_publicly(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            store = ConfigStore(directory)
            first = store.save(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "192.168.1.50",
                    "sleep_enabled": True,
                    "sleep_secret": "ab" * 32,
                }
            )
            updated = store.save(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "192.168.1.50",
                    "sleep_enabled": True,
                    "sleep_secret": "",
                }
            )
            self.assertEqual(updated["sleep_secret"], first["sleep_secret"])
            self.assertEqual(updated["sleep_device_id"], "primary")
            visible = public_config(updated)
            self.assertTrue(visible["sleep_paired"])
            self.assertNotIn("sleep_secret", visible)
            self.assertNotIn("sleep_device_id", visible)

    def test_existing_secret_migrates_to_primary_device(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "config.json").write_text(
                json.dumps(
                    {
                        "mac_address": "00:11:22:33:44:55",
                        "host_ip": "192.168.1.50",
                        "sleep_enabled": True,
                        "sleep_secret": "ab" * 32,
                    }
                ),
                encoding="utf-8",
            )
            loaded = ConfigStore(directory).load()
            self.assertEqual(loaded["sleep_device_id"], "primary")
            self.assertEqual(loaded["sleep_secret"], "ab" * 32)

    def test_status_can_be_disabled_without_a_host_ip(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            saved = ConfigStore(directory).save(
                {
                    "mac_address": "00:11:22:33:44:55",
                    "host_ip": "",
                    "status_enabled": False,
                }
            )
            self.assertEqual(saved["host_ip"], "")
            self.assertFalse(saved["status_enabled"])

    def test_corrupt_json_falls_back_to_defaults(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "config.json").write_text("{not json", encoding="utf-8")
            self.assertEqual(ConfigStore(directory).load()["pc_name"], "Gaming PC")

    def test_bad_persisted_fields_are_sanitized_independently(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            Path(directory, "config.json").write_text(
                json.dumps(
                    {
                        "pc_name": "  Living Room  ",
                        "mac_address": "bad",
                        "host_ip": "999.1.1.1",
                        "broadcast_address": "192.168.1.255",
                        "wake_port": 0,
                        "status_port": 27037,
                        "status_enabled": "false",
                    }
                ),
                encoding="utf-8",
            )
            loaded = ConfigStore(directory).load()
            self.assertEqual(loaded["pc_name"], "Living Room")
            self.assertEqual(loaded["mac_address"], "")
            self.assertEqual(loaded["host_ip"], "")
            self.assertEqual(loaded["broadcast_address"], "192.168.1.255")
            self.assertEqual(loaded["wake_port"], 9)
            self.assertEqual(loaded["status_port"], 27037)
            self.assertTrue(loaded["status_enabled"])

    def test_json_field_matrix_cannot_break_config_loading(self) -> None:
        values = [None, True, False, 0, -1, 65536, 1.5, "", "bad", [], {}]
        expected_keys = {
            "pc_name",
            "mac_address",
            "host_ip",
            "broadcast_address",
            "wake_port",
            "status_port",
            "status_enabled",
            "sleep_enabled",
            "sleep_port",
            "sleep_device_id",
            "sleep_secret",
        }
        for field in expected_keys:
            for value in values:
                with self.subTest(field=field, value=value):
                    with tempfile.TemporaryDirectory() as directory:
                        Path(directory, "config.json").write_text(
                            json.dumps({field: value}), encoding="utf-8"
                        )
                        loaded = ConfigStore(directory).load()
                        self.assertEqual(set(loaded), expected_keys)
                        self.assertIsInstance(loaded["pc_name"], str)
                        self.assertIsInstance(loaded["mac_address"], str)
                        self.assertIsInstance(loaded["host_ip"], str)
                        self.assertIsInstance(loaded["broadcast_address"], str)
                        self.assertIs(type(loaded["wake_port"]), int)
                        self.assertIs(type(loaded["status_port"]), int)
                        self.assertIs(type(loaded["status_enabled"]), bool)
                        self.assertIs(type(loaded["sleep_enabled"]), bool)
                        self.assertIs(type(loaded["sleep_port"]), int)
                        self.assertIsInstance(loaded["sleep_device_id"], str)
                        self.assertIsInstance(loaded["sleep_secret"], str)


if __name__ == "__main__":
    unittest.main()
