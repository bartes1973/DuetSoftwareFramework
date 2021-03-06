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
Release: 901
Summary: DSF Control Server
Group:   3D Printing
Source0: duetcontrolserver_%{_tversion}
License: GPLv3
URL:     https://github.com/chrishamm/DuetSoftwareFramework
BuildRequires: rpm >= 4.7.2-2
Requires: duetruntime
%systemd_requires

AutoReq:  0

%description
DSF Control Server

%prep
%setup -q -T -c -n %{name}-%{version}

%build

%install
rsync -vaH %{S:0}/. %{buildroot}/

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
%config(noreplace) %{dsfoptdir}/conf/config.json
%{dsfoptdir}/bin/DuetControlServer*
