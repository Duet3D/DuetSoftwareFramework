#!/usr/bin/env bash

set -e
# PACKAGER_SCRIPT and PACKAGER_DIR must be set before sourcing common.functions
PACKAGER_SCRIPT=$(readlink -f $0)
PACKAGER_DIR=$(dirname $PACKAGER_SCRIPT)

source $PACKAGER_DIR/../common/common.functions

[ $VALIDATED -ne 1 ] && exit 1

mkdir -p $DEST_DIR

pkg_progs() {
	echo "- Packaging programs..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetcontrolserver_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetcontrolserver_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetcontrolserver_$dcsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetcontrolserver_$dcsver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetwebserver_$dwsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetwebserver_$dwsver/DEBIAN/control
	sed -i "s/DWSVER/$dwsver/g" $DEST_DIR/duetwebserver_$dwsver/DEBIAN/control
	sed -i "s/DWSVER/$dwsver/g" $DEST_DIR/duetwebserver_$dwsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetwebserver_$dwsver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetpluginservice_$dpsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetpluginservice_$dpsver/DEBIAN/control
	sed -i "s/DPSVER/$dpsver/g" $DEST_DIR/duetpluginservice_$dpsver/DEBIAN/control
	sed -i "s/DPSVER/$dpsver/g" $DEST_DIR/duetpluginservice_$dpsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetpluginservice_$dpsver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duettools_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duettools_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duettools_$dcsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duettools_$dcsver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetruntime_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetruntime_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetruntime_$dcsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetruntime_$dcsver $DEST_DIR
}

pkg_plugins() {
	echo "- Packaging plugins..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetpimanagementplugin_$dmpver/DEBIAN/control
	sed -i "s/DMPVER/$dmpver/g" $DEST_DIR/duetpimanagementplugin_$dmpver/DEBIAN/control
	sed -i "s/DPSVER/$dpsver/g" $DEST_DIR/duetpimanagementplugin_$dmpver/DEBIAN/control
	sed -i "s/DMPVER/$dmpver/g" $DEST_DIR/duetpimanagementplugin_$dmpver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetpimanagementplugin_$dmpver $DEST_DIR
}

pkg_sd() {
	echo "- Packaging virtual SD card package v$sdver..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetsd_$sdver/DEBIAN/control
	sed -i "s/SDVER/$sdver/g" $DEST_DIR/duetsd_$sdver/DEBIAN/control
	dpkg-deb --build $DEST_DIR/duetsd_$sdver $DEST_DIR
}

pkg_dwc() {
	echo "- Packaging DWC..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetwebcontrol_$dwcver/DEBIAN/control
	sed -i "s/DWCVER/$dwcver/g" $DEST_DIR/duetwebcontrol_$dwcver/DEBIAN/control
	dpkg-deb --build $DEST_DIR/duetwebcontrol_$dwcver $DEST_DIR
}

pkg_meta() {
	echo "- Packaging meta..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DWSVER/$dwsver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DPSVER/$dpsver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/SDVER/$sdver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DWCVER/$dwcver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" $DEST_DIR/duetsoftwareframework_$dcsver/DEBIAN/changelog
	dpkg-deb --build $DEST_DIR/duetsoftwareframework_$dcsver $DEST_DIR/
}

[ $BUILD_PROGS -eq 1 ] && { [ $BUILD -eq 1 ] && build_progs || : ; } && [ $PKGS -eq 1 ] && pkg_progs
[ $BUILD_PLUGINS -eq 1 ] && { [ $BUILD -eq 1 ] && build_plugins || : ; } && [ $PKGS -eq 1 ] && pkg_plugins
[ $BUILD_SD -eq 1 ] && { [ $BUILD -eq 1 ] && build_sd || : ; } && [ $PKGS -eq 1 ] && pkg_sd
[ $BUILD_DWC -eq 1 ] && { [ $BUILD -eq 1 ] && build_dwc || : ; } && [ $PKGS -eq 1 ] && pkg_dwc
[ $BUILD_META -eq 1 ] && { [ $BUILD -eq 1 ] && build_meta || : ; } && [ $PKGS -eq 1 ] && pkg_meta

if [ $PKGS -eq 1 ] ; then
	echo "Completing package creation"
	if [ -n "$SIGNING_KEY"  ] ; then
		if which dpkg-sig >/dev/null 2>&1 ; then
			dpkg-sig -k $SIGNING_KEY -s builder $DEST_DIR/*.deb
		else
			echo "dpkg-sig isn't installed.  Skipping signing."
		fi
	fi

	mkdir -p $DEST_DIR/binary-$TARGET_ARCH
	mv $DEST_DIR/*.deb $DEST_DIR/binary-$TARGET_ARCH
	echo
	echo "Built $DEST_DIR/binary-$TARGET_ARCH"
	du -sch --time $DEST_DIR/binary-$TARGET_ARCH/*
else
	echo
	echo "Built $DEST_DIR"
	du -sch --time $DEST_DIR/*
fi
[ $CLEANUP -eq 1 ] && cleanup_tmp
