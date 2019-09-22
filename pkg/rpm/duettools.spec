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
Release: 900
Summary: DSF Tools
Group:   3D Printing
Source0: duettools_%{_tversion}
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetruntime

AutoReq:  0

%description
DSF Tools

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

%files
%defattr(-,root,root,-)
%{dsfoptdir}/bin/CodeLogger*
%{dsfoptdir}/bin/CodeConsole*
