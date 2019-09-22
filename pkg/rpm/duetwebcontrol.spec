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
Release: %{_release}.900
Summary: Official web interface for Duet electronics
Group:   3D Printing
Source0: duetwebcontrol_%{_tversion}-%{_release}
License: GPLv3
URL:     https://github.com/chrishamm/DuetWebControl
BuildRequires: rpm >= 4.7.2-2
Requires: duetruntime

AutoReq:  0

%description
Official web interface for Duet electronics

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

%files
%defattr(-,root,root,-)
%{dsfoptdir}/dwc2
%{dsfoptdir}/sd/www
