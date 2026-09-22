#!/usr/bin/env python3
"""Validate the packaged Decky plugin and Windows Sleep Bridge boundaries."""

from __future__ import annotations

import json
import re
import subprocess
import sys
import tempfile
import zipfile
from pathlib import Path


EXPECTED_FILES = {
    "wake-pc/main.py",
    "wake-pc/plugin.json",
    "wake-pc/py_modules/wakepc.py",
    "wake-pc/dist/index.js",
    "wake-pc/dist/index.js.map",
    "wake-pc/source/src/index.tsx",
    "wake-pc/source/src/runtime.ts",
    "wake-pc/source/package.json",
    "wake-pc/source/plugin.json",
    "wake-pc/source/.npmrc",
    "wake-pc/source/pnpm-lock.yaml",
    "wake-pc/source/rollup.config.js",
    "wake-pc/source/tsconfig.json",
    "wake-pc/LICENSE",
    "wake-pc/THIRD_PARTY_NOTICES.md",
    "wake-pc/third_party/decky-api-1.1.3/LICENSE",
}

HARNESS = r"""
import asyncio
import importlib.util
import logging
import sys
import tempfile
import types
from pathlib import Path

plugin_dir = Path(sys.argv[1]).resolve()
module_dir = plugin_dir / "py_modules"

decky = types.ModuleType("decky")
decky.logger = logging.getLogger("wake-pc-package-validation")
decky.logger.disabled = True

with tempfile.TemporaryDirectory() as settings_dir:
    decky.DECKY_PLUGIN_SETTINGS_DIR = settings_dir
    sys.modules["decky"] = decky

    # Decky exposes py_modules, not the plugin root, to backend imports.
    sys.path.append(str(module_dir))
    spec = importlib.util.spec_from_file_location(
        "packaged_wake_pc_main", plugin_dir / "main.py"
    )
    if spec is None or spec.loader is None:
        raise RuntimeError("Could not load packaged main.py")

    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    plugin = module.Plugin()
    asyncio.run(plugin._main())
    config = asyncio.run(plugin.get_config())
    if config.get("pc_name") != "Gaming PC":
        raise RuntimeError("Packaged backend returned an invalid default configuration")
    if "sleep_secret" in config or config.get("sleep_paired") is not False:
        raise RuntimeError("Packaged backend exposed an invalid Sleep Bridge default")
    wake_result = asyncio.run(plugin.send_wake())
    if wake_result.get("ok") is not False:
        raise RuntimeError("Incomplete packaged configuration should reject Wake")
    save_result = asyncio.run(plugin.save_config({
        "mac_address": "00:11:22:33:44:55",
        "host_ip": "",
        "status_enabled": False,
    }))
    if save_result.get("ok") is not True:
        raise RuntimeError("Packaged backend could not save a valid configuration")
    status_result = asyncio.run(plugin.check_status())
    if status_result.get("enabled") is not False:
        raise RuntimeError("Packaged backend ignored disabled status checking")
    asyncio.run(plugin._unload())
"""


def validate_bridge(installer: Path, expected_version: str) -> None:
    match = re.fullmatch(
        r"wake-pc-sleep-bridge-v(\d+\.\d+\.\d+)-setup\.exe",
        installer.name,
    )
    if match is None:
        raise SystemExit(f"Unexpected Sleep Bridge installer name: {installer.name}")
    if match.group(1) != expected_version:
        raise SystemExit(
            "Sleep Bridge version mismatch: "
            f"expected {expected_version}, got {match.group(1)}"
        )
    executable = installer.read_bytes()
    if not executable.startswith(b"MZ") or len(executable) < 1_000_000:
        raise SystemExit("Sleep Bridge Setup.exe is missing or implausible")
    if b"Inno Setup Setup Data" not in executable[:1_000_000]:
        raise SystemExit("Sleep Bridge artifact is not an Inno Setup executable")
    print(
        "Windows Sleep Bridge Inno Setup artifact validated: "
        f"{installer} (version {match.group(1)})"
    )


def main() -> None:
    if len(sys.argv) not in (2, 4):
        raise SystemExit(
            f"Usage: {Path(sys.argv[0]).name} <plugin.zip> "
            "[<sleep-bridge-setup.exe> <companion-version>]"
        )

    archive = Path(sys.argv[1]).resolve()
    with zipfile.ZipFile(archive) as package:
        names = set(package.namelist())
        bad_member = package.testzip()
        if bad_member is not None:
            raise SystemExit(f"Plugin ZIP contains a corrupt member: {bad_member}")
        missing = EXPECTED_FILES - names
        if missing:
            raise SystemExit(f"Package is missing required files: {sorted(missing)}")
        if "wake-pc/wakepc.py" in names:
            raise SystemExit("Backend helper must be packaged under py_modules/")
        if any("__pycache__" in name or name.endswith(".pyc") for name in names):
            raise SystemExit("Package contains generated Python cache files")
        if any(
            "node_modules/" in name
            or name.endswith((".node", ".wasm", ".so", ".dylib", ".dll"))
            for name in names
        ):
            raise SystemExit(
                "Package contains dependency directories or native/Wasm libraries"
            )
        if any(not name.startswith("wake-pc/") or ".." in Path(name).parts for name in names):
            raise SystemExit("Package must contain exactly one wake-pc/ root")

        metadata = json.loads(package.read("wake-pc/package.json"))
        expected_name = f"wake-pc-v{metadata['version']}.zip"
        if archive.name != expected_name:
            raise SystemExit(
                f"Package version mismatch: expected {expected_name}, got {archive.name}"
            )
        manifest = json.loads(package.read("wake-pc/plugin.json"))
        if manifest.get("name") != "Wake PC" or manifest.get("api_version") != 1:
            raise SystemExit("Decky manifest identity or API version is invalid")
        flags = manifest.get("flags", [])
        if flags:
            raise SystemExit("Release packages must not enable development or privilege flags")

        with tempfile.TemporaryDirectory() as directory:
            package.extractall(directory)
            subprocess.run(
                [
                    sys.executable,
                    "-I",
                    "-c",
                    HARNESS,
                    str(Path(directory) / "wake-pc"),
                ],
                check=True,
            )

    print(f"Packaged backend startup validated: {archive}")
    if len(sys.argv) == 4:
        bridge_archive = Path(sys.argv[2]).resolve()
        bridge_version = sys.argv[3]
        validate_bridge(bridge_archive, bridge_version)


if __name__ == "__main__":
    main()
