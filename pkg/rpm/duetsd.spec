%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetsd
Version: %{_tversion}
Release: %{_tag:%{_tag}-}%{_release}
Summary: DSF SD Card
Group:   3D Printing
Source0: duetsd_%{_tversion}%{_tag:-%{_tag}}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2

AutoReq:  0

%description
DSF SD Card

%files
%defattr(0664,root,root,0775)
%dir %{dsfoptdir}/sd/filaments
%dir %{dsfoptdir}/sd/firmware
%dir %{dsfoptdir}/sd/gcodes
%dir %{dsfoptdir}/sd/macros
%dir %{dsfoptdir}/sd/sys
%config(noreplace) %{dsfoptdir}/sd/sys/config.g
