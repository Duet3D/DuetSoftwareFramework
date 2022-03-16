%define __objdump /usr/bin/true
%define __strip /usr/bin/true
%define _build_id_links none
%global _debug 			0

%global debug_package %{nil}

%global _bindir /usr/bin
%global _datadir /usr/share
%global dsfoptdir /opt/dsf

Name:    duetpluginservice
Version: %{_tversion}
Release: %{_release}
Summary: DSF Plugin Service
Group:   3D Printing
Source0: duetpluginservice_%{_tversion}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetcontrolserver = %{_tversion}
%systemd_requires

AutoReq:  0

%description
DSF Service for third-party plugins

%build
mkdir %{buildroot}/%{dsfoptdir}/plugins >/dev/null 2>&1 || :

%pre
if [ $1 -gt 1 ] && systemctl -q is-active %{name}.service ; then
# upgrade
	systemctl stop %{name}.service > /dev/null 2>&1 || :
	systemctl stop %{name}-root.service > /dev/null 2>&1 || :
fi


%post
systemctl daemon-reload >/dev/null 2>&1 || :

%preun
if [ $1 -eq 0 ] ; then
# remove
	systemctl --no-reload disable %{name}.service >/dev/null 2>&1 || :
	systemctl --no-reload disable %{name}-root.service >/dev/null 2>&1 || :
fi

%postun
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}.service ; then
# upgrade
	systemctl start %{name}.service
fi
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}-root.service ; then
	systemctl start %{name}-root.service
fi

%files
%defattr(0644,root,root,-) 
%{_unitdir}/duetpluginservice.service
%{_unitdir}/duetpluginservice-root.service

%defattr(-,dsf,dsf,-)
%dir %{dsfoptdir}/plugins
%{dsfoptdir}/bin/DuetPluginService
%{dsfoptdir}/bin/DuetPluginService.*
%config(noreplace) %{dsfoptdir}/conf/plugins.json
%config(noreplace) %{dsfoptdir}/conf/apparmor.conf


