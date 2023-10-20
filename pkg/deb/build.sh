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

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetcontrolserver_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetcontrolserver_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetcontrolserver_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duetcontrolserver_$dsfver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetwebserver_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetwebserver_$dsfver/DEBIAN/control
	dpkg-deb --build -Zxz $DEST_DIR/duetwebserver_$dsfver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetpluginservice_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetpluginservice_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetpluginservice_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duetpluginservice_$dsfver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duettools_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duettools_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duettools_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duettools_$dsfver $DEST_DIR

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetruntime_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetruntime_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetruntime_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duetruntime_$dsfver $DEST_DIR
}

pkg_plugins() {
	echo "- Packaging plugins..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetpimanagementplugin_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetpimanagementplugin_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetpimanagementplugin_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duetpimanagementplugin_$dsfver $DEST_DIR
}

pkg_sd() {
	echo "- Packaging virtual SD card package v$sdver..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetsd_$sdver/DEBIAN/control
	sed -i "s/SDVER/$sdver/g" $DEST_DIR/duetsd_$sdver/DEBIAN/control
	dpkg-deb --build -Zxz $DEST_DIR/duetsd_$sdver $DEST_DIR
}

pkg_dwc() {
	echo "- Packaging DWC..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetwebcontrol_$dwcver/DEBIAN/control
	sed -i "s/DWCVER/$(echo $dwcver | sed -e 's/-/~/g')/g" $DEST_DIR/duetwebcontrol_$dwcver/DEBIAN/control
	dpkg-deb --build -Zxz $DEST_DIR/duetwebcontrol_$dwcver $DEST_DIR
}

pkg_meta() {
	echo "- Packaging meta..."

	sed -i "s/TARGET_ARCH/$TARGET_ARCH/g" $DEST_DIR/duetsoftwareframework_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetsoftwareframework_$dsfver/DEBIAN/control
	sed -i "s/SDVER/$(echo $sdver | sed -e 's/-/~/g')/g" $DEST_DIR/duetsoftwareframework_$dsfver/DEBIAN/control
	sed -i "s/DWCVER/$(echo $dwcver | sed -e 's/-/~/g')/g" $DEST_DIR/duetsoftwareframework_$dsfver/DEBIAN/control
	sed -i "s/DSFVER/$(echo $dsfver | sed -e 's/-/~/g')/g" $DEST_DIR/duetsoftwareframework_$dsfver/DEBIAN/changelog
	dpkg-deb --build -Zxz $DEST_DIR/duetsoftwareframework_$dsfver $DEST_DIR/
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
