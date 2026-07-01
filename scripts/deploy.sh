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
)

declare -A PROJECT_POSTINST=(
    [DuetControlServer]="pkg/deb/duetcontrolserver/DEBIAN/postinst"
    [DuetPiManagementPlugin]="pkg/deb/duetpimanagementplugin/DEBIAN/postinst"
    [DuetPluginService]="pkg/deb/duetpluginservice/DEBIAN/postinst"
    [DuetWebServer]="pkg/deb/duetwebserver/DEBIAN/postinst"
)

ALL_PROJECTS=(DuetControlServer DuetPiManagementPlugin DuetPluginService DuetWebServer CodeConsole CodeLogger CodeStream CustomHttpEndpoint ModelObserver PluginManager)

SSH_USER="root"
TARGET=""
SELECTED=()
ALL=false
LOCAL=false
SKIP_BUILD=false
START_SERVICES=false

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
  -h, --help           Show this help

Projects (specify one or more, or use --all):
$(printf '  %s\n' "${ALL_PROJECTS[@]}")

Examples:
  $(basename "$0") --all --target 192.168.4.27
  $(basename "$0") -t 192.168.4.27 DuetControlServer DuetWebServer
  $(basename "$0") --all --local
  $(basename "$0") --skip-build --target 192.168.4.27 DuetControlServer
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

if ! $LOCAL && [[ -z "$TARGET" ]]; then
    echo "Error: --target <ip> is required for remote deploy (or use --local)." >&2
    usage
    exit 1
fi

# Validate project names
for project in "${SELECTED[@]}"; do
    if [[ -z "${PROJECT_SRC[$project]+x}" ]]; then
        echo "Error: unknown project '$project'" >&2
        exit 1
    fi
done

# --- Build ---
if ! $SKIP_BUILD; then
    mkdir -p "$BUILD_DIR"
    for project in "${SELECTED[@]}"; do
        echo "=== Building $project ==="
        dotnet build -r linux-arm64 --self-contained "$REPO_ROOT/${PROJECT_SRC[$project]}" -o "$BUILD_DIR"
    done
fi

# --- Stop services ---
SERVICES="duetcontrolserver duetwebserver duetpluginservice duetpluginservice-root"
if $LOCAL; then
    echo "=== Stopping DSF services ==="
    sudo systemctl stop $SERVICES || true
else
    echo "=== Stopping DSF services on $TARGET ==="
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

# --- Run postinst scripts for selected projects that have one ---
for project in "${SELECTED[@]}"; do
    postinst="${PROJECT_POSTINST[$project]+}"
    [[ -z "${PROJECT_POSTINST[$project]+x}" ]] && continue
    postinst="$REPO_ROOT/${PROJECT_POSTINST[$project]}"

    echo "=== Running $project postinst ==="
    if $LOCAL; then
        sudo sh "$postinst"
    else
        postinst_name=$project-postinst
        rsync -av "$postinst" "${SSH_USER}@${TARGET}:/tmp/$postinst_name"
        ssh "${SSH_USER}@${TARGET}" "chmod +x /tmp/$postinst_name && /tmp/$postinst_name; rm -f /tmp/$postinst_name"
    fi
done

# --- Start services if requested ---
if $START_SERVICES; then
    if $LOCAL; then
        echo "=== Starting DSF services ==="
        sudo systemctl start $SERVICES || true
    else
        echo "=== Starting DSF services on $TARGET ==="
        ssh "${SSH_USER}@${TARGET}" "systemctl start $SERVICES || true"
    fi
fi

echo "=== Deploy complete ==="
