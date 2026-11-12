#!/bin/sh
# Install Renode, the emulator that runs the firmware images for the emulation tests (src/DuetEmulation),
# into /opt/renode, checking the archive against its published SHA-256.

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
