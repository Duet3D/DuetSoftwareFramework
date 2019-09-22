%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0
%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetwebserver
Version: %{_tversion}
Release: 900
Summary: DSF Web Server
Group:   3D Printing
Source0: duetwebserver_%{_tversion}
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetcontrolserver
Requires: duetruntime

AutoReq:  0

%description
DSF Web Server

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

%post
/bin/systemctl daemon-reload >/dev/null 2>&1 || :
/bin/systemctl enable duetwebserver.service >/dev/null 2>&1 || :

%preun
if [ "$1" -eq "0" ]; then
	/bin/systemctl --no-reload disable duetwebserver.service > /dev/null 2>&1 || :
	/bin/systemctl stop duetwebserver.service > /dev/null 2>&1 || :
fi

%postun
/bin/systemctl daemon-reload >/dev/null 2>&1 || :

%files
%defattr(-,root,root,-)
%{_unitdir}/duetwebserver.service
%config(noreplace) %{dsfoptdir}/conf/http.json
%{dsfoptdir}/bin/DuetWebServer*
%{dsfoptdir}/bin/web.config
%{dsfoptdir}/bin/appsettings.json
%{dsfoptdir}/bin/appsettings.Development.json
