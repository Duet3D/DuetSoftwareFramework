%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetruntime
Version: %{_tversion}
Release: %{_tag:%{_tag}-}%{_release}
Summary: DSF Common Runtime Components
Group:   3D Printing
Source0: duetruntime_%{_tversion}%{_tag:-%{_tag}}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2

AutoReq:  0

%global __os_install_post %{nil}

%description
DSF Common Runtime Components

%files
%defattr(-,dsf,dsf,-)
%{dsfoptdir}/bin/*
