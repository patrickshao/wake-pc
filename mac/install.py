#!/usr/bin/env python3
"""Install a standalone user command; never touch PC settings or send packets."""
from __future__ import annotations
import argparse
import shlex
import shutil
import sys
from pathlib import Path

MARKER = "Wake PC standalone installation v1\n"


def install(prefix: Path, uninstall: bool = False) -> None:
    runtime = prefix / "share" / "wake-pc"
    command = prefix / "bin" / "wake-pc"
    marker = runtime / ".wake-pc-install"
    launcher = ("#!/bin/sh\n# Installed by Wake PC mac/install.py\nexec " +
                shlex.quote(sys.executable) + " " +
                shlex.quote(str(runtime / "mac" / "wake_pc.py")) + ' "$@"\n')
    if runtime.is_symlink() or command.is_symlink():
        raise ValueError("Existing symlink at installation path; leaving it unchanged.")
    if runtime.exists() and (not marker.is_file() or marker.read_text() != MARKER):
        raise ValueError(f"Unrecognized existing directory: {runtime}; leaving it unchanged.")
    if command.exists() and command.read_text() != launcher:
        raise ValueError(f"Existing command differs: {command}; leaving it unchanged.")
    if uninstall:
        if command.exists():
            command.unlink()
        if runtime.exists():
            shutil.rmtree(runtime)
        print("Removed Wake PC command and runtime. Saved PC settings were preserved.")
        return
    if runtime.exists() or command.exists():
        raise ValueError("Already installed. Uninstall first to replace the runtime; settings are preserved.")
    source = Path(__file__).resolve().parents[1]
    runtime.mkdir(parents=True)
    try:
        for relative in ("mac/wake_pc.py", "mac/Wake PC.command", "mac/install.py", "py_modules/wakepc.py", "docs/mac-wake.md", "LICENSE"):
            destination = runtime / relative
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source / relative, destination)
        marker.write_text(MARKER)
        command.parent.mkdir(parents=True, exist_ok=True)
        # Exclusive creation protects an existing command even if it appeared meanwhile.
        with command.open("x") as stream:
            stream.write(launcher)
        command.chmod(0o755)
    except Exception:
        shutil.rmtree(runtime)
        raise
    print(f"Installed command: {command}\nStandalone runtime: {runtime}")
    print("No wake sent. Existing settings are used without modification.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--prefix", type=Path, default=Path.home() / ".local")
    parser.add_argument("--uninstall", action="store_true")
    args = parser.parse_args()
    try:
        install(args.prefix.expanduser().resolve(), args.uninstall)
    except (OSError, ValueError) as error:
        print(f"Wake PC install: {error}", file=sys.stderr)
        raise SystemExit(1)
