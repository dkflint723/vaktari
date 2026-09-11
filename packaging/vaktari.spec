# Packages the ALREADY-PUBLISHED output rather than building from source in
# rpmbuild. NativeAOT wants a .NET SDK and clang, which would make this spec
# drag a large BuildRequires set behind it for no benefit — CI has already done
# that work and produced a self-contained tree.
#
# Version is injected by the workflow: rpmbuild --define "version 0.1.0"
%global _build_id_links none
%global __strip /bin/true

Name:           vaktari
Version:        %{?version}%{!?version:0.0.0}
Release:        1%{?dist}
Summary:        A file manager for KDE that consumes the desktop instead of reimplementing it

# Supersedes the package this was before the rename. Without it, `dnf upgrade`
# installs vaktari and leaves the whole heimdall install in place — two copies
# of the same program, one of them with a dead desktop entry.
#
# Unversioned Obsoletes on purpose: every heimdall that ever existed is
# superseded, and there will never be a newer one.
Obsoletes:      heimdall
Provides:       heimdall = %{version}-%{release}

License:        MIT
URL:            https://github.com/dkflint723/vaktari
Source0:        vaktari-linux-x64.tar.gz

ExclusiveArch:  x86_64

# Loaded at runtime by the bundled SkiaSharp, and by the desktop integration.
Requires:       fontconfig
Requires:       glib2
Requires:       shared-mime-info
Requires:       xdg-utils

# Each of these turns a feature on. The application starts and runs without
# them, so they are not hard requirements — see README.
Recommends:     git
Recommends:     avahi-tools

# The binary is NativeAOT: no .NET runtime, and no debuginfo worth extracting.
%global debug_package %{nil}

%description
Vaktari is a Linux-first file manager built with Avalonia. It reads the
desktop's own configuration — colour scheme, icon theme, font, trash, bookmarks
and mounts — from the same places KDE reads them, rather than maintaining a
parallel set of settings.

%prep
%setup -q -n vaktari

%install
# The whole publish directory travels together: libSkiaSharp.so and
# libHarfBuzzSharp.so are loaded from the binary's own directory, so copying out
# the executable alone produces something that aborts before it draws anything.
install -d %{buildroot}%{_libdir}/vaktari
cp -a Vaktari.Ui *.so %{buildroot}%{_libdir}/vaktari/

install -d %{buildroot}%{_bindir}
ln -s %{_libdir}/vaktari/Vaktari.Ui %{buildroot}%{_bindir}/vaktari

install -D -m644 %{_sourcedir}/vaktari.desktop \
    %{buildroot}%{_datadir}/applications/vaktari.desktop

for size in 16 24 48; do
    install -D -m644 %{_sourcedir}/icons/hicolor/${size}x${size}/apps/vaktari.svg \
        %{buildroot}%{_datadir}/icons/hicolor/${size}x${size}/apps/vaktari.svg
done
install -D -m644 %{_sourcedir}/icons/hicolor/scalable/apps/vaktari.svg \
    %{buildroot}%{_datadir}/icons/hicolor/scalable/apps/vaktari.svg

# The symbolic variant, which the package was silently dropping. It is a
# single-colour glyph using the `.ColorScheme-Text` + `fill="currentColor"`
# convention, so the desktop tints it to match wherever it is drawn — which is
# what a dark panel or a monochrome menu actually wants. Shipping the full-colour
# icon there gives a plate of navy in a row of line art.
#
# Note the filename differs from the others: the spec names symbolic icons
# `<name>-symbolic.svg`, and the theme resolves it by that name rather than by
# the directory alone.
install -D -m644 %{_sourcedir}/icons/hicolor/symbolic/apps/vaktari-symbolic.svg \
    %{buildroot}%{_datadir}/icons/hicolor/symbolic/apps/vaktari-symbolic.svg

# Refresh the desktop and icon caches. Fedora 30+ has RPM file triggers that do
# this automatically, so these are belt-and-braces there — but the spec should
# not depend on the host distribution having them, and `|| :` means a machine
# without the tools installed still upgrades cleanly.
%post
/usr/bin/update-desktop-database &>/dev/null || :
/usr/bin/touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :

%postun
/usr/bin/update-desktop-database &>/dev/null || :
# $1 is the number of remaining copies: 0 means this was an uninstall rather
# than the old half of an upgrade, and only then should the cache be rebuilt.
if [ $1 -eq 0 ]; then
    /usr/bin/touch --no-create %{_datadir}/icons/hicolor &>/dev/null || :
    /usr/bin/gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :
fi

%posttrans
/usr/bin/gtk-update-icon-cache %{_datadir}/icons/hicolor &>/dev/null || :

%files
%license LICENSE
%license THIRD-PARTY-NOTICES.txt
%doc README.md
%{_libdir}/vaktari/
%{_bindir}/vaktari
%{_datadir}/applications/vaktari.desktop
%{_datadir}/icons/hicolor/*/apps/vaktari.svg
%{_datadir}/icons/hicolor/symbolic/apps/vaktari-symbolic.svg

%changelog
* Wed Jul 29 2026 Flint <noreply@users.noreply.github.com> - 0.1.0-1
- Initial package
