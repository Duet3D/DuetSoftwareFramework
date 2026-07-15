#!/usr/bin/env bash
# Pull a minimal aarch64 sysroot from a running Pi so the cross toolchain can link against the Pi's
# actual glibc (Bookworm 2.36). This is only needed for a glibc-matched *dynamic* build -- most
# importantly the P/Invoke shared library (libduet_sbc.so). The standalone jitter-test binary is
# linked statically and does NOT need a sysroot.
#
# Usage:
#   scripts/fetch-pi-sysroot.sh <user@pi-host> [dest-dir]
#
# Then configure with the sysroot:
#   cmake --preset pi-arm64 -DDUET_SBC_SYSROOT=$(pwd)/pi-sysroot -DDUET_SBC_STATIC=OFF
#   cmake --build --preset pi-arm64
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "Usage: $0 <user@pi-host> [dest-dir]" >&2
    exit 2
fi

PI_HOST="$1"
DEST="${2:-$(cd "$(dirname "$0")/.." && pwd)/pi-sysroot}"

echo "Fetching aarch64 sysroot from ${PI_HOST} into ${DEST} ..."
mkdir -p "${DEST}"

# The libraries and headers needed to link a normal Linux userspace program.
rsync -az --rsync-path="rsync" \
    --include='/lib' --include='/lib/**' \
    --include='/usr' --include='/usr/lib' --include='/usr/lib/**' \
    --include='/usr/include' --include='/usr/include/**' \
    --exclude='*' \
    "${PI_HOST}:/" "${DEST}/"

echo "Done. Sysroot at ${DEST}"
