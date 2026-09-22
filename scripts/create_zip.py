#!/usr/bin/env python3
"""Create a deterministic ZIP containing one source directory."""

from __future__ import annotations

import stat
import sys
import zipfile
from pathlib import Path


FIXED_TIMESTAMP = (2026, 7, 13, 0, 0, 0)


def main() -> None:
    if len(sys.argv) != 3:
        raise SystemExit(
            f"Usage: {Path(sys.argv[0]).name} <source-directory> <archive.zip>"
        )

    source = Path(sys.argv[1]).resolve()
    archive = Path(sys.argv[2]).resolve()
    if not source.is_dir():
        raise SystemExit(f"Source directory does not exist: {source}")

    archive.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(
        archive,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
    ) as package:
        for path in sorted(
            (item for item in source.rglob("*") if item.is_file()),
            key=lambda item: item.relative_to(source.parent).as_posix(),
        ):
            name = path.relative_to(source.parent).as_posix()
            info = zipfile.ZipInfo(name, date_time=FIXED_TIMESTAMP)
            info.compress_type = zipfile.ZIP_DEFLATED
            info.create_system = 3
            info.external_attr = (
                stat.S_IFREG | stat.S_IMODE(path.stat().st_mode)
            ) << 16
            package.writestr(
                info,
                path.read_bytes(),
                compress_type=zipfile.ZIP_DEFLATED,
                compresslevel=9,
            )


if __name__ == "__main__":
    main()
