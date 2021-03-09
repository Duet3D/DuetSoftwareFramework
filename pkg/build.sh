#!/usr/bin/env bash

set -e

PKG_DIR=$(readlink -f $(dirname $0))
COMMON_DIR=$PKG_DIR/common
PACKAGE_TYPES="deb"
HELP=0

source $COMMON_DIR/parse_args

# Check for required binaries. If missing, bail.
REQUIRED_BINARIES=(xmllint jq npm dotnet)
MISSING_BINS=false
for BIN in ${REQUIRED_BINARIES[@]}; do
	if ! (which $BIN &>/dev/null); then
		echo "Missing required binary: ${BIN}"
		MISSING_BINS=true
	fi
done
if $MISSING_BINS; then
	echo "Please install the above binaries before continuing"
	exit 1
fi

print_help_top_level() {
cat <<EOF
Usage: $0 [ --target-arch=< i386 | i686 | x86_64 | armhf | armhfp | aarch64 > ]
	[ --build-type=< Debug | Release > ]
	[ --dest-dir=< destination directory > ]
	[ --signing-key=< key id or path to keyfile > ]
	[ --no-pkgs ]
	[ --no-build ]
	[ --no-cleanup ]
	[ --print-debug ]
	[ --help ]
	[ --packages=[<package>, ... ]
	[ <package-type> ... ]

Runs the build script in the <package-type> directory.
If more than 1 package-type is specified, they will be run in succession.
If none are specified, all will be run.

target-arch:   Defaults to "armhf" for deb packages and "armhfp" for rpm packages.
build-type:    Defaults to "Debug".
dest-dir:      Defaults to "/tmp/duet/<deb|rpm>/<build-type>/<target-arch>".
signing-key:   A key id or a path preceeded by an '@' to a file containing a
               key id.
               No default.  If not provided, the packages won't be signed.
no-pkgs:       Builds but doesn't package the results.
no-build:      Packages existing builds.
no-cleanup:    Prevents the work subdirectories in <dest-dir> from being cleaned up.
               Automatically set if no-pkgs was specified.
print-debug:   Prints the setup variables and exits.
packages:      One or more of progs, plugins, sd, dwc, meta separated by commas.
               If none are specified, all will be built.

EOF
exit 0
}

[ $HELP -eq 1 ] && print_help_top_level

[ ${#POSITIONAL_ARGS[@]} -gt 0 ] && PACKAGE_TYPES=${POSITIONAL_ARGS[@]}

for pt in $PACKAGE_TYPES ; do
	build_script=$PKG_DIR/$pt/build.sh
	[ ! -x $build_script ] && { echo "No build script for package type $pt.  Skipping." ; continue ; }
	$build_script "$@"
done
