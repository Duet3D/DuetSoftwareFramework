#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
DEFAULT_BUILD_DIR="$REPO_ROOT/build/dotnet"
DEFAULT_AOT_BUILD_DIR="$REPO_ROOT/build/aot"

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
AOT=false
ARCH=linux-arm64
BUILD_TYPE=Debug
BUILD_DIR=
PUBLISH_ARGS=

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
      --sysroot <dir>  Sysroot to cross-link libduet_sbc.so against. Only needed to
                       target a glibc older than the container's 2.36; off by default
      --fetch-sysroot  Fetch a sysroot from the deploy target and build against it
      --aot            Build ahead of time binaries. Defaults to "false"
      --arch           Architecture to build for. Defaults to "$ARCH"
      --build-type     Defaults to "Debug". Also selects the CMake preset libduet_sbc.so
                       is built with, so Debug builds it unoptimised and steppable
  -p, --publish-args   msbuild properties
  -o, --dest-dir       Defaults to "$DEFAULT_BUILD_DIR unless --aot then "$DEFAULT_AOT_BUILD_DIR/<arch>/" 
  -h, --help           Show this help

Projects (specify one or more, or use --all):
$(printf '  %s\n' "${ALL_PROJECTS[@]}")

Notes:
  Projects publish to their own default output directory (bin/<config>/<tfm>/<rid>/publish) and the
  selected ones are then collated into the build directory. With --all the whole solution is
  published in one go (MSBuild parallelises it); otherwise the selected projects are published
  concurrently.

  DuetSbcInterface is the native SPI transfer loop (libduet_sbc.so) that DuetControlServer
  P/Invokes into. DCS cannot run without it, so selecting DuetControlServer builds it too.

  Because a shared library links glibc dynamically, the .so must not need a newer glibc than the
  target has. The devcontainer is Debian Bookworm and so targets the same glibc 2.36 as Raspberry
  Pi OS Bookworm, meaning a plain cross-build is deployable and no sysroot is involved. To target
  an older release, pass --sysroot <dir> or --fetch-sysroot to build against the Pi's own libraries.

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
        --aot)        AOT=true;       shift ;;
        --arch)       ARCH="$2";      shift 2 ;;
        --build-type) BUILD_TYPE="$2"; shift 2 ;;
        -o|--dest-dir) BUILD_DIR="$2"; shift 2 ;;
        -p|--publish-args)    PUBLISH_ARGS="$2"; shift 2 ;;
        -h|--help)    usage; exit 0           ;;
        -*)           echo "Unknown option: $1"; usage; exit 1 ;;
        All)          ALL=true; shift ;;
        *)            SELECTED+=("$1"); shift ;;
    esac
done

case $ARCH in
	linux-arm)   OBJCOPY_NAME=arm-linux-gnueabihf-objcopy ; TARGET_ARCH=armhf ;;
	linux-arm64) OBJCOPY_NAME=aarch64-linux-gnu-objcopy ; TARGET_ARCH=arm64 ;;
	linux-x64)   OBJCOPY_NAME=objcopy ; TARGET_ARCH=amd64 ;;
	*) echo "Unsupported arch: $ARCH" ; exit 1 ;;
esac
echo "Arch: $ARCH"

if [[ -z "$BUILD_DIR" ]]; then
    if $AOT; then
        BUILD_DIR="$DEFAULT_AOT_BUILD_DIR/$ARCH"
    else
        BUILD_DIR="$DEFAULT_BUILD_DIR"
    fi
fi
echo "Build directory: $BUILD_DIR"

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
# Cross-compiled here rather than on the Pi so a deploy needs no toolchain on the target. The
# devcontainer is Debian Bookworm, so its cross toolchain targets the same glibc 2.36 as Raspberry
# Pi OS Bookworm and a plain cross build produces a .so that loads on the Pi. A sysroot is only
# needed to target something older, and is opt-in via --sysroot / --fetch-sysroot.
resolve_sysroot() {
    # An explicit --sysroot always wins
    if [[ -n "$SYSROOT" ]]; then
        if [[ ! -d "$SYSROOT" ]]; then
            echo "Error: sysroot '$SYSROOT' does not exist" >&2
            exit 1
        fi
        return
    fi

    if $FETCH_SYSROOT; then
        if [[ -z "$TARGET" ]]; then
            echo "Error: --fetch-sysroot needs a deploy target (-t) to fetch from" >&2
            exit 1
        fi
        echo "=== Fetching sysroot from ${SSH_USER}@${TARGET} (--fetch-sysroot) ==="
        "$SBC_SRC_DIR/scripts/fetch-pi-sysroot.sh" "${SSH_USER}@${TARGET}" "$DEFAULT_SYSROOT"
        SYSROOT="$DEFAULT_SYSROOT"
    fi
}

build_sbc_interface() {
    echo "=== Building DuetSbcInterface (libduet_sbc.so) ==="

    # The .so is loaded by the managed binaries, so it has to be built for --arch as well. Each arch
    # maps onto a cross-compiling preset in src/DuetSbcInterface/CMakePresets.json, except when the
    # host already is that architecture, in which case there is nothing to cross-compile.
    local preset cmake_args=() cross_preset host_arch native=false
    host_arch="$(uname -m)"
    case "$ARCH" in
        linux-arm64) cross_preset=arm64 ; [[ "$host_arch" == "aarch64" ]] && native=true ;;
        linux-arm)   cross_preset=armhf ; [[ "$host_arch" == "armv7l" || "$host_arch" == "armv6l" ]] && native=true ;;
        # There is no x86_64 cross toolchain in the container, so linux-x64 is host-only
        linux-x64)   cross_preset="" ; [[ "$host_arch" == "x86_64" ]] && native=true ;;
    esac

    if $native; then
        # Already on the target architecture (e.g. building on the Pi itself with --local): build
        # natively. That needs no toolchain and no sysroot, and gets glibc right by construction.
        echo "    Host is $host_arch; building natively"
        preset=native
    elif [[ -z "$cross_preset" ]]; then
        echo "Error: cannot build $SBC_LIB_NAME for $ARCH on $host_arch; no cross toolchain" >&2
        exit 1
    else
        resolve_sysroot
        if [[ -n "$SYSROOT" ]]; then
            echo "    Linking against sysroot: $SYSROOT"
            preset="$cross_preset-sysroot"
            # The preset defaults to pi-sysroot/; --sysroot may point somewhere else.
            cmake_args+=("-DDUET_SBC_SYSROOT=$SYSROOT")
        else
            preset="$cross_preset"
            echo "    Cross-compiling against the container's $cross_preset libraries (glibc 2.36)"
        fi
    fi

    # An optimised .so is not steppable: the debugger loses locals and reorders lines. Every preset
    # has a -debug twin that differs only in CMAKE_BUILD_TYPE, so --build-type picks the same
    # configuration for the native library as it already does for the managed assemblies.
    if [[ "${BUILD_TYPE,,}" == "debug" ]]; then
        preset="$preset-debug"
        echo "    Build type is $BUILD_TYPE; using the unoptimised preset"
    fi

    local build_dir="$SBC_SRC_DIR/build/$preset"

    # --preset must be run from the project directory (that is where CMakePresets.json lives). Only
    # the shared library is built here; the harness and the tests are not part of a deployment.
    (cd "$SBC_SRC_DIR" \
        && cmake --preset "$preset" "${cmake_args[@]}" \
        && cmake --build --preset "$preset" --target duet_sbc_shared -j"$(nproc)")

    verify_sbc_arch "$build_dir/src/$SBC_LIB_NAME" "$build_dir"

    # Land it next to the managed assemblies so default P/Invoke probing resolves it
    cp "$build_dir/src/$SBC_LIB_NAME" "$BUILD_DIR/"
    echo "=== $SBC_LIB_NAME -> $BUILD_DIR/ ==="
}

# verify_sbc_arch <library> <build directory>
# Check that the library was built for the architecture that was asked for.
#
# A build directory first configured without a toolchain file keeps the host compiler forever after:
# the toolchain's set(CMAKE_CXX_COMPILER) is a plain set, which does not override a value already in
# the cache. Pointing a preset at a toolchain afterwards therefore changes nothing, the build still
# succeeds, and it quietly produces host binaries. Nothing notices until the target tries to load
# one, and the loader's message for a shared object of the wrong architecture is "cannot open shared
# object file: No such file or directory" - about a file that is plainly there.
verify_sbc_arch() {
    local lib="$1" build_dir="$2" expected actual
    case "$ARCH" in
        linux-arm64) expected="AArch64" ;;
        linux-arm)   expected="ARM" ;;
        linux-x64)   expected="X86-64" ;;
        *)           return 0 ;;
    esac

    actual="$(readelf -h "$lib" 2>/dev/null | sed -n 's/^ *Machine: *//p')"
    if [[ "$actual" != *"$expected"* ]]; then
        echo "Error: $SBC_LIB_NAME was built for '$actual', but $ARCH needs '$expected'." >&2
        echo "       The CMake cache in $build_dir predates the toolchain file, so it is still" >&2
        echo "       using the compiler it was first configured with. Build again after:" >&2
        echo "           rm -rf $build_dir" >&2
        exit 1
    fi
}

# --- Build ---
# Nothing is published straight into BUILD_DIR: every project publishes to its own default publish
# directory (bin/<config>/<tfm>/<rid>/publish) and collect_output() copies the selected ones into
# BUILD_DIR afterwards. That keeps concurrent publishes from writing to a shared output directory.
SOLUTION="$REPO_ROOT/src/DuetSoftwareFramework.sln"

if $AOT; then
    BUILD_LABEL="Native AOT"
else
    BUILD_LABEL="dotnet runtime"
fi

publish_args=()
if [[ -n "$PUBLISH_ARGS" ]]; then
    publish_args+=("-p:$PUBLISH_ARGS")
fi

# dotnet_publish <project-or-solution path>
dotnet_publish() {
    local target="$1"
    if $AOT; then
        dotnet publish "$target" -r "$ARCH" -c "$BUILD_TYPE" -p:AotPublish=true -p:ObjCopyName="$OBJCOPY_NAME" "${publish_args[@]}"
    else
        dotnet publish "$target" -r "$ARCH" -c "$BUILD_TYPE" --self-contained "${publish_args[@]}"
    fi
}

# The target framework is part of the publish path and is not known here, hence the glob
project_publish_dir() {
    local project="$1"
    local matches=("$REPO_ROOT/${PROJECT_SRC[$project]}"/bin/"$BUILD_TYPE"/*/"$ARCH"/publish)
    if [[ ${#matches[@]} -ne 1 || ! -d "${matches[0]}" ]]; then
        echo "Error: expected exactly one publish directory for $project, got: ${matches[*]}" >&2
        return 1
    fi
    echo "${matches[0]}"
}

# Merge the per-project publish directories into BUILD_DIR. The projects share most of their
# dependencies, so the copies overlap; later copies simply overwrite identical files.
collect_output() {
    echo "=== Collecting publish output into $BUILD_DIR ==="
    local project publish_dir
    for project in "${SELECTED[@]}"; do
        [[ "$project" == "DuetSbcInterface" ]] && continue
        publish_dir="$(project_publish_dir "$project")"
        cp -a "$publish_dir/." "$BUILD_DIR/"
        echo "    $project <- $publish_dir"
    done
}

if ! $SKIP_BUILD; then
    mkdir -p "$BUILD_DIR"

    if [[ " ${SELECTED[*]} " == *" DuetSbcInterface "* ]]; then
        build_sbc_interface
    fi

    DOTNET_PROJECTS=()
    for project in "${SELECTED[@]}"; do
        [[ "$project" == "DuetSbcInterface" ]] || DOTNET_PROJECTS+=("$project")
    done

    if $ALL; then
        # A single publish of the solution; MSBuild builds the projects in parallel itself. Only the
        # deployable projects publish: the libraries, tests, source generators and documentation are
        # all marked IsPublishable=false, so they are built as dependencies but produce no output
        # here.
        echo "=== Building solution ($BUILD_LABEL) ==="
        dotnet_publish "$SOLUTION"
    elif [[ ${#DOTNET_PROJECTS[@]} -eq 1 ]]; then
        echo "=== Building ${DOTNET_PROJECTS[0]} ($BUILD_LABEL) ==="
        dotnet_publish "$REPO_ROOT/${PROJECT_SRC[${DOTNET_PROJECTS[0]}]}"
    elif [[ ${#DOTNET_PROJECTS[@]} -gt 1 ]]; then
        # The projects share most of their project references (DuetAPI, DuetAPIClient,
        # DuetSharedLibrary). Building one of them first brings those up to date so the parallel
        # publishes below do not race each other building the same dependency.
        echo "=== Building shared dependencies ==="
        dotnet build "$REPO_ROOT/${PROJECT_SRC[${DOTNET_PROJECTS[0]}]}" -r "$ARCH" -c "$BUILD_TYPE" "${publish_args[@]}"

        # Each project publishes to its own output directory, so they can run concurrently. Their
        # logs would interleave, so each is captured and only printed if that publish fails.
        LOG_DIR="$(mktemp -d)"
        declare -A BUILD_PIDS=()
        for project in "${DOTNET_PROJECTS[@]}"; do
            echo "=== Building $project ($BUILD_LABEL) ==="
            dotnet_publish "$REPO_ROOT/${PROJECT_SRC[$project]}" > "$LOG_DIR/$project.log" 2>&1 &
            BUILD_PIDS[$project]=$!
        done

        build_failed=false
        for project in "${!BUILD_PIDS[@]}"; do
            if wait "${BUILD_PIDS[$project]}"; then
                echo "=== $project built ==="
            else
                echo "Error: $project build failed" >&2
                cat "$LOG_DIR/$project.log" >&2
                build_failed=true
            fi
        done
        rm -rf "$LOG_DIR"
        $build_failed && exit 1
    fi

    if [[ ${#DOTNET_PROJECTS[@]} -gt 0 ]]; then
        collect_output
    fi
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
RSYNC_OPTS="-rav --exclude='*.dbg' --exclude='*.pdb' --exclude='*.xml'"
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
