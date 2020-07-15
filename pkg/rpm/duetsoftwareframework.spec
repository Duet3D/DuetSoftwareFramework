%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global _libdir /usr/lib

Name:    duetsoftwareframework
Version: %{_tversion}
Release: %{_release}
Summary: Duet Software Framework
Group:   3D Printing
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
Source0: duetsoftwareframework_%{_tversion}

BuildRequires: rpm >= 4.7.2-2

AutoReq:  0
Requires: duetcontrolserver
Requires: duetwebserver
Requires: duetsd
Requires: duetwebcontrol

%description
Duet Software Framework

%files
