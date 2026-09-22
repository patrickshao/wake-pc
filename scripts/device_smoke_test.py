#!/usr/bin/env python3
"""Direct network smoke test for use on a SteamOS device before UI testing."""

import argparse
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "py_modules"))

from wakepc import send_magic_packets, tcp_reachable


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--mac", required=True, help="Host Ethernet MAC address")
    parser.add_argument("--ip", required=True, help="Host local IPv4 address")
    parser.add_argument("--broadcast", default="", help="Optional subnet broadcast")
    parser.add_argument("--wake-port", type=int, default=9)
    parser.add_argument("--status-port", type=int, default=27036)
    parser.add_argument(
        "--check-only", action="store_true", help="Do not send a wake packet"
    )
    args = parser.parse_args()

    if not args.check_only:
        count = send_magic_packets(
            args.mac, args.broadcast, args.wake_port, packet_count=3
        )
        print(f"Sent {count} magic packets")
    print(
        f"TCP {args.ip}:{args.status_port} is "
        f"{'reachable' if tcp_reachable(args.ip, args.status_port) else 'unreachable'}"
    )


if __name__ == "__main__":
    main()
