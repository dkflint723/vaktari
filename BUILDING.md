# Building Vaktari

Linux-first, developed on Fedora KDE. Nothing here is KDE-specific — Vaktari
reads the desktop's own configuration where it exists and falls back where it
does not — but the colour scheme and icon theme come from `kdeglobals`, so on a
non-KDE desktop it will use its built-in defaults.

**Windows runs.** What it does is in the [README](README.md); what it does
not — content search among them — is under *Known limits* there. The plan the
port was built from is kept at [docs/history/WINDOWS.md](docs/history/WINDOWS.md),
because comments in the source cite its sections; it is not a status document.
`Vaktari.Ui` picks its platform assembly from the build machine's OS.

Publishing on Windows needs the **MSVC C++ build tools**, and `vswhere.exe` on
`PATH` so the NativeAOT toolchain lookup can find them:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish src/Vaktari.Ui -c Release -r win-x64 -p:PublishAot=true
```

As on Linux, **the publish directory is the deliverable** — the executable needs
`libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and `av_libglesv2.dll` beside it.

To get the **installer** rather than the directory, without waiting for CI:

```bash
packaging/build-windows.sh 0.5.2
```

That does the publish above and hands the result to Inno Setup, leaving
`dist/vaktari-0.5.2-win-x64-setup.exe` and its checksum — the same two files
the release page carries, from the same `vaktari.iss`. It puts `vswhere` on
`PATH` itself, finds Inno Setup 6 or 7 wherever it was installed, and refuses to
package a publish that is missing one of the three libraries above.

**The version is required and has no default.** It is stamped into the binary,
the filename and the Add/Remove Programs entry, so a local build that guessed
one would claim to be a release that does not exist — and `vaktari --version`
is how you tell which copy you are running. Pick something above whatever is
installed, or Windows treats the install as a downgrade.

The choice is a `VaktariPlatform` property, defaulted from the OS and
overridable, so either configuration can be compiled from either machine:

```bash
dotnet build src/Vaktari.Ui -p:VaktariPlatform=Linux
```

That override is worth knowing about on Linux too — it is how you check a change
has not broken the Windows configuration without waiting for CI. It proves the
other configuration *compiles*; to check that it *behaves*, docs/history/WINDOWS.md §8a has a
WSL recipe for running the Linux suite from the Windows machine.

**Rebuild without the override before running.** Both configurations write to the
same `bin/Debug/net10.0/`, so the cross-check leaves the other platform's binary
where you launch from. **Building in WSL does the same thing by a different
route** — a checkout under `/mnt/d` is the same directory Windows builds into,
so a `dotnet build` on the Linux side replaces the Windows binary with a real
Linux one, no override involved. It starts and then dies at the platform seam with
`PlatformNotSupportedException: No platform implementation for this operating
system yet` — a true statement about a binary built for the other OS, and a
thoroughly misleading one about your machine. A bare `dotnet build` fixes it.

`dotnet test` is green on both, and the `PathRules` suite is split three ways —
platform-neutral, POSIX, Windows — because a POSIX literal names something else
on Windows. Each half skips on the other's platform, so a run reports skips
rather than failures. docs/history/WINDOWS.md §5b has the detail.

---

## 1. The SDK

Vaktari targets **.NET 10** (`net10.0`). That is the one hard requirement; an
older SDK will not build it.

### Fedora

```bash
sudo dnf install dotnet-sdk-10.0
```

### Arch

```bash
sudo pacman -S dotnet-sdk
```

Arch's `dotnet-sdk` tracks the current release, so check what you actually got:

```bash
dotnet --list-sdks
```

**If your distribution does not carry .NET 10 yet**, install it beside the system
one rather than fighting the package manager:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
export PATH="$HOME/.dotnet:$PATH"
```

That puts the SDK in `~/.dotnet` and touches nothing system-wide. Add the
`export` to your shell profile if you want it to persist.

---

## 2. Build and run

```bash
git clone <your remote> vaktari
cd vaktari
dotnet build
dotnet run --project src/Vaktari.Ui
```

The first build restores Avalonia 12.1 and CommunityToolkit.Mvvm from NuGet.
Nothing else is fetched, and no native library is compiled — a debug build needs
only the SDK.

**`TreatWarningsAsErrors` is on for every project.** A warning you might ignore
elsewhere fails the build here, deliberately. Trim, AOT and single-file analysers
are also enabled in debug, so a dependency that would break a published build
shows up now rather than months later.

---

## 3. Runtime programs

Vaktari shells out rather than reimplementing what the desktop already does.
None of these is needed to *build*, and the application starts without any of
them — the corresponding feature simply does nothing.

| Program | Package (Fedora) | Package (Arch) | Used for |
|---|---|---|---|
| `gio` | `glib2` | `glib2` | mounting and unmounting drives |
| `git` | `git` | `git` | version-control decorations |
| `xdg-mime`, `xdg-open` | `xdg-utils` | `xdg-utils` | opening files, mime fallback |
| `avahi-browse` | `avahi-tools` | `avahi` | discovering network shares |
| — | `shared-mime-info` | `shared-mime-info` | the mime database itself |

```bash
# Fedora
sudo dnf install glib2 git xdg-utils avahi-tools shared-mime-info

# Arch
sudo pacman -S glib2 git xdg-utils avahi shared-mime-info
```

`shared-mime-info` earns its place: Vaktari parses `/usr/share/mime/globs2`
directly and only falls back to spawning `xdg-mime` for a name the glob database
cannot classify. That fallback is capped and cached, because it is a shell script
that starts processes — a folder full of extensionless files once turned it into
a 44-second listing.

**Optional — file sharing.** The share feature runs `copyparty` if it can find
it, either on `PATH` or as `python3 -m copyparty`. It is packaged on neither
distribution:

```bash
pipx install copyparty        # or: pip install --user copyparty
```

---

## 4. Publishing

Release builds are trimmed, and NativeAOT removes the need to install a .NET
runtime on the target. **It does NOT produce a single file.** That needs a C
toolchain:

```bash
# Fedora
sudo dnf install clang zlib-devel

# Arch
sudo pacman -S clang            # zlib is already in base
```

Then:

```bash
dotnet publish src/Vaktari.Ui -c Release -r linux-x64 -p:PublishAot=true
```

**The whole `publish/` directory is the deliverable, not just the executable:**

```
src/Vaktari.Ui/bin/Release/net10.0/linux-x64/publish/
├── Vaktari.Ui          the program
├── libSkiaSharp.so      rendering  — REQUIRED
└── libHarfBuzzSharp.so  text shaping — REQUIRED
```

**"Self-contained" means no .NET runtime to install. It does not mean one
file.** SkiaSharp and HarfBuzz are native libraries and stay beside the binary,
which loads them from its own directory at startup. Copy the executable out on
its own and it aborts before drawing anything:

```
System.DllNotFoundException: Unable to load shared library 'libSkiaSharp'
  at Avalonia.Skia.SkiaPlatform.Initialize(SkiaOptions)
```

So install the directory and link to it, rather than copying the binary:

```bash
P=src/Vaktari.Ui/bin/Release/net10.0/linux-x64/publish
mkdir -p ~/.local/lib/vaktari
cp -a "$P"/. ~/.local/lib/vaktari/
ln -sf ~/.local/lib/vaktari/Vaktari.Ui ~/.local/bin/vaktari
```

The symlink works because the loader resolves `/proc/self/exe` before looking
for neighbours, so it finds the libraries in the real directory.

`InvariantGlobalization` is on, so there is **no ICU dependency** — the binary
does not need `libicu` on the target machine. Fontconfig and an X11 or Wayland
session are still required, which any desktop already has.

---

## 4a. Tests

```bash
dotnet test tests/Vaktari.Core.Tests
```

**Only the pure pieces of `Vaktari.Core` are covered** — `PathRules`,
`ByteSize`, `NaturalOrder` and `BatchRename`. Those are the parts with real
logic, no filesystem and no UI, so a test is cheap and a failure is unambiguous.
They run in CI on every push, after the Debug build and before the AOT publish,
so a broken assumption fails in seconds rather than after a 55-second link.

**The test project switches OFF the trim and AOT analysers** that
`Directory.Build.props` turns on everywhere else. xunit and the test host use
reflection by design, and with warnings-as-errors inherited those analysers would
fail the build over code that is never published. **Switched off in the test
project rather than loosened in the shared props** — the application's guarantees
must not weaken to accommodate its tests.

Deliberately not covered: `PathCompleter` (needs real directories on disk),
`Checksums` (needs real files), and anything in `Vaktari.Ui` (needs a display).

## 5. Giving someone else a build

`.github/workflows/build.yml` builds on every push to `main` and publishes a
release tarball when you push a `v*` tag:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

That produces `vaktari-linux-x64.tar.gz` on the Releases page, containing the
publish directory plus `install.sh`. The recipient runs:

```bash
tar -xzf vaktari-linux-x64.tar.gz
cd vaktari && ./install.sh
```

It installs under `~/.local`, needs no root, and refuses to proceed if
`libSkiaSharp.so` is missing rather than installing something that aborts at
startup.

**Two limits worth knowing before sending it to a stranger.**

**glibc.** The binary is built on the runner's glibc and will not start on an
older one — fine for current Fedora and Arch, not for a long-term-support distro
several years behind. Building in a container based on the oldest glibc you want
to support is the fix, when that day comes.

**Nothing is sandboxed, and that is deliberate.** Vaktari reads `kdeglobals`,
the icon theme, the XDG trash and `user-dirs.dirs`, and shells out to `gio`,
`git`, `xdg-open` and `avahi-browse`. **Flatpak would be a poor fit** — a file
manager wants the host filesystem and the host's programs, which is exactly what
the sandbox exists to prevent. If a single self-contained file is ever wanted,
**AppImage** is the format that matches this application, because it bundles
without isolating.

**A private repo's releases are private too.** Anyone you send the link to needs
access to the repository. To hand a build to someone outside, either attach the
tarball directly or make the repo public.

**When CI is not available** — an exhausted Actions budget, an offline machine,
or wanting to try a change before tagging it — `packaging/build-windows.sh`
produces the Windows installer locally from the same script the workflow uses.
There is no Linux equivalent, because the publish directory plus `install.sh` is
already the deliverable there and needs no compiler beyond the SDK.

---

## 5a. Distribution packages

`.github/workflows/distro.yml` builds **natively inside Fedora and Arch
containers**, on a `v*` tag or on demand from the Actions tab. Two reasons it is
separate from `build.yml`: container jobs take minutes, and a distro build is
only interesting when a release is being cut.

**Fedora** produces an RPM from `packaging/vaktari.spec`. The spec packages the
already-published tree rather than compiling inside `rpmbuild` — NativeAOT wants
an SDK and clang, and CI has already done that work. The job then **installs the
RPM and runs it**, because a package that installs is the only proof that the
`Requires:` list is complete.

**Arch** builds and validates `packaging/PKGBUILD`. Unlike the RPM it compiles
from source, which is the Arch convention and what the AUR expects.

> **The Arch package is `vaktari`.** It was `vaktari-fm` when the project was
> called Heimdall, because `heimdall` is taken in Arch's repositories by the
> Samsung firmware flashing tool. The rename left that reason behind, so the
> suffix is gone and `packaging/PKGBUILD` supersedes the old package with
> `replaces=('heimdall-fm')`. Confirm the name is still free before publishing:
> `pacman -Ss '^vaktari$'`.

**Publishing to the AUR is deliberately not automated.** It needs an SSH key with
push rights to `aur.archlinux.org`, and putting one in repository secrets so a
workflow can publish on your behalf is a decision worth making consciously. The
manual path:

```bash
git clone ssh://aur@aur.archlinux.org/vaktari.git
cp packaging/PKGBUILD packaging/.SRCINFO vaktari/
cd vaktari && git commit -am "0.1.0" && git push
```

Update `sha256sums` in the PKGBUILD first — it ships as `SKIP`, which the AUR
accepts but which verifies nothing.

---

## 6. Things that will trip you up

**`src/Vaktari.Ui/vaktari.png` must exist for the build to succeed.** It is
tracked, so a fresh clone has it — but it is referenced as an `AvaloniaResource`
and embedded in the binary, so deleting it locally fails the build with:

```
error MSB4018: The "GenerateAvaloniaResourcesTask" task failed unexpectedly.
System.IO.FileNotFoundException: Could not find file '…/src/Vaktari.Ui/vaktari.png'
```

Restore it with `git checkout -- src/Vaktari.Ui/vaktari.png`. Any 128×128 PNG at
that path also unblocks a build; the task only needs a readable image.

**Debug builds carry `AvaloniaUI.DiagnosticsSupport`**, which is excluded from
Release by condition. If you publish and the developer tools vanish, that is why.

**Avalonia is 12.1, not 11.** APIs moved. If you are reading advice written for
Avalonia 11 and it does not compile, that is usually the reason — decompile the
shipped assembly rather than trusting a blog post:

```bash
dotnet tool install -g ilspycmd
export PATH="$PATH:$HOME/.dotnet/tools"
ilspycmd -t Avalonia.RelativePoint \
  ~/.nuget/packages/avalonia/12.1.0/lib/net10.0/Avalonia.Base.dll
```

That habit has settled more questions in this project than any amount of
reasoning about what the framework "should" do.

---

## 7. Diagnostics

Environment variables, all off by default:

| Variable | Prints |
|---|---|
| `vaktari --version` | the version and the file it is running from — **check this first when a feature seems missing** |
| `VAKTARI_LOAD_DEBUG=1` | heap, GC and thread-pool counters per folder load |
| `VAKTARI_TILE_DEBUG=1` | realized container count, index range and viewport per measure |
| `VAKTARI_ICON_DEBUG=1` | per-shape bounds, brushes and gradient axes while rendering SVG icons |
| `VAKTARI_FONT_DEBUG=1` | font resolution |
| `VAKTARI_SETTINGS_DEBUG=1` | settings as deserialized, after normalising, and what a fresh record claims |
| `VAKTARI_PANEL_DEBUG=1` | why the details panel grew, held or gave back window width |
| `VAKTARI_QUIET_DEBUG=1` | failures that are deliberately swallowed — network discovery, scripts, sharing |

`VAKTARI_TILE_DEBUG` is the one to reach for first when a listing feels slow:
the realized count is unambiguous in a way that a timing figure is not. If it
approaches the item count, nothing is being virtualized.
