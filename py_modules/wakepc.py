"""Network and configuration primitives loaded from Decky's py_modules path."""

from __future__ import annotations

import hashlib
import hmac
import http.client
import ipaddress
import json
import os
import re
import secrets
import socket
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any


DEFAULT_CONFIG: dict[str, Any] = {
    "pc_name": "Gaming PC",
    "mac_address": "",
    "host_ip": "",
    "broadcast_address": "",
    "wake_port": 9,
    "status_port": 27036,
    "status_enabled": True,
    "sleep_enabled": False,
    "sleep_port": 47653,
    "sleep_device_id": "",
    "sleep_secret": "",
}

_PLAIN_MAC = re.compile(r"^[0-9a-fA-F]{12}$")
_SEPARATED_MAC = re.compile(
    r"^[0-9a-fA-F]{2}(?P<sep>[:-])[0-9a-fA-F]{2}"
    r"(?P=sep)[0-9a-fA-F]{2}(?P=sep)[0-9a-fA-F]{2}"
    r"(?P=sep)[0-9a-fA-F]{2}(?P=sep)[0-9a-fA-F]{2}$"
)
_DOTTED_MAC = re.compile(r"^[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}$")
_SLEEP_SECRET = re.compile(r"^[0-9a-fA-F]{64}$")
_SLEEP_DEVICE_ID = re.compile(r"^(?:primary|[0-9a-f]{32})$")
_PAIRING_CODE = re.compile(r"^[0-9]{6}$")
_BRIDGE_VERSION = re.compile(r"^[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$")
_SLEEP_PATH = "/v1/sleep"
_STATUS_PATH = "/v1/status"
_PAIR_PATH = "/v1/pair"
_NETWORK_RECOVERY_HINT = (
    "Open Setup & status from the Windows companion tray "
    "and check the saved PC IP address in your client."
)


class ConfigError(ValueError):
    """Raised when user configuration is invalid."""


class SleepBridgeError(RuntimeError):
    """Raised when the optional Windows Sleep Bridge rejects or misses a request."""


class SleepBridgeConnectionError(SleepBridgeError):
    """Raised when the Windows Sleep Bridge cannot be reached."""


@dataclass(frozen=True)
class PairingCredential:
    """One client-specific credential returned by Sleep Bridge pairing."""

    device_id: str
    secret_hex: str


def _bridge_connection_error(
    host_ip: str, port: int, error: BaseException
) -> SleepBridgeConnectionError:
    endpoint = f"{host_ip}:{port}"
    if isinstance(error, ConnectionRefusedError):
        return SleepBridgeConnectionError(
            f"Sleep Bridge is not running at {endpoint}. "
            f"Start it from the Windows Start menu. {_NETWORK_RECOVERY_HINT}"
        )
    if isinstance(error, (TimeoutError, socket.timeout)):
        return SleepBridgeConnectionError(
            f"Timed out reaching Sleep Bridge at {endpoint}. "
            f"Check the saved PC IP and Windows Firewall rule. {_NETWORK_RECOVERY_HINT}"
        )
    return SleepBridgeConnectionError(
        f"Could not connect to Sleep Bridge at {endpoint}. "
        f"Check the saved PC IP and Windows listener. {_NETWORK_RECOVERY_HINT}"
    )


def normalize_mac(value: str) -> str:
    """Validate a common MAC format and return uppercase colon notation."""
    candidate = str(value).strip()
    if not (
        _PLAIN_MAC.fullmatch(candidate)
        or _SEPARATED_MAC.fullmatch(candidate)
        or _DOTTED_MAC.fullmatch(candidate)
    ):
        raise ConfigError(
            "Invalid MAC address. Use 00:11:22:33:44:55, "
            "00-11-22-33-44-55, or 0011.2233.4455."
        )

    compact = re.sub(r"[:-]|\.", "", candidate).upper()
    return ":".join(compact[index : index + 2] for index in range(0, 12, 2))


def build_magic_packet(mac_address: str) -> bytes:
    """Build the 102-byte standard Wake-on-LAN magic packet."""
    normalized = normalize_mac(mac_address)
    mac_bytes = bytes.fromhex(normalized.replace(":", ""))
    return b"\xff" * 6 + mac_bytes * 16


def _ipv4(value: Any, field_name: str, *, optional: bool = False) -> str:
    candidate = str(value or "").strip()
    if optional and not candidate:
        return ""
    try:
        return str(ipaddress.IPv4Address(candidate))
    except ipaddress.AddressValueError as error:
        raise ConfigError(f"{field_name} must be a valid IPv4 address.") from error


def _port(value: Any, field_name: str) -> int:
    if isinstance(value, bool):
        raise ConfigError(f"{field_name} must be between 1 and 65535.")
    try:
        port = int(value)
    except (TypeError, ValueError, OverflowError) as error:
        raise ConfigError(f"{field_name} must be between 1 and 65535.") from error
    if not 1 <= port <= 65535:
        raise ConfigError(f"{field_name} must be between 1 and 65535.")
    return port


def _loaded_ipv4(value: Any, field_name: str) -> str:
    try:
        return _ipv4(value, field_name, optional=True)
    except ConfigError:
        return ""


def _loaded_port(value: Any, field_name: str, default: int) -> int:
    try:
        return _port(value, field_name)
    except ConfigError:
        return default


def normalize_sleep_secret(value: Any, *, optional: bool = False) -> str:
    """Return a lowercase 256-bit hexadecimal pairing key."""
    candidate = str(value or "").strip()
    if optional and not candidate:
        return ""
    if not _SLEEP_SECRET.fullmatch(candidate):
        raise ConfigError("Sleep Bridge pairing key must be 64 hexadecimal characters.")
    return candidate.lower()


def normalize_sleep_device_id(value: Any, *, optional: bool = False) -> str:
    """Return a bounded bridge device ID without exposing it to the frontend."""
    candidate = str(value or "").strip().lower()
    if optional and not candidate:
        return ""
    if not _SLEEP_DEVICE_ID.fullmatch(candidate):
        raise ConfigError("Sleep Bridge device ID is invalid.")
    return candidate


def normalize_pairing_code(value: Any) -> str:
    """Return a six-digit one-time pairing code."""
    candidate = str(value or "").strip()
    if not _PAIRING_CODE.fullmatch(candidate):
        raise ConfigError("Pairing code must contain exactly 6 digits.")
    return candidate


def sanitize_loaded_config(value: dict[str, Any]) -> dict[str, Any]:
    """Normalize persisted fields independently so one bad value cannot break the UI."""
    pc_name_value = value.get("pc_name", DEFAULT_CONFIG["pc_name"])
    pc_name = str(pc_name_value).strip() if pc_name_value is not None else ""
    if not pc_name or len(pc_name) > 64:
        pc_name = DEFAULT_CONFIG["pc_name"]

    mac_value = str(value.get("mac_address") or "").strip()
    if mac_value:
        try:
            mac_value = normalize_mac(mac_value)
        except ConfigError:
            mac_value = ""

    status_value = value.get("status_enabled", DEFAULT_CONFIG["status_enabled"])
    status_enabled = (
        status_value
        if isinstance(status_value, bool)
        else DEFAULT_CONFIG["status_enabled"]
    )
    sleep_value = value.get("sleep_enabled", DEFAULT_CONFIG["sleep_enabled"])
    sleep_enabled = (
        sleep_value
        if isinstance(sleep_value, bool)
        else DEFAULT_CONFIG["sleep_enabled"]
    )
    try:
        sleep_secret = normalize_sleep_secret(
            value.get("sleep_secret"), optional=True
        )
    except ConfigError:
        sleep_secret = ""
    try:
        sleep_device_id = normalize_sleep_device_id(
            value.get("sleep_device_id"), optional=True
        )
    except ConfigError:
        sleep_device_id = ""
        sleep_secret = ""
    if sleep_secret and not sleep_device_id:
        sleep_device_id = "primary"
    if not sleep_secret:
        sleep_device_id = ""

    return {
        "pc_name": pc_name,
        "mac_address": mac_value,
        "host_ip": _loaded_ipv4(value.get("host_ip"), "Local IP address"),
        "broadcast_address": _loaded_ipv4(
            value.get("broadcast_address"), "Broadcast address"
        ),
        "wake_port": _loaded_port(
            value.get("wake_port", DEFAULT_CONFIG["wake_port"]),
            "Wake UDP port",
            DEFAULT_CONFIG["wake_port"],
        ),
        "status_port": _loaded_port(
            value.get("status_port", DEFAULT_CONFIG["status_port"]),
            "Status TCP port",
            DEFAULT_CONFIG["status_port"],
        ),
        "status_enabled": status_enabled,
        "sleep_enabled": sleep_enabled,
        "sleep_port": _loaded_port(
            value.get("sleep_port", DEFAULT_CONFIG["sleep_port"]),
            "Sleep Bridge port",
            DEFAULT_CONFIG["sleep_port"],
        ),
        "sleep_device_id": sleep_device_id,
        "sleep_secret": sleep_secret,
    }


def public_config(value: dict[str, Any]) -> dict[str, Any]:
    """Return configuration safe for the Decky frontend without the pairing key."""
    public = dict(value)
    secret = public.pop("sleep_secret", "")
    device_id = public.pop("sleep_device_id", "")
    public["sleep_paired"] = bool(secret and device_id)
    return public


def validate_config(value: dict[str, Any]) -> dict[str, Any]:
    """Return a sanitized complete configuration or raise ConfigError."""
    if not isinstance(value, dict):
        raise ConfigError("Settings payload must be an object.")
    merged = {**DEFAULT_CONFIG, **(value or {})}
    pc_name = str(merged["pc_name"]).strip()
    if not pc_name:
        raise ConfigError("PC name is required.")
    if len(pc_name) > 64:
        raise ConfigError("PC name must be 64 characters or fewer.")

    status_enabled = merged["status_enabled"]
    if not isinstance(status_enabled, bool):
        raise ConfigError("Status checking must be enabled or disabled.")
    sleep_enabled = merged["sleep_enabled"]
    if not isinstance(sleep_enabled, bool):
        raise ConfigError("Sleep control must be enabled or disabled.")
    sleep_secret = normalize_sleep_secret(
        merged["sleep_secret"], optional=not sleep_enabled
    )
    sleep_device_id = normalize_sleep_device_id(
        merged["sleep_device_id"] or ("primary" if sleep_secret else ""),
        optional=not sleep_enabled,
    )

    return {
        "pc_name": pc_name,
        "mac_address": normalize_mac(str(merged["mac_address"])),
        "host_ip": _ipv4(
            merged["host_ip"],
            "Local IP address",
            optional=not (status_enabled or sleep_enabled),
        ),
        "broadcast_address": _ipv4(
            merged["broadcast_address"], "Broadcast address", optional=True
        ),
        "wake_port": _port(merged["wake_port"], "Wake UDP port"),
        "status_port": _port(merged["status_port"], "Status TCP port"),
        "status_enabled": status_enabled,
        "sleep_enabled": sleep_enabled,
        "sleep_port": _port(merged["sleep_port"], "Sleep Bridge port"),
        "sleep_device_id": sleep_device_id,
        "sleep_secret": sleep_secret,
    }


class ConfigStore:
    """Small atomic JSON store rooted in Decky's plugin settings directory."""

    def __init__(self, settings_directory: str | os.PathLike[str]):
        self.directory = Path(settings_directory)
        self.path = self.directory / "config.json"

    def load(self) -> dict[str, Any]:
        if not self.path.exists():
            return DEFAULT_CONFIG.copy()
        try:
            with self.path.open("r", encoding="utf-8") as config_file:
                loaded = json.load(config_file)
            if not isinstance(loaded, dict):
                return DEFAULT_CONFIG.copy()
            return sanitize_loaded_config(loaded)
        except (OSError, json.JSONDecodeError):
            return DEFAULT_CONFIG.copy()

    def save(self, value: dict[str, Any]) -> dict[str, Any]:
        if not isinstance(value, dict):
            raise ConfigError("Settings payload must be an object.")
        candidate = dict(value)
        current = self.load()
        if not str(candidate.get("sleep_secret") or "").strip():
            candidate["sleep_secret"] = current.get("sleep_secret", "")
            candidate["sleep_device_id"] = current.get("sleep_device_id", "")
        config = validate_config(candidate)
        self.directory.mkdir(parents=True, exist_ok=True)
        descriptor, temporary_path = tempfile.mkstemp(
            prefix="config-", suffix=".json", dir=self.directory
        )
        try:
            with os.fdopen(descriptor, "w", encoding="utf-8") as config_file:
                json.dump(config, config_file, indent=2)
                config_file.write("\n")
                config_file.flush()
                os.fsync(config_file.fileno())
            os.chmod(temporary_path, 0o600)
            os.replace(temporary_path, self.path)
        except Exception:
            try:
                os.unlink(temporary_path)
            except FileNotFoundError:
                pass
            raise
        return config


def build_sleep_signature(secret_hex: str, timestamp: int, nonce: str) -> str:
    """Build the Sleep Bridge HMAC signature for one request."""
    return _build_bridge_signature(secret_hex, _SLEEP_PATH, timestamp, nonce)


def _build_bridge_signature(
    secret_hex: str, path: str, timestamp: int, nonce: str
) -> str:
    """Build an HMAC signature for one fixed authenticated bridge route."""
    secret = bytes.fromhex(normalize_sleep_secret(secret_hex))
    if path not in {_SLEEP_PATH, _STATUS_PATH}:
        raise ConfigError("Sleep Bridge request path is invalid.")
    if not isinstance(timestamp, int) or isinstance(timestamp, bool):
        raise ConfigError("Sleep request timestamp is invalid.")
    if not re.fullmatch(r"[A-Za-z0-9_-]{16,64}", str(nonce)):
        raise ConfigError("Sleep request nonce is invalid.")
    canonical = f"POST\n{path}\n{timestamp}\n{nonce}\n".encode("utf-8")
    return hmac.new(secret, canonical, hashlib.sha256).hexdigest()


def request_pairing_key(
    host_ip: str,
    port: int,
    pairing_code: str,
    replace_device_id: str = "",
    *,
    timeout_seconds: float = 3.0,
) -> PairingCredential:
    """Exchange an expiring short code for a client-specific credential."""
    target = _ipv4(host_ip, "Local IP address")
    target_port = _port(port, "Sleep Bridge port")
    code = normalize_pairing_code(pairing_code)
    headers = {
        "Content-Length": "0",
        "X-Wake-PC-Pairing-Code": code,
        "X-Wake-PC-Protocol": "2",
    }
    replaced_id = normalize_sleep_device_id(
        replace_device_id, optional=True
    )
    if replaced_id:
        headers["X-Wake-PC-Replace-Device-ID"] = replaced_id

    connection = http.client.HTTPConnection(
        target, target_port, timeout=timeout_seconds
    )
    try:
        connection.request("POST", _PAIR_PATH, body=b"", headers=headers)
        response = connection.getresponse()
        body = response.read(4097)
        if len(body) > 4096:
            raise SleepBridgeError("Sleep Bridge returned an invalid response.")
        if response.status == 200:
            try:
                payload = json.loads(body.decode("utf-8"))
                secret = normalize_sleep_secret(payload.get("secret_hex"))
                device_id = normalize_sleep_device_id(
                    payload.get("device_id") or "primary"
                )
            except (AttributeError, UnicodeDecodeError, json.JSONDecodeError, ConfigError) as error:
                raise SleepBridgeError(
                    "Sleep Bridge returned an invalid pairing response."
                ) from error
            return PairingCredential(device_id=device_id, secret_hex=secret)
        if response.status == 401:
            raise SleepBridgeError("Pairing code was not accepted.")
        if response.status == 410:
            raise SleepBridgeError(
                "Pairing code expired. Request a new code from the Windows tray icon."
            )
        if response.status == 429:
            raise SleepBridgeError(
                "Too many pairing attempts. Request a new code from the Windows tray icon."
            )
        raise SleepBridgeError(f"Sleep Bridge returned HTTP {response.status}.")
    except SleepBridgeError:
        raise
    except (OSError, TimeoutError, http.client.HTTPException) as error:
        raise _bridge_connection_error(target, target_port, error) from error
    finally:
        connection.close()


def send_sleep_request(
    host_ip: str,
    port: int,
    device_id: str,
    secret_hex: str,
    *,
    timestamp: int | None = None,
    nonce: str | None = None,
    timeout_seconds: float = 3.0,
) -> str:
    """Send one authenticated request to the Windows Sleep Bridge."""
    target = _ipv4(host_ip, "Local IP address")
    target_port = _port(port, "Sleep Bridge port")
    client_id = normalize_sleep_device_id(device_id)
    secret = normalize_sleep_secret(secret_hex)
    request_timestamp = int(time.time()) if timestamp is None else timestamp
    request_nonce = secrets.token_urlsafe(16) if nonce is None else nonce
    signature = build_sleep_signature(secret, request_timestamp, request_nonce)
    headers = {
        "Content-Length": "0",
        "X-Wake-PC-Device-ID": client_id,
        "X-Wake-PC-Timestamp": str(request_timestamp),
        "X-Wake-PC-Nonce": request_nonce,
        "X-Wake-PC-Signature": signature,
    }

    connection = http.client.HTTPConnection(
        target, target_port, timeout=timeout_seconds
    )
    try:
        connection.request("POST", _SLEEP_PATH, body=b"", headers=headers)
        response = connection.getresponse()
        body = response.read(4097)
        if len(body) > 4096:
            raise SleepBridgeError("Sleep Bridge returned an invalid response.")
        if response.status == 202:
            return "Sleep accepted"
        if response.status == 401:
            raise SleepBridgeError("Sleep Bridge rejected the pairing key.")
        raise SleepBridgeError(f"Sleep Bridge returned HTTP {response.status}.")
    except SleepBridgeError:
        raise
    except (OSError, TimeoutError, http.client.HTTPException) as error:
        raise _bridge_connection_error(target, target_port, error) from error
    finally:
        connection.close()


def request_bridge_version(
    host_ip: str,
    port: int,
    device_id: str,
    secret_hex: str,
    *,
    timestamp: int | None = None,
    nonce: str | None = None,
    timeout_seconds: float = 1.5,
) -> str | None:
    """Return the authenticated bridge version, or None for an older bridge."""
    target = _ipv4(host_ip, "Local IP address")
    target_port = _port(port, "Sleep Bridge port")
    client_id = normalize_sleep_device_id(device_id)
    secret = normalize_sleep_secret(secret_hex)
    request_timestamp = int(time.time()) if timestamp is None else timestamp
    request_nonce = secrets.token_urlsafe(16) if nonce is None else nonce
    signature = _build_bridge_signature(
        secret, _STATUS_PATH, request_timestamp, request_nonce
    )
    headers = {
        "Content-Length": "0",
        "X-Wake-PC-Device-ID": client_id,
        "X-Wake-PC-Timestamp": str(request_timestamp),
        "X-Wake-PC-Nonce": request_nonce,
        "X-Wake-PC-Signature": signature,
    }

    connection = http.client.HTTPConnection(
        target, target_port, timeout=timeout_seconds
    )
    try:
        connection.request("POST", _STATUS_PATH, body=b"", headers=headers)
        response = connection.getresponse()
        body = response.read(4097)
        if len(body) > 4096:
            raise SleepBridgeError("Sleep Bridge returned an invalid response.")
        if response.status == 404:
            return None
        if response.status == 200:
            try:
                payload = json.loads(body.decode("utf-8"))
                version = str(payload.get("version", ""))
            except (AttributeError, UnicodeDecodeError, json.JSONDecodeError) as error:
                raise SleepBridgeError(
                    "Sleep Bridge returned an invalid status response."
                ) from error
            if not _BRIDGE_VERSION.fullmatch(version):
                raise SleepBridgeError(
                    "Sleep Bridge returned an invalid status response."
                )
            return version
        if response.status == 401:
            raise SleepBridgeError("Sleep Bridge rejected this device's pairing key.")
        raise SleepBridgeError(f"Sleep Bridge returned HTTP {response.status}.")
    except SleepBridgeError:
        raise
    except (OSError, TimeoutError, http.client.HTTPException) as error:
        raise _bridge_connection_error(target, target_port, error) from error
    finally:
        connection.close()


def send_magic_packets(
    mac_address: str,
    broadcast_address: str = "",
    port: int = 9,
    packet_count: int = 3,
    delay_seconds: float = 0.2,
) -> int:
    """Send repeated magic packets and return the number successfully sent."""
    packet = build_magic_packet(mac_address)
    target = _ipv4(broadcast_address, "Broadcast address", optional=True)
    target = target or "255.255.255.255"
    wake_port = _port(port, "Wake UDP port")
    if not 1 <= packet_count <= 10:
        raise ValueError("Packet count must be between 1 and 10.")

    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as wake_socket:
        wake_socket.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        for index in range(packet_count):
            wake_socket.sendto(packet, (target, wake_port))
            if index + 1 < packet_count:
                time.sleep(delay_seconds)
    return packet_count


def tcp_reachable(host_ip: str, port: int, timeout_seconds: float = 1.5) -> bool:
    """Return whether a TCP connection can be established within the timeout."""
    target = _ipv4(host_ip, "Local IP address")
    target_port = _port(port, "Status TCP port")
    try:
        with socket.create_connection((target, target_port), timeout=timeout_seconds):
            return True
    except (OSError, TimeoutError):
        return False
