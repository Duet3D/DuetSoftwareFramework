#!/bin/bash

set -e
pwd=$(pwd)
rm -rf /tmp/duet
mkdir /tmp/duet

dcsver=$(xmllint --xpath "string(//Project/PropertyGroup/AssemblyVersion)" ../src/DuetControlServer/DuetControlServer.csproj)
dwsver=$(xmllint --xpath "string(//Project/PropertyGroup/AssemblyVersion)" ../src/DuetWebServer/DuetWebServer.csproj)
sdver=$(cat $pwd/duetsd/DEBIAN/control | grep Version | cut -d ' ' -f 2)

build() {
	echo "Building $1 configuration (DCS $dcsver and DWS $dwsver),,,"

	# TODO: Replace this with actual dotnet publish commands once .NET Core SDK 2.2.300 is available on Arch Linux x64
	rm -rf /tmp/duet/files
	mkdir /tmp/duet/files
	cp -r /mnt/Christian/Source/repos/DuetSoftwareFramework/bin/$1/* /tmp/duet/files
	chmod 644 /tmp/duet/files/*
	chmod +x /tmp/duet/files/DuetControlServer
	chmod +x /tmp/duet/files/DuetWebServer

	echo "- Arranging files..."
	mkdir -p /tmp/duet/duetcontrolserver_$dcsver/opt/dsf/bin
	cp -r $pwd/duetcontrolserver/* /tmp/duet/duetcontrolserver_$dcsver
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetcontrolserver_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetcontrolserver_$dcsver/DEBIAN/changelog
	mv /tmp/duet/files/DuetControlServer* /tmp/duet/duetcontrolserver_$dcsver/opt/dsf/bin

	mkdir -p /tmp/duet/duetwebserver_$dwsver/opt/dsf/bin
	cp -r $pwd/duetwebserver/* /tmp/duet/duetwebserver_$dwsver
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetwebserver_$dwsver/DEBIAN/control
	sed -i "s/DWSVER/$dwsver/g" /tmp/duet/duetwebserver_$dwsver/DEBIAN/control
	sed -i "s/DWSVER/$dwsver/g" /tmp/duet/duetwebserver_$dwsver/DEBIAN/changelog
	mv /tmp/duet/files/DuetWebServer* /tmp/duet/duetwebserver_$dwsver/opt/dsf/bin
	mv /tmp/duet/files/appsettings.* /tmp/duet/duetwebserver_$dwsver/opt/dsf/bin
	mv /tmp/duet/files/web.config /tmp/duet/duetwebserver_$dwsver/opt/dsf/bin

	mkdir -p /tmp/duet/duetruntime_$dcsver/opt/dsf/bin
	cp -r $pwd/duetruntime/* /tmp/duet/duetruntime_$dcsver
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetruntime_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetruntime_$dcsver/DEBIAN/changelog
	mv /tmp/duet/files/* /tmp/duet/duetruntime_$dcsver/opt/dsf/bin
	rmdir /tmp/duet/files

	echo "- Building packages..."
	cd /tmp/duet
	dpkg-deb --build duetcontrolserver_$dcsver
	dpkg-deb --build duetwebserver_$dwsver
	dpkg-deb --build duetruntime_$dcsver
}

build_sd() {
	echo "Building virtual SD card package v$sdver..."
	cp -r $pwd/duetsd /tmp/duet/duetsd_$sdver
	sed -i "s/SDVER/$sdver/g" /tmp/duet/duetsd_$sdver/DEBIAN/control
	dpkg-deb --build duetsd_$sdver
}

build_dwc() {
	echo "Building DWC2..."

	echo "- Cloning repository..."
	git clone -q --single-branch --branch next https://github.com/chrishamm/DuetWebControl.git /tmp/duet/DuetWebControl
	dwcver=$(jq -r ".version" /tmp/duet/DuetWebControl/package.json)
	cd /tmp/duet/DuetWebControl

	echo "- Installing dependencies..."
	npm install > /dev/null

	echo "- Building web interface..."
	npm run build > /dev/null

	echo "- Arranging files..."
	mkdir -p /tmp/duet/duetwebcontrol_$dwcver/opt/dsf/dwc2
	cp -r $pwd/duetwebcontrol/* /tmp/duet/duetwebcontrol_$dwcver
	sed -i "s/DWCVER/$dwcver/g" /tmp/duet/duetwebcontrol_$dwcver/DEBIAN/control
	unzip -q /tmp/duet/DuetWebControl/dist/DuetWebControl-Duet3.zip -d /tmp/duet/duetwebcontrol_$dwcver/opt/dsf/dwc2
	rm -rf /tmp/duet/DuetWebControl

	echo "- Building package..."
	cd /tmp/duet
	dpkg-deb --build duetwebcontrol_$dwcver
}

build_meta() {
	echo "Building meta package..."

	cp -r $pwd/duetsoftwareframework /tmp/duet/duetsoftwareframework_$dcsver
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetsoftwareframework_$dcsver/DEBIAN/control
	sed -i "s/DCSVER/$dcsver/g" /tmp/duet/duetsoftwareframework_$dcsver/DEBIAN/changelog
	dpkg-deb --build duetsoftwareframework_$dcsver
}

#build "Release"
build "Debug"
build_sd
build_dwc
build_meta

mkdir -p ./dists/stretch/dsf/binary-armhf
mv *.deb ./dists/stretch/dsf/binary-armhf
dpkg-scanpackages dists/stretch/dsf/binary-armhf /dev/null > ./dists/stretch/dsf/binary-armhf/Packages
dpkg-scanpackages dists/stretch/dsf/binary-armhf /dev/null | gzip -9c > ./dists/stretch/dsf/binary-armhf/Packages.gz
