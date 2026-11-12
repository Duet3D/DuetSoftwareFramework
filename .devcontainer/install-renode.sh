#!/bin/sh
# Install Renode, the emulator that runs the firmware images for the emulation tests (src/DuetEmulation),
# into /opt/renode, checking the archive against its published SHA-256, and the SVD register
# descriptions its platforms apply that the device packs in lib/CoreN2G do not have.

set -eu

: "${RENODE_VERSION:?RENODE_VERSION must be set}"

arch="$(dpkg --print-architecture)"
case "${arch}" in
    amd64)
        renode_archive="renode-${RENODE_VERSION}.linux-portable.tar.gz"
        renode_sha256="4ba7c68b59e2447f188ef4b4b112fcccd2802582c460beedceb63b84e5605a5f"
        ;;
    arm64)
        renode_archive="renode-${RENODE_VERSION}.linux-arm64-portable.tar.gz"
        renode_sha256="e9051578411441bfb47b77de0381cfba3badce03663367fab561472fd7807f1f"
        ;;
    *)
        echo "Unsupported architecture: ${arch}" >&2
        exit 1
        ;;
esac

curl -fL "https://github.com/renode/renode/releases/download/v${RENODE_VERSION}/${renode_archive}" -o "/tmp/${renode_archive}"
echo "${renode_sha256}  /tmp/${renode_archive}" | sha256sum -c -
mkdir -p /opt/renode
tar -xzf "/tmp/${renode_archive}" -C /opt/renode --strip-components=1
ln -sf /opt/renode/renode /usr/local/bin/renode
rm -f "/tmp/${renode_archive}"

# The SAME70's SVD, as Antmicro publishes it for Renode's own SAME70 platform
mkdir -p /opt/renode/svd
curl -fL "https://dl.antmicro.com/projects/renode/svd/ATSAME70Q21.svd" -o /opt/renode/svd/ATSAME70Q21.svd
echo "2906bec73828b9e7eeb6d66d23b612bc07529cfc985242d0fb2effcfa33e26bb  /opt/renode/svd/ATSAME70Q21.svd" | sha256sum -c -
