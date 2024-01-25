#!/bin/bash

# Need to have a version number in order to install a specific DSF version...
if [[ -z "$1" ]]; then
	echo "Missing version"
	exit 1
fi
VERSION=`echo $1 | sed 's/-/~/g'`

# Determine the corresponding RRF package version. There may be multiple RRF packages per DSF version
RRF_VERSION=`apt-cache show reprapfirmware | grep "Version:" | cut -d ' ' -f 2 | grep "$VERSION" | head -n 1`
if [[ -z "$RRF_VERSION" ]]; then
	echo "Could not find RepRapFirmware package for version $1. Invalid version or wrong package feed?"
	exit 1
fi

# Install given version
echo "Installing DSF $VERSION and RRF $RRF_VERSION"
apt-get install -y --allow-downgrades -qq -o Dpkg::Options::="--force-confold" duetsoftwareframework=$VERSION duetcontrolserver=$VERSION duetwebserver=$VERSION duetpluginservice=$VERSION duettools=$VERSION duetruntime=$VERSION duetwebcontrol=$VERSION duetpimanagementplugin=$VERSION reprapfirmware=$RRF_VERSION < /dev/null
echo "Done!"

