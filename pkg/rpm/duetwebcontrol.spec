%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetwebcontrol
Version: %{_tversion}
Release: %{_release}
Summary: Official web interface for Duet electronics
Group:   3D Printing
Source0: duetwebcontrol_%{_tversion}
License: GPLv3
URL:     https://github.com/Duet3D/DuetWebControl
BuildRequires: rpm >= 4.7.2-2

AutoReq:  0

%description
Official web interface for Duet electronics

%files
%defattr(-,root,root,-)
%{dsfoptdir}/dwc
%{dsfoptdir}/sd/www
