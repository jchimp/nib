#!/usr/bin/env bash
# nib highlight fixture
set -euo pipefail

readonly TARGET="${1:-/etc/nginx}"

for conf in "$TARGET"/*.conf; do
    [[ -f "$conf" ]] || continue
    echo "checking ${conf}"
done
