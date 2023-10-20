#!/usr/bin/env bash

set -e
# PACKAGER_SCRIPT and PACKAGER_DIR must be set before sourcing common.functions
PACKAGER_SCRIPT=$(readlink -f $0)
PACKAGER_DIR=$(dirname $PACKAGER_SCRIPT)

source $PACKAGER_DIR/../common/common.functions

[ $VALIDATED -ne 1 ] && exit 1

RPMBUILD_DIR=$DEST_DIR/rpmbuild
DEST_DIR=$RPMBUILD_DIR/SOURCES
mkdir -p $DEST_DIR

pkg_progs() {
	echo "- Packaging programs..."
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duetcontrolserver.spec
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duetwebserver.spec
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duetpluginservice.spec
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duettools.spec
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duetruntime.spec
}

pkg_plugins() {
	echo "- Plugins not supported on this platform."
}

pkg_sd() {
	echo "- Packaging virtual SD card package v$sdver..."
	rpmbuild --target=noarch --define="%_topdir $RPMBUILD_DIR" --define="%_arch noarch" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $sdver" -ba ${PACKAGER_DIR}/duetsd.spec
}

pkg_dwc() {
	echo "- Packaging DWC..."
	[ -z "$dwcver" ] && dwcver=$(jq -r ".version" $DEST_DIR/DuetWebControl/package.json)-2

	rpmbuild --target=noarch --define="%_topdir $RPMBUILD_DIR" --define="%_arch noarch" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion ${dwcver%%-*}"  --define="%_release ${dwcver##*-}" \
		-ba ${PACKAGER_DIR}/duetwebcontrol.spec
}

pkg_meta() {
	echo "- Packaging meta..."
	rpmbuild --target=${TARGET_ARCH}-linux-gnu --define="%_topdir $RPMBUILD_DIR" --define="%_arch $TARGET_ARCH" \
		--define="%_build_type ${BUILD_TYPE}" --define="%_tversion $dsfver" -ba ${PACKAGER_DIR}/duetsoftwareframework.spec
}

[ $BUILD_PROGS -eq 1 ] && { [ $BUILD -eq 1 ] && build_progs || : ; } && [ $PKGS -eq 1 ] && pkg_progs
[ $BUILD_PLUGINS -eq 1 ] && { [ $BUILD -eq 1 ] && build_plugins || : ; } && [ $PKGS -eq 1 ] && pkg_plugins
[ $BUILD_SD -eq 1 ] && { [ $BUILD -eq 1 ] && build_sd || : ; } && [ $PKGS -eq 1 ] && pkg_sd
[ $BUILD_DWC -eq 1 ] && { [ $BUILD -eq 1 ] && build_dwc || : ; } && [ $PKGS -eq 1 ] && pkg_dwc
[ $BUILD_META -eq 1 ] && { [ $BUILD -eq 1 ] && build_meta || : ; } && [ $PKGS -eq 1 ] && pkg_meta

if [ $PKGS -eq 1 ] ; then
	if [ -n "$SIGNING_KEY" ] ; then
		echo "Signing: "
		rpmsign --addsign --key-id=$SIGNING_KEY $RPMBUILD_DIR/RPMS/$TARGET_ARCH/* $RPMBUILD_DIR/RPMS/noarch/*
	fi
	echo "Completed package creation"
	du -sch --time $RPMBUILD_DIR/RPMS/$TARGET_ARCH/*
	du -sch --time $RPMBUILD_DIR/RPMS/noarch/*
else
	echo "Completed builds"
	echo "Built $DEST_DIR"
	du -sch --time $DEST_DIR/*
fi
#[ $CLEANUP -eq 1 ] && cleanup_tmp
