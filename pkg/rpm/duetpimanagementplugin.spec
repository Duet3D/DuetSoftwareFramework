%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0

%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetpimanagementplugin
Version: %{_tversion}
Release: %{_release}
Summary: DSF Pi Management Plugin
Group:   3D Printing
Source0: duetpimanagementplugin_%{_tversion}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetcontrolserver = %{_tversion}
Requires: duetpluginservice = %{_tversion}
%systemd_requires

AutoReq:  0

%description
DSF Pi Management Plugin

%post
systemctl restart duetcontrolserver duetpluginservice >/dev/null 2>&1 || :

%files
%defattr(-,dsf,dsf,-)
%{dsfoptdir}/bin/DuetPiManagementPlugin
%{dsfoptdir}/bin/DuetPiManagementPlugin.*
%{dsfoptdir}/plugins/DuetPiManagementPlugin.json
%config(noreplace) %{dsfoptdir}/plugins/DuetPiManagementPlugin/dsf/*
%dir %{dsfoptdir}/plugins/DuetPiManagementPlugin
%dir %{dsfoptdir}/plugins/DuetPiManagementPlugin/dsf
