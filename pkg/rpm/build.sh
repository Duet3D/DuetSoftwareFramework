#!/bin/bash

set -e
# PACKAGER_SCRIPT and PACKAGER_DIR must be set before sourcing common.functions
PACKAGER_SCRIPT=$(readlink -f $0)
PACKAGER_DIR=$(dirname $PACKAGER_SCRIPT)

source $PACKAGER_DIR/../common/common.functions

[ $VALIDATED -ne 1 ] && exit 1

mkdir -p $DEST_DIR

pkg_progs() {
	echo "- Packaging programs..."
}

pkg_sd() {
	echo "- Packaging virtual SD card package v$sdver..."
}

pkg_dwc() {
	echo "- Packaging DWC2..."
}


pkg_meta() {
	echo "- Packaging meta..."
}

[ $BUILD_PROGS -eq 1 ] && build_progs && [ $PKGS -eq 1 ] && pkg_progs
[ $BUILD_SD -eq 1 ] && build_sd && [ $PKGS -eq 1 ] && pkg_sd
[ $BUILD_DWC -eq 1 ] && build_dwc && [ $PKGS -eq 1 ] && pkg_dwc
[ $BUILD_META -eq 1 ] && build_meta && [ $PKGS -eq 1 ] && pkg_meta

if [ $PKGS -eq 1 ] ; then
else
	echo "Completing package creation"
	echo "Built $DEST_DIR"
	du -sch --time $DEST_DIR/*
fi
[ $CLEANUP -eq 1 ] && cleanup_tmp
