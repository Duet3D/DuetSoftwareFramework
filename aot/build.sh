#!/usr/bin/env bash
#
# Cross-builds DSF as NativeAOT binaries in a container that carries the required toolchain.
#
# NativeAOT links a real ELF executable, so the build host needs cross binutils for the target and a
# sysroot whose glibc is no newer than the target's. The container provides both, see Containerfile
#
set -e

AOT_DIR=$(readlink -f $(dirname $0))
TOP_DIR=$(readlink -f $AOT_DIR/..)
IMAGE=dsf-aot

ARCH=linux-arm
BUILD_TYPE=Debug
CONTAINER_RUNTIME=docker
DEST_DIR=
declare -i DEB=0
declare -i HELP=0
declare -a PROJECTS
declare -a PUBLISH_ARGS
declare -a PKG_ARGS

for a in "$@" ; do
	case "$a" in
		--arch=*)         ARCH=${a#*=} ;;
		--build-type=*)   BUILD_TYPE=${a#*=} ;;
		--dest-dir=*)     DEST_DIR=${a#*=} ;;
		--runtime=*)      CONTAINER_RUNTIME=${a#*=} ;;
		--deb)            DEB=1 ;;
		--help)           HELP=1 ;;
		--packages=*|--signing-key=*|--no-pkgs|--no-build|--no-cleanup) PKG_ARGS+=("$a") ;;
		-p:*|/p:*)        PUBLISH_ARGS+=("$a") ;;
		-*)               echo "Unknown option: $a" ; exit 1 ;;
		*)                PROJECTS+=("$a") ;;
	esac
done

print_help() {
cat <<EOF
Usage: $0 [ --arch=< linux-arm | linux-arm64 | linux-x64 > ]
	[ --build-type=< Debug | Release > ]
	[ --dest-dir=< destination directory > ]
	[ --runtime=< docker | podman > ]
	[ --deb ]
	[ -p:<msbuild property> ... ]
	[ <project> ... ]

Publishes the given projects as NativeAOT binaries, one subdirectory per project.
If no projects are given, every AOT-capable project is built.

arch:        Target runtime identifier.  Defaults to "linux-arm".
build-type:  Defaults to "Debug".
dest-dir:    Defaults to "$AOT_DIR/out/<arch>", or to the packager's default with --deb.
runtime:     Container runtime to use.  Defaults to "docker".
deb:         Builds Debian packages via pkg/build.sh --aot instead of bare binaries.
             --packages, --signing-key, --no-pkgs, --no-build and --no-cleanup are passed on;
             DWC is left out because it needs npm and is not architecture-specific anyway.

EOF
exit 0
}

[ $HELP -eq 1 ] && print_help

case $ARCH in
	linux-arm)   OBJCOPY_NAME=arm-linux-gnueabihf-objcopy ; TARGET_ARCH=armhf ;;
	linux-arm64) OBJCOPY_NAME=aarch64-linux-gnu-objcopy ; TARGET_ARCH=arm64 ;;
	linux-x64)   OBJCOPY_NAME=objcopy ; TARGET_ARCH=amd64 ;;
	*) echo "Unsupported arch: $ARCH" ; exit 1 ;;
esac

[ ${#PROJECTS[@]} -eq 0 ] && PROJECTS=(DuetControlServer DuetWebServer DuetPluginService DuetPiManagementPlugin CodeConsole CodeLogger CodeStream CustomHttpEndpoint ModelObserver PluginManager)
[ $DEB -eq 1 ] && [ ${#PKG_ARGS[@]} -eq 0 ] && PKG_ARGS=(--packages=progs,plugins)
[ -z "$DEST_DIR" ] && [ $DEB -eq 0 ] && DEST_DIR=$AOT_DIR/out/$ARCH
[ -z "$DEST_DIR" ] && DEST_DIR=/tmp/duet/deb/$BUILD_TYPE/$TARGET_ARCH

# Only podman needs the SELinux relabel suffix on bind mounts
MOUNT_OPT=
[ "$CONTAINER_RUNTIME" == podman ] && MOUNT_OPT=:Z

$CONTAINER_RUNTIME build -t $IMAGE -f $AOT_DIR/Containerfile $AOT_DIR

mkdir -p $DEST_DIR

# The repository is mounted read-only and copied inside the container without obj/bin. Reusing the
# host's intermediates would pin package versions from the host SDK, which differs from the one here
$CONTAINER_RUNTIME run --rm \
	-v $TOP_DIR:/src:ro$MOUNT_OPT \
	-v $DEST_DIR:/out$MOUNT_OPT \
	--user "$(id -u):$(id -g)" \
	-e ARCH="$ARCH" \
	-e BUILD_TYPE="$BUILD_TYPE" \
	-e TARGET_ARCH="$TARGET_ARCH" \
	-e OBJCOPY_NAME="$OBJCOPY_NAME" \
	-e DEB="$DEB" \
	-e PROJECTS="${PROJECTS[*]}" \
	-e PUBLISH_ARGS="${PUBLISH_ARGS[*]}" \
	-e PKG_ARGS="${PKG_ARGS[*]}" \
	$IMAGE \
	bash -c 'set -e
		mkdir -p /tmp/work
		cp -a /src/. /tmp/work/
		find /tmp/work -type d \( -name obj -o -name bin \) -prune -exec rm -rf {} +
		cd /tmp/work
		if [ "$DEB" == "1" ] ; then
			pkg/build.sh --aot --target-arch=$TARGET_ARCH --build-type=$BUILD_TYPE --dest-dir=/out $PKG_ARGS deb
		else
			# No SysRoot: Debian installs the cross libc where the cross linker already looks, and its
			# libc.so linker script holds absolute paths that --sysroot would prefix a second time
			for project in $PROJECTS ; do
				dotnet publish src/$project/$project.csproj -r $ARCH -c $BUILD_TYPE \
					-p:AotPublish=true -p:ObjCopyName=$OBJCOPY_NAME $PUBLISH_ARGS -o /out/$project
			done
		fi'

echo
echo "Built $DEST_DIR"
du -sch --time $DEST_DIR/*
