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
Requires: libcap
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
# File capabilities trigger AT_SECURE=1 on exec, which DPS verifies to defend against LD_PRELOAD masquerade.
# The IPC handshake rejects peers without AT_SECURE, so warn loudly if setcap fails
if ! setcap cap_sys_ptrace,cap_dac_read_search+ep %{dsfoptdir}/bin/DuetControlServer >/dev/null 2>&1; then
    echo "WARNING: failed to set file capabilities on DuetControlServer" >&2
    echo "         DSF services will refuse IPC connections until file capabilities can be set" >&2
fi

# Ensure dsf group memberships on upgrade. systemd-sysusers "m" lines are unreliable for
# pre-existing users on older systemd versions, so re-apply explicitly
for grp in gpio video dialout; do
    getent group "$grp" >/dev/null 2>&1 && usermod -a -G "$grp" dsf || :
done

%preun
if [ $1 -eq 0 ] ; then
# remove
	systemctl --no-reload disable %{name}.service >/dev/null 2>&1 || :
fi

%postun
if [ $1 -eq 1 ] && systemctl -q is-enabled %{name}.service ; then
# upgrade. Ignore the return code in case no board is connected
	systemctl start %{name}.service || :
fi

%files
%defattr(-,root,root,-)
%{_unitdir}/duetcontrolserver.service
%{_unitdir}/system.slice.d/duetcontrolserver.conf
%config(noreplace) %{_sysconfdir}/udev/rules.d/99-dsf-gpio.rules
%{_exec_prefix}/lib/sysusers.d/duetcontrolserver.conf
%{_exec_prefix}/lib/tmpfiles.d/duetcontrolserver.conf

%defattr(-,dsf,dsf,-)
%{dsfoptdir}/bin/DuetControlServer
%{dsfoptdir}/bin/DuetControlServer.*
%config(noreplace) %{dsfoptdir}/conf/config.json
