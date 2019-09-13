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
Release: 900
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
%defattr(-,root,root,-)
%{dsfoptdir}/bin/Zhaobang.IO.SeekableStreamReader.dll
%{dsfoptdir}/bin/System.*
%{dsfoptdir}/bin/Nito.*
%{dsfoptdir}/bin/Newtonsoft.*
%{dsfoptdir}/bin/netstandard.dll
%{dsfoptdir}/bin/Microsoft.*
%{dsfoptdir}/bin/LinuxDevices.*
%{dsfoptdir}/bin/libclrjit.so
%{dsfoptdir}/bin/libcoreclr.so
%{dsfoptdir}/bin/libhostfxr.so
%{dsfoptdir}/bin/libhostpolicy.so
%{dsfoptdir}/bin/DuetAPI.*
%{dsfoptdir}/bin/DuetAPIClient.*

%if %{?_build_type} == "Debug"
%{dsfoptdir}/bin/WindowsBase.dll
%{dsfoptdir}/bin/SOS.NETCore.dll
%{dsfoptdir}/bin/sosdocsunix.txt
%{dsfoptdir}/bin/Remotion.Ling.dll
%{dsfoptdir}/bin/mscorlib.dll
%{dsfoptdir}/bin/libsos.so
%{dsfoptdir}/bin/libsosplugin.so
%{dsfoptdir}/bin/libmscordbi.so
%{dsfoptdir}/bin/libmscordaccore.so
%{dsfoptdir}/bin/libdbgshim.so
%{dsfoptdir}/bin/libcoreclrtraceptprovider.so
%endif
