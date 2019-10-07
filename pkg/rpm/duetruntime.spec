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
Release: 901
Summary: DSF Common Runtime Components
Group:   3D Printing
Source0: duetruntime_%{_tversion}
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2

AutoReq:  0

%description
DSF Common Runtime Components

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

%files
%defattr(0644,root,root,-)
%{dsfoptdir}/bin/*
%attr(0755,root,root) %{dsfoptdir}/bin/createdump
