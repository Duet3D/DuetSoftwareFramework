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
Release: %{_tag:%{_tag}-}%{_release}
Summary: DSF Control Server
Group:   3D Printing
Source0: duetcontrolserver_%{_tversion}%{_tag:-%{_tag}}
License: GPLv3
URL:     https://github.com/Duet3D/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetruntime = %{_tversion}
%systemd_requires

AutoReq:  0

%description
DSF Control Server

%pre
if [ $1 -gt 1 ] && systemctl -q is-active %{name}.service ; then
# upgrade
	systemctl stop %{name}.service > /dev/null 2>&1 || :
fi

%post
systemctl daemon-reload >/dev/null 2>&1 || :

%preun
if [ $1 -eq 0 ] ; then
# remove
	systemctl --no-reload disable %{name}.service >/dev/null 2>&1 || :
fi

%postun
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}.service ; then
# upgrade
	systemctl start %{name}.service
fi

%files
%defattr(-,root,root,-)
%{_unitdir}/duetcontrolserver.service
%config(noreplace) %{_sysconfdir}/udev/rules.d/99-dsf-gpio.rules
%{_exec_prefix}/lib/sysusers.d/duetcontrolserver.conf
%{_exec_prefix}/lib/tmpfiles.d/duetcontrolserver.conf

%defattr(-,dsf,dsf,-)
%{dsfoptdir}/bin/DuetControlServer
%{dsfoptdir}/bin/DuetControlServer.*
%config(noreplace) %{dsfoptdir}/conf/config.json
