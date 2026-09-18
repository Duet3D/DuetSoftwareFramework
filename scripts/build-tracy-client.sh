#!/usr/bin/env bash
# Build the Tracy client library that a -p:Profiling=true build of DuetControlServer P/Invokes into.
#
# The Tracy-CSharp package ships a prebuilt TracyClient.so, but it is linked against glibc 2.38 and
# both the devcontainer and Raspberry Pi OS Bookworm are on 2.36, so it fails to load on either.
# The package's managed binding is fine; only the native half is built here, from the Tracy release
# the binding was generated against. Client and GUI must be the same release: Tracy's protocol
# carries a version and the server refuses a connection from anything else.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

# shellcheck source=scripts/native-arch.sh
source "$SCRIPT_DIR/native-arch.sh"

# Must match the Tracy-CSharp version in DuetControlServer.csproj and the Tracy GUI you connect with
TRACY_VERSION=v0.13.1
TRACY_REPO=https://github.com/wolfpld/tracy.git

ARCH="$(host_arch_rid)"
DEST_DIR=
ON_DEMAND=true
SRC_DIR="$REPO_ROOT/build/tracy/src"

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS]

Build libTracyClient.so for a profiling build of DuetControlServer.

Options:
  -a, --arch <rid>     Architecture to build for: linux-x64, linux-arm64 or linux-arm
                       (default: this machine, $ARCH)
  -v, --version <tag>  Tracy release to build (default: $TRACY_VERSION)
  -o, --dest-dir       Where to put the library (default: build/tracy/<arch>)
      --no-on-demand   Record from process start instead of waiting for the GUI to connect
  -h, --help           Show this help

The result lands in build/tracy/<arch>/TracyClient.so, which is where DuetControlServer.csproj
looks for it when built with -p:Profiling=true. Build it for the architecture you are profiling:
linux-arm64 for a Pi deployment, this machine's for the system tests.

Examples:
  $(basename "$0")
  $(basename "$0") --arch linux-arm64
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        -a|--arch)      ARCH="$2";          shift 2 ;;
        -v|--version)   TRACY_VERSION="$2"; shift 2 ;;
        -o|--dest-dir)  DEST_DIR="$2";      shift 2 ;;
        --no-on-demand) ON_DEMAND=false;    shift   ;;
        -h|--help)      usage; exit 0 ;;
        *)              echo "Unknown option: $1" >&2; usage; exit 1 ;;
    esac
done

if ! elf_machine_for "$ARCH" >/dev/null; then
    echo "Error: unsupported architecture '$ARCH'" >&2
    exit 1
fi

: "${DEST_DIR:=$REPO_ROOT/build/tracy/$ARCH}"
BUILD_DIR="$REPO_ROOT/build/tracy/$ARCH/cmake"

# The toolchain files live with libduet_sbc.so and carry the reasoning about glibc versions and
# sysroots; the same cross compilers produce a client the deployed DuetControlServer can load.
TOOLCHAIN_DIR="$REPO_ROOT/src/DuetSbcInterface/cmake"
declare -A TOOLCHAIN=(
    [linux-arm64]="$TOOLCHAIN_DIR/aarch64-linux-gnu.cmake"
    [linux-arm]="$TOOLCHAIN_DIR/arm-linux-gnueabihf.cmake"
)

echo "=== Tracy client $TRACY_VERSION for $ARCH ==="

# A shallow clone of the pinned tag is all that is needed, and re-cloning is the simplest way to
# move between tags: the checkout is disposable build output, not a working tree anyone edits.
if [[ -f "$SRC_DIR/.tracy-version" && "$(cat "$SRC_DIR/.tracy-version")" != "$TRACY_VERSION" ]]; then
    echo "    Source tree is $(cat "$SRC_DIR/.tracy-version"), fetching $TRACY_VERSION instead"
    rm -rf "$SRC_DIR"
fi
if [[ ! -d "$SRC_DIR" ]]; then
    echo "    Cloning $TRACY_REPO at $TRACY_VERSION"
    git clone --depth 1 --branch "$TRACY_VERSION" "$TRACY_REPO" "$SRC_DIR"
    echo "$TRACY_VERSION" > "$SRC_DIR/.tracy-version"
fi

cmake_args=(
    -S "$SRC_DIR"
    -B "$BUILD_DIR"
    -DCMAKE_BUILD_TYPE=Release
    -DBUILD_SHARED_LIBS=ON
)

# On demand the client records nothing until the GUI connects. Without it Tracy starts capturing
# when DuetControlServer starts and holds every zone in memory until someone connects, which on a
# long-running service is an unbounded amount of it.
if $ON_DEMAND; then
    cmake_args+=(-DTRACY_ON_DEMAND=ON)
    echo "    On-demand capture: the client idles until the Tracy GUI connects"
else
    echo "    Capturing from process start; memory grows until the GUI connects"
fi

if [[ -n "${TOOLCHAIN[$ARCH]:-}" ]]; then
    if [[ "$ARCH" == "$(host_arch_rid)" ]]; then
        echo "    Host is already $ARCH; building natively"
    else
        echo "    Cross-compiling with ${TOOLCHAIN[$ARCH]}"
        cmake_args+=("-DCMAKE_TOOLCHAIN_FILE=${TOOLCHAIN[$ARCH]}")
    fi
elif [[ "$ARCH" != "$(host_arch_rid)" ]]; then
    echo "Error: cannot build for $ARCH on $(host_arch_rid); no cross toolchain" >&2
    exit 1
fi

cmake "${cmake_args[@]}"
cmake --build "$BUILD_DIR" --target TracyClient -j"$(nproc)"

verify_elf_arch "$BUILD_DIR/libTracyClient.so" "$ARCH" "$BUILD_DIR"

# Named after the package's own native asset, which is the name the managed binding imports
mkdir -p "$DEST_DIR"
cp "$BUILD_DIR/libTracyClient.so" "$DEST_DIR/TracyClient.so"
echo "=== TracyClient.so -> $DEST_DIR/ ==="
