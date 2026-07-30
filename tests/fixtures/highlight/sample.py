#!/usr/bin/env python3
"""nib highlight fixture."""
from pathlib import Path


def load(path: Path) -> dict[str, str]:
    """Read a key=value file."""
    result: dict[str, str] = {}
    for line in path.read_text().splitlines():
        if line and not line.startswith("#"):
            key, _, value = line.partition("=")
            result[key.strip()] = value.strip()
    return result
