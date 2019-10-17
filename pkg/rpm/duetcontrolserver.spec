%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0

%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetcontrolserver
Version: %{_tversion}
Release: 900
Summary: DSF Control Server
Group:   3D Printing
Source0: duetcontrolserver_%{_tversion}
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetruntime

AutoReq:  0

%description
DSF Control Server

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

%pre
if (systemctl -q is-active duetcontrolserver.service); then
    systemctl -q stop duetcontrolserver.service >/dev/null 2>&1 || :
fi

%post
/bin/systemctl daemon-reload >/dev/null 2>&1 || :
/bin/systemctl enable duetcontrolserver.service >/dev/null 2>&1 || :

%preun
if [ "$1" -eq "0" ]; then
	/bin/systemctl --no-reload disable duetcontrolserver.service > /dev/null 2>&1 || :
	/bin/systemctl stop duetcontrolserver.service > /dev/null 2>&1 || :
fi

%postun
/bin/systemctl daemon-reload >/dev/null 2>&1 || :

%files
%defattr(-,root,root,-)
%{_unitdir}/duetcontrolserver.service
%config(noreplace) %{dsfoptdir}/conf/config.json
%{dsfoptdir}/bin/DuetControlServer*
