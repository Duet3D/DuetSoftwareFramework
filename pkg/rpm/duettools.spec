%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duettools
Version: %{_tversion}
Release: %{_tag:%{_tag}-}%{_release}
Summary: DSF Tools
Group:   3D Printing
Source0: duettools_%{_tversion}%{_tag:-%{_tag}}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetcontrolserver = %{_tversion}

AutoReq:  0

%description
DSF Tools

%files
%defattr(0755,dsf, dsf,-)
%{dsfoptdir}/bin/CodeLogger
%{dsfoptdir}/bin/CodeConsole
%{dsfoptdir}/bin/CustomHttpEndpoint
%{dsfoptdir}/bin/ModelObserver
%{dsfoptdir}/bin/PluginManager
%{dsfoptdir}/bin/CodeStream

%defattr(0664,dsf, dsf,-)
%{dsfoptdir}/bin/CodeLogger.*
%{dsfoptdir}/bin/CodeConsole.*
%{dsfoptdir}/bin/CustomHttpEndpoint.*
%{dsfoptdir}/bin/ModelObserver.*
%{dsfoptdir}/bin/PluginManager.*
%{dsfoptdir}/bin/CodeStream.*
