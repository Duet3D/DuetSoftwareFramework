#!/usr/bin/env bash

set -e
# PACKAGER_SCRIPT and PACKAGER_DIR must be set before sourcing common.functions
PACKAGER_SCRIPT=$(readlink -f ${0})
PACKAGER_DIR=$(dirname ${PACKAGER_SCRIPT})

source ${PACKAGER_DIR}/../common/common.functions
CLEANUP=0

[ ${VALIDATED} -ne 1 ] && exit 1

mkdir -p ${DEST_DIR}

pkg_progs() { : ; }
pkg_sd() { : ; }
pkg_dwc() { : ; }
pkg_meta() { : ; }

[ $BUILD_PROGS -eq 1 ] && { [ $BUILD -eq 1 ] && build_progs || : ; } && [ $PKGS -eq 1 ] && pkg_progs
[ $BUILD_SD    -eq 1 ] && { [ $BUILD -eq 1 ] && build_sd    || : ; } && [ $PKGS -eq 1 ] && pkg_sd
[ $BUILD_DWC   -eq 1 ] && { [ $BUILD -eq 1 ] && build_dwc   || : ; } && [ $PKGS -eq 1 ] && pkg_dwc
[ $BUILD_META  -eq 1 ] && { [ $BUILD -eq 1 ] && build_meta  || : ; } && [ $PKGS -eq 1 ] && pkg_meta

# For makepkg this script must not fail but the above will return exit != 0
# if not all packages are built at the same time due to set -e and test statements failing
exit 0
