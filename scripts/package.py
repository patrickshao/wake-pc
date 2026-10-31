#!/usr/bin/env python3
"""Package release artifacts using the Python standard library on any build host."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "out"


def run_script(name, *args):
    subprocess.run([sys.executable, str(ROOT / "scripts" / name), *map(str, args)], check=True)


def copy(source, destination):
    destination.parent.mkdir(parents=True, exist_ok=True)
    if source.is_dir():
        shutil.copytree(source, destination)
    else:
        shutil.copy2(source, destination)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--with-companion", action="store_true")
    args = parser.parse_args()
    plugin_version = json.loads((ROOT / "package.json").read_text())["version"]
    bridge_version = (ROOT / "windows-native/version.txt").read_text().strip()
    artifact_name = f"wake-pc-v{plugin_version}.zip"
    bridge_artifact_name = f"wake-pc-sleep-bridge-v{bridge_version}-setup.exe"
    bridge_installer = Path(os.environ.get(
        "WAKE_PC_BRIDGE_INSTALLER", ROOT / "windows-native/artifacts" / bridge_artifact_name
    )).resolve()
    if args.with_companion and not bridge_installer.is_file():
        parser.error("Sleep Bridge Setup.exe is missing. Run windows-native/scripts/build.ps1 first.")

    stage = OUT / "package/wake-pc"
    shutil.rmtree(stage.parent, ignore_errors=True)
    for name in ("dist/index.js", "dist/index.js.map", "main.py", "py_modules/wakepc.py",
                 "package.json", "plugin.json", "README.md", "SECURITY.md", "LICENSE",
                 "THIRD_PARTY_NOTICES.md"):
        copy(ROOT / name, stage / name)
    for path in (ROOT / "src").rglob("*"):
        if path.is_file():
            copy(path, stage / "source/src" / path.relative_to(ROOT / "src"))
    for name in ("package.json", "plugin.json", ".npmrc", "rollup.config.js", "tsconfig.json", "pnpm-lock.yaml"):
        copy(ROOT / name, stage / "source" / name)
    for name in ("LICENSE", "README.md", "package.json", "src", "dist"):
        copy(ROOT / "node_modules/@decky/api" / name, stage / "third_party/decky-api-1.1.3" / name)

    archive = OUT / artifact_name
    run_script("create_zip.py", stage, archive)
    run_script("validate_package.py", archive)
    artifacts = [archive]
    if args.with_companion:
        installer = OUT / bridge_artifact_name
        if bridge_installer != installer.resolve():
            shutil.copy2(bridge_installer, installer)
        run_script("validate_package.py", archive, installer, bridge_version)
        artifacts.append(installer)
    for artifact in artifacts:
        latest = OUT / ("wake-pc-latest.zip" if artifact == archive
                        else "wake-pc-sleep-bridge-latest-setup.exe")
        shutil.copy2(artifact, latest)
        for download in (artifact, latest):
            run_script("write_sha256.py", download, str(download) + ".sha256")
        print(artifact)


if __name__ == "__main__":
    main()
