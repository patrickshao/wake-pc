#!/usr/bin/env python3
"""Mac entry point using the same WOL and bridge protocol as the Decky plugin."""
from __future__ import annotations

import argparse
import getpass
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "py_modules"))
from wakepc import (  # noqa: E402
    ConfigError, ConfigStore, SleepBridgeConnectionError, SleepBridgeError,
    request_bridge_version, request_pairing_key, send_magic_packets, tcp_reachable,
    validate_config, send_sleep_request,
)

DEFAULT_DIRECTORY = Path.home() / "Library" / "Application Support" / "Wake PC"


def configure(store: ConfigStore) -> dict:
    current = store.load()
    print("Set up Wake PC on this Mac")
    print("On Windows, open Wake PC Companion → Setup & status → Status.")
    print("Copy the wired MAC and IP addresses shown there.")

    def prompt(label: str, key: str) -> str:
        default = str(current[key])
        answer = input(f"{label}{' [' + default + ']' if default else ''}: ").strip()
        return answer or default

    candidate = dict(current)
    candidate["pc_name"] = prompt("PC name", "pc_name")
    candidate["mac_address"] = prompt("Ethernet MAC address", "mac_address")
    candidate["host_ip"] = prompt("PC IPv4 address", "host_ip")
    candidate["broadcast_address"] = prompt(
        "LAN broadcast address (optional; blank uses 255.255.255.255)",
        "broadcast_address",
    )
    candidate["status_enabled"] = True
    result = store.save(candidate)
    print(f"Saved settings to {store.path}")
    print("Wake needs no pairing. Optional: run 'pair' while the PC is awake.")
    return result


def load_config(store: ConfigStore) -> dict:
    if not store.path.exists():
        raise ConfigError("No Mac settings yet. Run 'wake-pc setup' once with the PC's wired Ethernet MAC address and local IPv4 address from Windows Setup & status. No wake sent.")
    config = validate_config(store.load())
    if not config["mac_address"] or not config["host_ip"]:
        raise ConfigError("Incomplete PC settings. Run 'wake-pc setup' with the wired Ethernet MAC and local IPv4 address. No wake sent.")
    return config


def readiness(config: dict, timeout: float = 1.0) -> str | None:
    """Report only observed listeners; TCP alone does not prove streaming readiness."""
    host = config["host_ip"]
    if config["sleep_secret"] and config["sleep_device_id"]:
        try:
            version = request_bridge_version(
                host, config["sleep_port"], config["sleep_device_id"],
                config["sleep_secret"], timeout_seconds=timeout,
            )
            if version:
                return f"Windows bridge {version} responded (authenticated)."
        except SleepBridgeConnectionError:
            pass  # A sleeping PC is expected to be unreachable.
        # Rejected credentials are surfaced, not mistaken for a sleeping PC.
    elif tcp_reachable(host, config["sleep_port"], timeout):
        return f"PC bridge port {config['sleep_port']} is reachable (not authenticated)."
    if tcp_reachable(host, config["status_port"], timeout):
        return f"PC status port {config['status_port']} is reachable."
    return None


def wake(config: dict, wait_seconds: float) -> int:
    name = config["pc_name"]
    # WOL must not depend on a response from a bridge running on the sleeping PC.
    sent = send_magic_packets(
        config["mac_address"], config["broadcast_address"], config["wake_port"],
    )
    target = config["broadcast_address"] or "255.255.255.255"
    print(f"Sent {sent} wake packets for {name} to {target}:{config['wake_port']}.", flush=True)
    if wait_seconds == 0:
        print("Packet delivery does not confirm the PC woke. Run 'status' to check.")
        return 0
    print(f"Checking {config['host_ip']} for up to {wait_seconds:g} seconds…", flush=True)
    deadline = time.monotonic() + wait_seconds
    while time.monotonic() < deadline:
        try:
            observed = readiness(config, min(1.0, max(0.05, (deadline - time.monotonic()) / 2)))
        except SleepBridgeError as error:
            print(f"Wake packets were sent, but bridge verification failed: {error}", file=sys.stderr)
            print("Run 'pair' while Windows is awake to refresh this Mac's credential.", file=sys.stderr)
            return 1
        if observed:
            print(f"{name}: {observed}")
            print("This confirms a reachable service; it does not verify game streaming.")
            return 0
        remaining = deadline - time.monotonic()
        if remaining > 0:
            time.sleep(min(1.0, remaining))
    print("Wake sent; PC response not confirmed.")
    print("Check Ethernet/Wake-on-LAN, the saved PC address, and that both devices are on the same LAN.")
    print("Windows may be awake with its bridge and Steam status ports closed. See docs/mac-wake.md.")
    return 2


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--config-dir", type=Path, default=DEFAULT_DIRECTORY,
                        help="settings directory (default: ~/Library/Application Support/Wake PC)")
    parser.add_argument("--wait", type=int, default=45, metavar="SECONDS",
                        help="wake verification window, 0 to 300 seconds (default: 45)")
    parser.add_argument("--yes", action="store_true", help="confirm an explicit sleep request")
    parser.add_argument("command", nargs="?", default="wake",
                        choices=["setup", "wake", "status", "pair", "sleep", "launch"])
    args = parser.parse_args(argv)
    if not 0 <= args.wait <= 300:
        parser.error("--wait must be between 0 and 300 seconds")
    store = ConfigStore(args.config_dir.expanduser())
    try:
        if args.command == "setup":
            configure(store)
            return 0
        config = load_config(store)
        if args.command == "sleep":
            if not args.yes:
                raise ConfigError("To put the PC to sleep, run 'wake-pc sleep --yes'.")
            if not config["sleep_device_id"] or not config["sleep_secret"]:
                raise ConfigError("Pair this Mac first with 'wake-pc pair'.")
            send_sleep_request(config["host_ip"], config["sleep_port"],
                               config["sleep_device_id"], config["sleep_secret"])
            print("Sleep request accepted. Sleep has not been verified.")
            return 0
        if args.command == "pair":
            print("On Windows, open Wake PC Companion → Setup & status → Pair for Sleep…")
            credential = request_pairing_key(
                config["host_ip"], config["sleep_port"],
                getpass.getpass("Six-digit pairing code: "),
                config["sleep_device_id"],
            )
            config.update(sleep_device_id=credential.device_id, sleep_secret=credential.secret_hex)
            store.save(config)
            print("Mac paired for sleep and status. Wake needs no pairing.")
            return 0
        if args.command == "status":
            observed = readiness(config)
            print(f"{config['pc_name']}: {observed or 'No response; may be asleep, offline, or blocked by the network.'}")
            return 0 if observed else 2
        return wake(config, args.wait)
    except (ConfigError, SleepBridgeError, OSError) as error:
        print(f"Wake PC: {error}", file=sys.stderr)
        return 1
    except (KeyboardInterrupt, EOFError):
        print("\nCancelled.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
