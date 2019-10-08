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
Release: 901
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
%defattr(0664,root,root,-)
%attr(0755, root, root) %{dsfoptdir}/bin/CodeLogger
%{dsfoptdir}/bin/CodeLogger.*
%attr(0755, root, root) %{dsfoptdir}/bin/CodeConsole
%{dsfoptdir}/bin/CodeConsole.*
%attr(0755, root, root) %{dsfoptdir}/bin/ModelObserver
%{dsfoptdir}/bin/ModelObserver.*
