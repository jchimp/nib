"""Watches a directory of config files and reloads them when they change."""

from __future__ import annotations

import hashlib
import time
from dataclasses import dataclass, field
from pathlib import Path
from typing import Callable, Iterator

POLL_SECONDS = 1.5
IGNORED_SUFFIXES = frozenset({".swp", ".tmp", ".bak"})


@dataclass(slots=True)
class Watched:
    """One file on the watch list, and the digest we last saw."""

    path: Path
    digest: str = ""
    reloads: int = 0
    tags: list[str] = field(default_factory=list)

    @property
    def stale(self) -> bool:
        return self.digest != digest_of(self.path)


def digest_of(path: Path) -> str:
    """Return the SHA-256 of a file, or an empty string if it vanished."""
    try:
        return hashlib.sha256(path.read_bytes()).hexdigest()
    except FileNotFoundError:
        return ""


def discover(root: Path, pattern: str = "*.conf") -> Iterator[Watched]:
    for path in sorted(root.rglob(pattern)):
        if path.suffix in IGNORED_SUFFIXES or path.name.startswith("."):
            continue
        yield Watched(path=path, digest=digest_of(path), tags=["auto"])


def watch(root: Path, on_change: Callable[[Watched], None]) -> None:
    files = {w.path: w for w in discover(root)}
    print(f"watching {len(files)} file(s) under {root}")

    while True:
        for watched in files.values():
            if not watched.stale:
                continue
            watched.digest = digest_of(watched.path)
            watched.reloads += 1
            on_change(watched)
        time.sleep(POLL_SECONDS)


if __name__ == "__main__":
    watch(Path.cwd(), lambda w: print(f"  reloaded {w.path.name} (x{w.reloads})"))
