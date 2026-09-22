"""Write a conventional SHA-256 sidecar without external shell utilities."""

from __future__ import annotations

import hashlib
import sys
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 3:
        raise SystemExit("usage: write_sha256.py INPUT OUTPUT")

    source = Path(sys.argv[1])
    destination = Path(sys.argv[2])
    with source.open("rb") as stream:
        hasher = hashlib.sha256()
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            hasher.update(chunk)
        digest = hasher.hexdigest()
    destination.write_text(f"{digest}  {source.name}\n", encoding="ascii")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
