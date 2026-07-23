#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
BUILD_DIR="$REPO_ROOT/build"

declare -A PROJECT_SRC=(
    [DuetControlServer]="src/DuetControlServer"
    [DuetPiManagementPlugin]="src/DuetPiManagementPlugin"
    [DuetPluginService]="src/DuetPluginService"
    [DuetWebServer]="src/DuetWebServer"
    [CodeConsole]="src/CodeConsole"
    [CodeLogger]="src/CodeLogger"
    [CodeStream]="src/CodeStream"
    [CustomHttpEndpoint]="src/CustomHttpEndpoint"
    [ModelObserver]="src/ModelObserver"
    [PluginManager]="src/PluginManager"
    [DuetSbcInterface]="src/DuetSbcInterface"
)

# DuetSbcInterface is native (CMake), not a dotnet project: it builds libduet_sbc.so, the SPI
# transfer loop that DuetControlServer P/Invokes into. It is built separately below.
SBC_SRC_DIR="$REPO_ROOT/src/DuetSbcInterface"
SBC_LIB_NAME="libduet_sbc.so"
DEFAULT_SYSROOT="$SBC_SRC_DIR/pi-sysroot"

declare -A PROJECT_POSTINST=(
    [DuetControlServer]="pkg/deb/duetcontrolserver/DEBIAN/postinst"
    [DuetPiManagementPlugin]="pkg/deb/duetpimanagementplugin/DEBIAN/postinst"
    [DuetPluginService]="pkg/deb/duetpluginservice/DEBIAN/postinst"
    [DuetWebServer]="pkg/deb/duetwebserver/DEBIAN/postinst"
)

ALL_PROJECTS=(DuetControlServer DuetSbcInterface DuetPiManagementPlugin DuetPluginService DuetWebServer CodeConsole CodeLogger CodeStream CustomHttpEndpoint ModelObserver PluginManager)

SSH_USER="root"
TARGET=""
SELECTED=()
ALL=false
LOCAL=false
SKIP_BUILD=false
START_SERVICES=false
SYSROOT=""
FETCH_SYSROOT=false

usage() {
    cat <<EOF
Usage: $(basename "$0") [OPTIONS] [PROJECT...]

Build and deploy DSF projects to a remote target or locally.

Options:
  -t, --target <ip>    Target IP address for remote deploy
  -u, --user <user>    SSH user for remote target (default: root)
  -a, --all            Select all projects
  -l, --local          Deploy locally to /opt/dsf/bin instead of a remote target
      --skip-build     Skip the build step; only sync binaries and run postinst scripts
      --start-services  Start DSF services after deployment
      --sysroot <dir>  Pi sysroot to cross-link libduet_sbc.so against
                       (default: $DEFAULT_SYSROOT if it exists)
      --fetch-sysroot  (Re-)fetch the sysroot from the deploy target before building
  -h, --help           Show this help

Projects (specify one or more, or use --all):
$(printf '  %s\n' "${ALL_PROJECTS[@]}")

Notes:
  DuetSbcInterface is the native SPI transfer loop (libduet_sbc.so) that DuetControlServer
  P/Invokes into. DCS cannot run without it, so selecting DuetControlServer builds it too.

  Because a shared library links glibc dynamically, the .so must be built against the *target's*
  glibc. The devcontainer toolchain targets a newer glibc than Raspberry Pi OS Bookworm, so a
  plain cross-build produces a .so that fails to load on the Pi. This script therefore links
  against a sysroot copied from the Pi, fetching it automatically on first use when a deploy
  target is given.

Examples:
  $(basename "$0") --all --target 192.168.4.27
  $(basename "$0") -t 192.168.4.27 DuetControlServer DuetWebServer
  $(basename "$0") --all --local
  $(basename "$0") --skip-build --target 192.168.4.27 DuetControlServer
  $(basename "$0") -t 192.168.4.27 --fetch-sysroot DuetSbcInterface
EOF
}

# --- Argument parsing ---
while [[ $# -gt 0 ]]; do
    case "$1" in
        -t|--target)  TARGET="$2";    shift 2 ;;
        -u|--user)    SSH_USER="$2";  shift 2 ;;
        -a|--all)     ALL=true;       shift   ;;
        -l|--local)   LOCAL=true;     shift   ;;
        --skip-build) SKIP_BUILD=true; shift  ;;
        --start-services) START_SERVICES=true; shift ;;
        --sysroot)    SYSROOT="$2";   shift 2 ;;
        --fetch-sysroot) FETCH_SYSROOT=true; shift ;;
        -h|--help)    usage; exit 0           ;;
        -*)           echo "Unknown option: $1"; usage; exit 1 ;;
        All)          ALL=true; shift ;;
        *)            SELECTED+=("$1"); shift ;;
    esac
done

if $ALL; then
    SELECTED=("${ALL_PROJECTS[@]}")
fi

if [[ ${#SELECTED[@]} -eq 0 ]]; then
    echo "Error: no projects specified. Use --all or list project names." >&2
    usage
    exit 1
fi

DEPLOY=true

if ! $LOCAL && [[ -z "$TARGET" ]]; then
    DEPLOY=false
fi

# Validate project names
for project in "${SELECTED[@]}"; do
    if [[ -z "${PROJECT_SRC[$project]+x}" ]]; then
        echo "Error: unknown project '$project'" >&2
        exit 1
    fi
done

# DuetControlServer P/Invokes into libduet_sbc.so and will not start without it, so building or
# deploying DCS always implies the native interface too.
if [[ " ${SELECTED[*]} " == *" DuetControlServer "* && " ${SELECTED[*]} " != *" DuetSbcInterface "* ]]; then
    echo "=== DuetControlServer selected: including DuetSbcInterface (libduet_sbc.so) ==="
    SELECTED+=(DuetSbcInterface)
fi

# --- Native SPI interface (libduet_sbc.so) ---
# Cross-compiled here rather than on the Pi so a deploy needs no toolchain on the target. The catch
# is glibc: a .so links it dynamically, and the devcontainer toolchain targets a newer glibc than
# Raspberry Pi OS Bookworm, so linking against the container's libraries yields a .so that fails at
# dlopen with "GLIBC_2.3x not found". Linking against a sysroot copied from the Pi avoids that.
resolve_sysroot() {
    # An explicit --sysroot always wins
    if [[ -n "$SYSROOT" ]]; then
        if [[ ! -d "$SYSROOT" ]]; then
            echo "Error: sysroot '$SYSROOT' does not exist" >&2
            exit 1
        fi
        return
    fi

    if $FETCH_SYSROOT || [[ ! -d "$DEFAULT_SYSROOT" ]]; then
        if [[ -n "$TARGET" ]]; then
            if $FETCH_SYSROOT; then
                echo "=== Fetching sysroot from ${SSH_USER}@${TARGET} (--fetch-sysroot) ==="
            else
                echo "=== No sysroot at $DEFAULT_SYSROOT; fetching one from ${SSH_USER}@${TARGET} ==="
                echo "    (one-off; subsequent builds reuse it. Refresh with --fetch-sysroot)"
            fi
            "$SBC_SRC_DIR/scripts/fetch-pi-sysroot.sh" "${SSH_USER}@${TARGET}" "$DEFAULT_SYSROOT"
        fi
    fi

    if [[ -d "$DEFAULT_SYSROOT" ]]; then
        SYSROOT="$DEFAULT_SYSROOT"
    fi
}

build_sbc_interface() {
    echo "=== Building DuetSbcInterface (libduet_sbc.so) ==="

    # One of the presets in src/DuetSbcInterface/CMakePresets.json; they all put their build tree
    # in build/<preset-name>.
    local preset cmake_args=()
    if [[ "$(uname -m)" == "aarch64" ]]; then
        # Already on the target architecture (e.g. building on the Pi itself with --local): build
        # natively. That needs no toolchain and no sysroot, and gets glibc right by construction.
        echo "    Host is aarch64; building natively"
        preset=native
    else
        resolve_sysroot
        if [[ -n "$SYSROOT" ]]; then
            echo "    Linking against sysroot: $SYSROOT"
            preset=pi-arm64
            # The preset defaults to pi-sysroot/; --sysroot may point somewhere else.
            cmake_args+=("-DDUET_SBC_SYSROOT=$SYSROOT")
        else
            preset=arm64
            echo "WARNING: no Pi sysroot available, linking against the container's aarch64 libraries." >&2
            echo "         The resulting $SBC_LIB_NAME may fail to load on the Pi with a GLIBC version error." >&2
            echo "         Re-run with a deploy target (-t) to fetch one, or pass --sysroot <dir>." >&2
        fi
    fi

    local build_dir="$SBC_SRC_DIR/build/$preset"

    # --preset must be run from the project directory (that is where CMakePresets.json lives). Only
    # the shared library is built here; the harness and the tests are not part of a deployment.
    (cd "$SBC_SRC_DIR" \
        && cmake --preset "$preset" "${cmake_args[@]}" \
        && cmake --build --preset "$preset" --target duet_sbc_shared -j"$(nproc)")

    # Land it next to the managed assemblies so default P/Invoke probing resolves it
    cp "$build_dir/src/$SBC_LIB_NAME" "$BUILD_DIR/"
    echo "=== $SBC_LIB_NAME -> $BUILD_DIR/ ==="
}

# --- Build ---
if ! $SKIP_BUILD; then
    mkdir -p "$BUILD_DIR"
    for project in "${SELECTED[@]}"; do
        if [[ "$project" == "DuetSbcInterface" ]]; then
            build_sbc_interface
            continue
        fi
        echo "=== Building $project ==="
        dotnet build -r linux-arm64 --self-contained "$REPO_ROOT/${PROJECT_SRC[$project]}" -o "$BUILD_DIR"
    done
fi

if ! $DEPLOY; then
    echo "=== Build complete. No deployment target specified. ==="
    exit 0
fi

# --- Stop services ---
SERVICES="duetcontrolserver duetwebserver duetpluginservice duetpluginservice-root"
if $LOCAL; then
    echo "=== Stopping DSF services ==="
    sudo systemctl stop $SERVICES || true
else
    echo "=== Stopping DSF services on $SSH_USER@$TARGET ==="
    ssh "${SSH_USER}@${TARGET}" "systemctl stop $SERVICES || true"
fi

# --- Sync binaries ---
# Use --delete only when all projects are selected to avoid wiping unselected binaries
RSYNC_OPTS="-rav"
$ALL && RSYNC_OPTS="$RSYNC_OPTS --delete"

if $LOCAL; then
    echo "=== Syncing binaries to /opt/dsf/bin/ ==="
    sudo rsync $RSYNC_OPTS "$BUILD_DIR/" /opt/dsf/bin/
else
    echo "=== Syncing binaries to ${TARGET}:/opt/dsf/bin/ ==="
    rsync $RSYNC_OPTS "$BUILD_DIR/" "${SSH_USER}@${TARGET}:/opt/dsf/bin/"
fi

# --- Run postinst scripts for selected projects that have one (in parallel) ---
run_postinst() {
    local project="$1"
    local postinst="$REPO_ROOT/${PROJECT_POSTINST[$project]}"

    echo "=== Running $project postinst ==="
    if $LOCAL; then
        sudo sh "$postinst"
    else
        local postinst_name=$project-postinst
        rsync -av "$postinst" "${SSH_USER}@${TARGET}:/tmp/$postinst_name"
        ssh "${SSH_USER}@${TARGET}" "chmod +x /tmp/$postinst_name && /tmp/$postinst_name; rm -f /tmp/$postinst_name"
        echo "=== $project postinst completed on $TARGET ==="
    fi
}

declare -A POSTINST_PIDS=()
for project in "${SELECTED[@]}"; do
    [[ -z "${PROJECT_POSTINST[$project]+x}" ]] && continue
    run_postinst "$project" &
    POSTINST_PIDS[$project]=$!
done

postinst_failed=false
for project in "${!POSTINST_PIDS[@]}"; do
    if ! wait "${POSTINST_PIDS[$project]}"; then
        echo "Error: $project postinst failed" >&2
        postinst_failed=true
    fi
done
$postinst_failed && exit 1

# --- Start services if requested ---
if $START_SERVICES; then
    if $LOCAL; then
        echo "=== Starting DSF services ==="
        sudo systemctl start $SERVICES || true
    else
        echo "=== Starting DSF services on $SSH_USER@$TARGET ==="
        ssh "${SSH_USER}@${TARGET}" "systemctl start $SERVICES || true"
    fi
fi

echo "=== Deploy complete ==="
