"""Decky backend entry point for Wake PC."""

from __future__ import annotations

import asyncio
from typing import Any

import decky

from wakepc import (
    ConfigError,
    ConfigStore,
    SleepBridgeConnectionError,
    SleepBridgeError,
    public_config,
    request_pairing_key,
    request_bridge_version,
    send_magic_packets,
    send_sleep_request,
    tcp_reachable,
    validate_config,
)


class Plugin:
    def _get_store(self) -> ConfigStore:
        """Return an initialized store even if an RPC races plugin startup."""
        store = getattr(self, "store", None)
        if store is None:
            store = ConfigStore(decky.DECKY_PLUGIN_SETTINGS_DIR)
            self.store = store
        return store

    async def _main(self) -> None:
        self._get_store()
        decky.logger.info("Wake PC backend started")

    async def _unload(self) -> None:
        decky.logger.info("Wake PC backend stopped")

    async def _uninstall(self) -> None:
        try:
            self._get_store().path.unlink(missing_ok=True)
            decky.logger.info("Wake PC configuration removed")
        except OSError:
            decky.logger.exception("Could not remove Wake PC configuration")

    async def get_config(self) -> dict[str, Any]:
        return public_config(self._get_store().load())

    async def save_config(self, config: dict[str, Any]) -> dict[str, Any]:
        try:
            saved = await asyncio.to_thread(self._get_store().save, config)
            decky.logger.info("Wake PC configuration saved")
            return {
                "ok": True,
                "config": public_config(saved),
                "message": "Settings saved",
            }
        except ConfigError as error:
            decky.logger.warning("Configuration rejected: %s", error)
            return {"ok": False, "message": str(error)}
        except OSError:
            decky.logger.exception("Could not persist Wake PC configuration")
            return {"ok": False, "message": "Could not save settings."}

    async def send_wake(self) -> dict[str, Any]:
        config = self._get_store().load()
        try:
            count = await asyncio.to_thread(
                send_magic_packets,
                config.get("mac_address", ""),
                config.get("broadcast_address", ""),
                config.get("wake_port", 9),
                3,
                0.2,
            )
            decky.logger.info("Sent %d Wake-on-LAN packets", count)
            return {"ok": True, "message": "Wake packet sent"}
        except ConfigError as error:
            decky.logger.warning("Wake request rejected: %s", error)
            return {"ok": False, "message": str(error)}
        except OSError:
            decky.logger.exception("Wake-on-LAN network send failed")
            return {"ok": False, "message": "Network send failed"}

    async def check_status(self) -> dict[str, Any]:
        config = self._get_store().load()
        if not config.get("status_enabled", True):
            return {
                "ok": True,
                "enabled": False,
                "online": False,
                "message": "Status checking is disabled",
            }
        try:
            online = await asyncio.to_thread(
                tcp_reachable,
                config.get("host_ip", ""),
                config.get("status_port", 27036),
                1.5,
            )
            return {
                "ok": True,
                "enabled": True,
                "online": online,
                "message": "Host reachable" if online else "TCP port unreachable",
            }
        except ConfigError as error:
            decky.logger.warning("Status check rejected: %s", error)
            return {
                "ok": False,
                "enabled": True,
                "online": False,
                "message": str(error),
            }
        except OSError:
            decky.logger.exception("Status check failed")
            return {
                "ok": False,
                "enabled": True,
                "online": False,
                "message": "Status check failed",
            }

    async def send_sleep(self) -> dict[str, Any]:
        config = self._get_store().load()
        if not config.get("sleep_enabled", False):
            return {"ok": False, "message": "Sleep control is not configured."}
        try:
            message = await asyncio.to_thread(
                send_sleep_request,
                config.get("host_ip", ""),
                config.get("sleep_port", 47653),
                config.get("sleep_device_id", ""),
                config.get("sleep_secret", ""),
            )
            decky.logger.info("Sleep Bridge accepted the sleep request")
            return {"ok": True, "message": message}
        except (ConfigError, SleepBridgeError) as error:
            decky.logger.warning("Sleep request rejected: %s", error)
            return {"ok": False, "message": str(error)}
        except OSError:
            decky.logger.exception("Sleep Bridge request failed")
            return {"ok": False, "message": "Sleep Bridge request failed."}

    async def pair_sleep_bridge(
        self, config: dict[str, Any], pairing_code: str
    ) -> dict[str, Any]:
        try:
            current = self._get_store().load()
            candidate = {
                **current,
                **config,
                "sleep_enabled": False,
                "sleep_device_id": current.get("sleep_device_id", ""),
                "sleep_secret": current.get("sleep_secret", ""),
            }
            validated = validate_config(candidate)
            credential = await asyncio.to_thread(
                request_pairing_key,
                validated["host_ip"],
                validated["sleep_port"],
                pairing_code,
                current.get("sleep_device_id", ""),
            )
            validated["sleep_device_id"] = credential.device_id
            validated["sleep_secret"] = credential.secret_hex
            validated["sleep_enabled"] = True
            saved = await asyncio.to_thread(self._get_store().save, validated)
            decky.logger.info("Sleep Bridge pairing completed")
            return {
                "ok": True,
                "config": public_config(saved),
                "message": "Sleep control paired",
            }
        except (ConfigError, SleepBridgeError) as error:
            decky.logger.warning("Sleep Bridge pairing rejected: %s", error)
            return {"ok": False, "message": str(error)}
        except OSError:
            decky.logger.exception("Could not save Sleep Bridge pairing")
            return {
                "ok": False,
                "message": "Pairing succeeded, but settings could not be saved. Request a new code from the Windows tray icon.",
            }

    async def check_sleep_bridge(self) -> dict[str, Any]:
        config = self._get_store().load()
        if not config.get("sleep_secret"):
            return {
                "ok": True,
                "enabled": False,
                "online": False,
                "message": "Sleep control is not configured",
                "version": "",
            }
        try:
            version = await asyncio.to_thread(
                request_bridge_version,
                config.get("host_ip", ""),
                config.get("sleep_port", 47653),
                config.get("sleep_device_id", ""),
                config.get("sleep_secret", ""),
            )
            return {
                "ok": True,
                "enabled": True,
                "online": True,
                "message": "Sleep Bridge reachable",
                "version": version or "",
            }
        except SleepBridgeConnectionError as error:
            return {
                "ok": True,
                "enabled": True,
                "online": False,
                "message": str(error),
                "version": "",
            }
        except SleepBridgeError as error:
            decky.logger.warning("Sleep Bridge status rejected: %s", error)
            return {
                "ok": False,
                "enabled": True,
                "online": False,
                "message": str(error),
                "version": "",
            }
        except ConfigError as error:
            decky.logger.warning("Sleep Bridge check rejected: %s", error)
            return {
                "ok": False,
                "enabled": True,
                "online": False,
                "message": str(error),
                "version": "",
            }
        except OSError:
            decky.logger.exception("Sleep Bridge check failed")
            return {
                "ok": False,
                "enabled": True,
                "online": False,
                "message": "Sleep Bridge check failed",
                "version": "",
            }
