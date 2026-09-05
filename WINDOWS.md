# Starting the Windows port

Written 29 July 2026, from the tree at that date. Everything below was read off
the source rather than recalled; where something is a guess it says so.

**This is a work plan, not a status document.** Delete it when the port lands.

---

## 1. The good news: there is exactly one seam

`MainWindow.axaml.cs` line ~54 is, in its own words, *"the one and only place a
platform type is named"*:

```csharp
IPlatform platform;

if (OperatingSystem.IsLinux())
    platform = new LinuxPlatform(JsonSessionStore.DefaultDirectory());
else
    throw new PlatformNotSupportedException(
        "No platform implementation for this operating system yet.");
```

Adding Windows means adding an `else if (OperatingSystem.IsWindows())` branch
and one project. **The UI names no other platform type** — verified by grepping
`Vaktari.Ui` for every `Xdg*`, `Linux*`, `Avahi*`, `Copyparty*` identifier; the
only hits are that one line and a comment. The local helper once named
`XdgDeduplicate` is gone: naming a new folder or file is `Core`'s
`NewItemName.Free`, which parenthesises on both platforms because both copy
engines already do.

`IPlatform` is the whole surface: 19 members, seven of them nullable because the
interface already anticipates a platform that lacks the capability. **Those
nullables are the porting budget** — `Sharing`, `Remotes`, `Discovery`, `Theme`,
`Icons`, `TrashMaintenance`, `AccessEditor` can all return `null` on day one and
the application still runs.

---

## 2. Two blockers before any code — DONE

Both cleared 3 August 2026. **The property-function `Condition` syntax works** —
that was the open question, and it is now answered on a real Windows machine.

The reference is not conditioned on the OS directly, but on a `VaktariPlatform`
property that *defaults* from the OS:

```xml
<PropertyGroup>
  <VaktariPlatform Condition="'$(VaktariPlatform)' == '' AND '$([System.OperatingSystem]::IsLinux())' == 'true'">Linux</VaktariPlatform>
  <VaktariPlatform Condition="'$(VaktariPlatform)' == '' AND '$([System.OperatingSystem]::IsWindows())' == 'true'">Windows</VaktariPlatform>
</PropertyGroup>
```

**The indirection is the point.** §8 warns that conditional references make it
possible to break the Linux build from a Windows machine without noticing, and
CI is a slow way to be told. With the override, either configuration compiles
from either machine:

```bash
dotnet build src/Vaktari.Ui -p:VaktariPlatform=Linux
```

**Verified on both, symmetrically** (3 August 2026 — Windows 11, and Fedora 44
under WSL): each machine builds its own configuration, the other machine's
configuration, and fails on `VaktariPlatform=None` with one clear error. On
each, only the selected platform assembly lands beside the app — no leakage
either way.

> **The override is a compile check, not a runnable build. Rebuild without it
> before you run anything.** Both configurations write to the same
> `bin/Debug/net10.0/`, so a cross-check build leaves the *other* platform's
> binary sitting at the path you launch from — and it starts, gets as far as the
> platform seam, and dies with
> `PlatformNotSupportedException: No platform implementation for this operating
> system yet.` The message is accurate and completely misleading: nothing is
> wrong with the platform, the binary was just compiled for the other one.
> A bare `dotnet build` puts the right one back.

`MainWindow.axaml.cs` is fenced with `VAKTARI_LINUX` / `VAKTARI_WINDOWS`,
defined beside the reference they belong to, and a `#else` arm carries an
`#error` naming the fix. **Give each arm its own `else`** — sharing one after the
`#endif` compiles, but leaves the `#error` arm ending in a dangling `else`, and
the five cascading syntax errors bury the message that explains the problem.

`[assembly: SupportedOSPlatform("windows")]` mirrors the Linux one.
**`Vaktari.Windows` stays on plain `net10.0`**, so it still compiles on the
Linux CI runner and is checked on every push — see §9.

**`Vaktari.Linux/AssemblyInfo.cs` carries `[assembly: SupportedOSPlatform("linux")]`.**
`Vaktari.Windows` needs the mirror image, and it can additionally use the real
`net10.0-windows` TFM, which Linux cannot (no `net10.0-linux` exists). That TFM
unlocks the Windows Forms/WPF interop surface if it is ever wanted — probably it
is not, but it also silences the platform analyser properly.

---

## 3. What is already portable, and must not be re-implemented

`Vaktari.Core` holds real logic, not just contracts. **None of this needs
touching:**

| | |
|---|---|
| `Vcs/GitVersionControl` | drives the `git` binary; behaves identically on Windows |
| `FileSystem/Checksums` | pure |
| `FileSystem/ImageSize` | header parsing, byte-level |
| `FileSystem/ByteSize`, `Grouping`, `FileKinds` | pure |
| `NaturalOrder` | pure |
| `PathCompleter` | **uses `/` in a doc comment only** — check the logic uses `Path.DirectorySeparatorChar` |
| `BatchRename` | pure |
| `PreviousName` | pure |

The whole `Vaktari.Ui` layer is Avalonia and portable in principle. Its problems
are path assumptions, not APIs — see §5.

---

## 4. The providers, ordered by how much they will hurt

### Trivial — a day, mostly BCL
- **`IFileSystemProvider`** — `Directory.EnumerateFileSystemEntries` already.
  Windows adds drive roots (`C:\`) where Linux has one `/`.
- **`IFileOperations`** — copy/move/delete are BCL. **Trash is the exception,
  see below.**
- **`IApplicationLauncher`** — `ShellExecute` via `Process.Start` with
  `UseShellExecute = true`. Easier than the Linux `.desktop` parsing.
- **`ISearchProvider`** — a directory walk. The Linux one shells out to `find`;
  a managed walk is fine and more portable.
- **`ITemplateProvider`** — Windows has `%APPDATA%\Microsoft\Windows\Templates`.
- **`IScriptRunner`** — same shape, different interpreter conventions.

### Moderate — real work but well-trodden
- **`IPlacesProvider`** — `SHGetKnownFolderPath` for Desktop/Documents/etc, plus
  `DriveInfo.GetDrives()`. **Registry-free via `Environment.GetFolderPath`,
  which covers most of it without P/Invoke.**
- **`IThemeProvider`** — registry: `HKCU\...\Themes\Personalize\AppsUseLightTheme`
  for dark mode, and DWM's `ColorizationColor` for the accent. Straightforward
  reads; no COM.
- **`IPropertiesProvider` / `IFileMetadataProvider`** — `FileInfo` covers most.
  Rich metadata (image dimensions, media duration) has no BCL equivalent —
  **`ImageSize` in Core already solves the image case.**
- **`IThumbnailProvider`** — Windows has `IShellItemImageFactory` (COM). A
  cheaper first pass: decode images directly with `ImageSize` + Avalonia, and
  return null for everything else. **The freedesktop thumbnail cache does not
  exist on Windows, so `XdgThumbnailProvider`'s whole caching strategy is moot.**
  **DONE, and the first pass was half of it.** Images are still decoded
  directly — the original file beats the shell's cached copy, and the "too small
  to enlarge" rule lives on that path. Everything else now goes to the shell
  with `SIIGBF_THUMBNAILONLY`, which is what makes video, PDF, HEIC and TIFF
  previews appear. Two things were needed beyond the call itself. *Which* types
  can have one is read from the registry per extension
  (`ShellEx\{E357FCCD-…}` under the extension, its ProgID, or its perceived
  type) rather than hardcoded, because the answer is per machine — this one has
  handlers for .pdf, .docx, .mp4 and .heic and none at all for .svg. And the
  call is time-bounded, because a thumbnail handler is somebody else's code:
  see `WindowsShellThumbnails`. The pixels come back as `IconPixels` through
  `GetThumbnailPixelsAsync`, which is the "per-file and bitmap-returning" seam
  this document asks for below rather than a PNG cached to disk for the sake of
  having a path.

### Hard — expect these to dominate the schedule
- **Trash / Recycle Bin.** There is **no BCL API.** The options are
  `SHFileOperation` (ANSI/Unicode struct marshalling, deprecated but simple) or
  `IFileOperation` (COM, the supported route). **Both are P/Invoke or COM under
  NativeAOT, which is the risky combination** — COM in particular needs
  source-generated interop or it will fail at runtime, not compile time. Budget
  real time here, and see §6.
  **`ITrashMaintenance` also needs `List`/`Restore`/`Empty`** — the Recycle Bin
  exposes these through the same COM surface. Returning `null` for
  `TrashMaintenance` on day one is legitimate and skips all of it.
- **`ITagStore` — DECIDED 4 August 2026: a path-keyed sidecar.** Four reasons,
  in the order they matter.
  **ADS loses data silently and irrecoverably** — a copy to FAT or exFAT, a zip
  round-trip, most archivers and most sync clients drop the stream and report
  success. A sidecar goes *stale*, which is visible and repairable; it cannot go
  silently empty.
  **ADS cannot be read on the listing path.** `GetAsync` runs for every row in
  the viewport, and an alternate stream costs a CreateFile per file where the
  index costs a dictionary lookup. This is `FileEntry`'s "no follow-up stat"
  rule one level down.
  **ADS does not exist off NTFS**, so tagging a file on an exFAT stick would
  simply fail.
  **And the promise it appears to keep, it does not.** The README's claim is
  that tags "travel with it and other tools can read them" — no other Windows
  tool reads a private alternate stream, so ADS buys the fragility of on-file
  storage without the interoperability that would justify it.
  **The cost, stated plainly:** tags do not travel to another machine, and a
  file renamed by *another* program loses them. Renames, moves, undo and
  permanent deletes made through this application keep the index correct —
  `WindowsTagStore.Retarget` follows a whole subtree — which is the case that is
  actually within our control.
  **The upgrade path is not ADS.** It is the property system's
  `System.Keywords`: the field Explorer already labels "Tags", which Windows
  Search indexes and other tools genuinely read. It only exists for formats with
  a metadata container — never a .txt, never a folder — so it can only mirror
  this index, not replace it. It needs `IPropertyStore`, which is COM.
- **`IIconThemeProvider` — DECIDED 4 August 2026: stays `null`, and the blocker
  is the interface rather than the effort.** The whole model differs. Linux has a
  theme of named icons the app resolves; Windows has **per-file icons** from
  `SHGetFileInfo`/`IShellItemImageFactory`. `IconLoader`'s SVG renderer and
  `XdgIconTheme`'s name resolution have **no Windows counterpart at all.**
  Look at the signatures rather than the summary: `ThemeName`,
  `Resolve(names, size)` returning **a path**, `NamesFor(path)` returning
  **freedesktop icon names**, `Reload(themeName)`. Windows has no theme to name,
  no named icons to resolve, and no icon *files* to point at — `SHGetFileInfo`
  answers with an `HICON` and `IShellItemImageFactory` with an `HBITMAP`.
  Satisfying this interface would mean extracting the handle, encoding a PNG and
  caching it to disk purely to have a path to hand back: reimplementing the
  freedesktop thumbnail cache to serve an abstraction that does not fit.
  **The right shape is a different seam** — something per-file and
  bitmap-returning, closer to `IThumbnailProvider` than to this — which is a
  change to Core, not a Windows implementation, and should not be made while
  Linux is the only real consumer.
  Meanwhile `null` falls back to the drawn glyphs in `IconLoader.Fallback` and
  `SidebarIcon`, **which is why those exist and are hand-drawn rather than
  themed**, and they look right on Windows as they stand.
- **`IAccessEditor`.** POSIX modes have no meaning on Windows; ACLs are a
  different model entirely. **Return `null`** — the interface is already nullable
  for exactly this reason.

### Judged not worth doing — and wrong on all three. See §7a.
- ~~**`INetworkDiscovery`** — Avahi has no Windows equivalent worth the effort.~~
  True of avahi, false of the conclusion: Windows runs its own mDNS responder
  and `DnsServiceBrowse` asks it the same question.
- ~~**`IRemoteMounts`** — `gio` has no counterpart; Windows mapped drives appear
  as ordinary drive letters through `IPlacesProvider` anyway.~~ Also true, and
  also not the point — it left no way to *connect* to a share from inside
  Vaktari, and nothing at all for a UNC path nobody had mapped.
- **`IFileSharing`** — copyparty runs on Windows if Python is installed; the
  existing `CopypartyShare` logic is mostly path handling and could move to
  Core. This one was right, and is what happened.

**Worth remembering as a pattern.** Each of these was filed under "not worth
doing" after correctly observing that the *Linux mechanism* has no Windows
counterpart — no avahi, no gio. The feature is not the mechanism, and in both
cases Windows had its own way to do the same thing. A port's real risk is not
the API that is missing; it is concluding from a missing API that the capability
is missing.

---

## 5. The path assumptions — DONE

**All fifteen POSIX assumptions in `Vaktari.Ui` now route through
`Vaktari.Core.FileSystem.PathRules`** (31 July 2026): `IsRoot`, `Normalise`,
`Parent`, `LeafName`, `Same`, `Ancestors`. Pure string shape — it never touches
the filesystem, so anything needing the disk stays on `IFileSystemProvider`.

**Linux behaviour is unchanged**, verified by porting both the new rules and the
inline code they replaced to Python and comparing case by case. The one
deliberate difference: `"//"` used to normalise to `""` — an empty path, a latent
bug — and now gives `/`.

What the port gets for free:

- **`IsRoot` asks `Path.GetPathRoot`** rather than comparing to `"/"`. A path
  equal to its own root IS the root, so `C:\` and UNC share roots work with no
  platform check.
- **A root keeps its trailing separator.** Trimming `/` leaves `""`; trimming
  `C:\` leaves `C:`, which on Windows means "the current directory on drive C" —
  a different place.
- **`Parent` returns null at a root, not empty.** `Path.GetDirectoryName` returns
  an empty string for a bare name, which had already caused a live bug where the
  Up button enabled itself on a virtual path and then did nothing.
- **`Same` compares `OrdinalIgnoreCase` on Windows and `Ordinal` on Linux**, so
  place highlighting and duplicate-tab detection are right on both: two paths
  differing only in case are one folder on NTFS and two on ext4.
- **`Ancestors`** replaced the column-strip walk that would not have terminated.

Two things deliberately left alone:

- **`FileClipboard`'s `file://` conversion still splits on `/`.** A URI is not a
  path — RFC 8089 uses `/` on every platform — and Windows exchanges files as
  **`CF_HDROP`**, so this needs a different mechanism rather than a separator fix.
  Annotated in place so a sweep does not "correct" it.
- **`VirtualPaths` keeps its `vaktari:` prefixes.** The old rationale (real paths
  start with `/`) stops being true on Windows, but `vaktari:` still cannot
  collide with `C:\`.

### 5a. One thing §5 got wrong: `PathRules` was separator-sensitive — FIXED

Found and fixed 3 August 2026, running `PathRules` on Windows for the first
time. **The rules handled real Windows paths correctly** — `IsRoot(@"C:\")`, UNC
share roots, `Parent`, `LeafName` and `Ancestors` all behaved — but **`Normalise`
never unified the separator character**, and on Windows both `\` and `/` are
legal:

```
Same(@"C:\Users", @"C:/Users")        = False   <-- one folder, two spellings
Same(@"C:\Users", @"C:\Users\")       = True
Same(@"C:\Users", @"c:\users")        = True
Ancestors(@"C:/Users/flint")          = ["C:\", "C:\Users", "C:/Users/flint"]
```

`Same` handles the trailing separator and the case rules — the two things §5
went looking for — and misses the third. **This is not theoretical:** Windows
accepts `C:/Users` everywhere, so it is what a paste into `Ctrl+L` can produce,
and `Same` is exactly what drives **place highlighting and duplicate-tab
detection**. Typing a path with forward slashes opens a second tab on a folder
already open and leaves the sidebar entry unhighlighted.

The `Ancestors` result is the same fault seen from the other side: the last
element is the normalised input and keeps its `/`, while every ancestor comes
from `Path.GetDirectoryName` and gets `\`. **One list, two conventions**, which
the column strip then compares with `Same`.

**The fix is a private `Unify` in `PathRules`**, applied at the top of both
`IsRoot` and `Normalise` — both, because `Normalise` delegates to `IsRoot`, and
fixing only one leaves `IsRoot("/")` disagreeing with `IsRoot(Normalise("/"))`:

```csharp
private static string Unify(string path)
    => path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
```

**It is a no-op on Linux**, where both constants are `/` — and it goes through
the platform's own constants rather than a literal for exactly that reason: `\`
is a legal *filename* character on Linux, so rewriting it there would rename
files. `PathRulesPosixTests.Backslash_is_an_ordinary_character` asserts this.

**No longer an argument: the POSIX suite was run on Fedora 44 after the change
and passes**, so §5's promise that Linux behaviour did not change is observed
rather than reasoned about. See §8a.

After the fix, all four `Same` spellings agree and `Ancestors` returns one list
regardless of how the path was written.

### 5b. The `PathRules` tests were Linux-only and said otherwise — SPLIT

`PathRulesTests.cs` opened by claiming "everything here runs on any platform:
the assertions are about POSIX paths, which the rules must handle identically
wherever they execute." **That was false: 7 of 56 failed on Windows**, and 10
once 5a was fixed, because a POSIX literal names something else here — `/` is
the root of the *current drive*, so `Path.GetPathRoot("/")` answers `\`.

Split three ways on 3 August 2026, with `PosixFact`/`PosixTheory` and
`WindowsFact`/`WindowsTheory` in `PlatformFacts.cs` doing the skipping:

| | |
|---|---|
| `PathRulesTests` | genuinely platform-neutral — virtual paths, empty, null |
| `PathRulesPosixTests` | every original POSIX assertion, verbatim |
| `PathRulesWindowsTests` | the `C:\`, UNC and separator cases, new |

**Skipping rather than a conditional expectation.** A test reading
`expected = IsWindows() ? @"\home" : "/home"` asserts that the code does whatever
it currently does, which is not a test.

**Nothing was weakened to make Windows pass** — the POSIX assertions moved
across unchanged, and they are the record of the promise in §5 that Linux
behaviour did not change.

**Windows: 67 passed, 13 skipped, 0 failed. Fedora 44: 59 passed, 13 skipped, 0
failed.** Thirteen skips each way, and each way they are the other platform's
fixture. The passed counts differ because a skipped theory counts once while a
running one expands per `InlineData` row.

### 5c. §5 missed two sites, and running it is what found them

"All fifteen POSIX assumptions in `Vaktari.Ui` now route through `PathRules`"
was not true. Two more surfaced on 3 August 2026 during step 3 — **neither by
reading the code again, and neither would have been**: the first was visible in
the window within seconds, and the second is in a converter with no test.

- **`PaneViewModel.RebuildBreadcrumbs`** split `CurrentPath` on `'/'` and
  prefixed a hardcoded `"/"` crumb. A Windows path contains no `/`, so the split
  found nothing to break on: the whole path stayed **one unclickable crumb**,
  sitting behind a root that does not exist there. It rendered
  `/ / C:\Users\flint`. Now `PathRules.Ancestors`, which had answered this
  correctly all along — `Ancestors("/x/y")` is the same three crumbs the split
  produced, so Linux is unchanged.
- **`FileConverters.ParentPath`** tested `parent.StartsWith(home + "/")`, so the
  `~` abbreviation never applied on Windows. Now through `PathRules`.

**The lesson is about method, not about these two.** The original sweep was a
grep for POSIX-shaped string operations, and both of these are exactly that —
they were missed because a grep over a large file finds what you remember to
look for. Running the application on the target found one of them immediately.
Assume more remain in code that has never executed on Windows: the search view,
the properties window and the share dialog have all been compiled here and none
has been opened.

---

## 6. NativeAOT is the constraint that will surprise you

The project publishes with `PublishAot=true` and `TrimMode=full`, and
`Directory.Build.props` turns on the trim, AOT and single-file analysers for
**every** project. That means:

- **A Windows provider that uses COM will produce analyser warnings, and
  `TreatWarningsAsErrors` turns those into build failures.** This is a feature —
  it catches the problem at build time rather than as a `PlatformNotSupported`
  at runtime — but it will feel like an obstacle on day one.
- Prefer **`[LibraryImport]` source-generated P/Invoke** over `[DllImport]`, and
  **`ComWrappers`-based source-generated COM** over `Marshal.GetActiveObject`
  style interop. Both are AOT-clean; the older styles are not.
- **Test the published binary, not just the debug build.** The Linux side learned
  this the hard way: a clean `dotnet build` says nothing about whether the AOT
  binary starts. See `BUILDING.md` §4.

### 6a. The AOT publish works — and what it took

Verified 4 August 2026, after the interop landed, because that is the change
this section was warning about:

```bash
dotnet publish src/Vaktari.Ui -c Release -r win-x64 -p:PublishAot=true
```

**Zero analyser warnings**, so the `LibraryImport` choice held up: the trim, AOT
and single-file analysers accepted every declaration in `Native.cs`. The binary
runs, lists `C:\`, and reports `font: desktop='Segoe UI'` — which is the
registry and `SystemParametersInfo` calls working *after* trimming and native
compilation, not just in a debug build.

**`vswhere.exe` must be on `PATH`.** Without it the ILCompiler's own toolchain
lookup fails and its error text is spliced into the linker command line, so the
failure reads as a corrupted `link.exe` path and says nothing about vswhere:

```
error MSB3073: The command ""'vswhere.exe' is not recognized ...;...\link.exe" @"...link.rsp"" exited with code 123
```

Either publish from a Developer Command Prompt, or:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
```

**The publish directory is the deliverable, exactly as on Linux.** `Vaktari.Ui.exe`
is 26.7 MB and needs `libSkiaSharp.dll`, `libHarfBuzzSharp.dll` and
`av_libglesv2.dll` beside it. Self-contained means no .NET runtime to install;
it does not mean one file.

---

## 7. Suggested order

1. ~~**Prove the scaffolding.**~~ **DONE, 3 August 2026.** `Vaktari.Windows`
   exists, both configurations and the neither-selected case were built on
   Windows, and the property-function `Condition` syntax §9 doubted is proven —
   see §2. `WindowsPlatform` returns `null` for all seven nullable members and
   throws `NotImplementedException` naming the interface for the other eleven,
   so the first thing built on top of it fails loudly and identifies itself.
   **Nothing runs yet**: the app throws on `platform.Properties`, which is the
   first member the constructor touches.
   **Two things turned up on the way, both since fixed — see §5a and §5b.**
   `PathRules.Normalise` did not unify `\` and `/`, which broke `Same` on
   Windows, and the `PathRules` tests were POSIX-only while claiming otherwise.
   Green on both: 67/13/0 on Windows, 59/13/0 on Fedora 44.
2. ~~**`PathRules` in Core**, and route the 15 sites through it.~~ **DONE,
   31 July 2026.** `Vaktari.Core/FileSystem/PathRules.cs` answers the four
   questions this application asks about a path's shape — `IsRoot`, `Normalise`,
   `Parent`, `LeafName`, plus `Same` and `Ancestors` — without assuming the
   separator. All fifteen sites route through it; Linux behaviour is unchanged,
   verified case by case against the inline code it replaced.
   **`Same` uses `OrdinalIgnoreCase` on Windows and `Ordinal` on Linux**, because
   two paths differing only in case are one folder on NTFS and two on ext4.
   **The `file://` conversion in `FileClipboard` deliberately still splits on
   `/`** — a URI is not a path, and Windows exchanges files as CF_HDROP anyway.
3. ~~**`IFileSystemProvider` + `IPlacesProvider`.** First light.~~ **DONE,
   3 August 2026.** The window lists `C:\` — 19 of 19 entries in 12 ms, every
   Hidden or System entry correctly excluded — with Local disk (C:) and its free
   space in the sidebar. Icons are the drawn fallbacks, as expected.
   **It needed eleven providers, not two.** `MainWindow`'s constructor reads
   every required member of `IPlatform` before it lists anything —
   `ShellViewModel` takes `Operations`, `Launcher`, `Search`, `Scripts` and
   `Templates` alongside the obvious ones — so a throwing stub anywhere meant no
   window at all. Step 4 below was largely absorbed as a result.
   Two more POSIX assumptions turned up on the way: see §5c.
4. ~~**`IApplicationLauncher`, `IFileOperations` (no trash), `ISearchProvider`.**~~
   **Absorbed into step 3**, for the reason above. Launcher is ShellExecute,
   operations are copy/move/delete/rename with **`Trash` failing rather than
   deleting**, and search is a managed walk with no index behind it.
5. ~~**`IThemeProvider`.**~~ **DONE, 4 August 2026.** Light or dark from
   `AppsUseLightTheme`, the accent from DWM, the UI font from
   `SPI_GETICONTITLELOGFONT`, and both keys watched with
   `RegNotifyChangeKeyValue` so a scheme change repaints without a restart.
   **The rest of the palette is derived**, because Windows publishes only those
   two facts where kdeglobals hands over a whole scheme — see
   `WindowsThemeProvider`.
   **The two accent registry values disagree about byte order.** Measured on one
   machine: `AccentColor = 0xFF4F4737` is ABGR, `ColorizationColor = 0xC437474F`
   is ARGB, and both mean `#37474F`. Read the wrong one the wrong way and the
   accent comes out with red and blue swapped, which looks plausible.
6. **The hard three** — trash, tags, icons. All three now decided; one is fully
   built, one is built, one is deliberately not.
   **Trash: half built.** `IFileOperations.Trash` recycles, via `SHFileOperation`
   rather than COM `IFileOperation`; verified by count, two items in and the bin
   goes from one to three. **`ITrashMaintenance` is still null**, so there is no
   Trash view and no Restore — those need the shell namespace, and that is the
   one COM decision still outstanding. Recycled items restore from Explorer
   meanwhile, which is why the half was worth having early.
   **Tags: built, as a path-keyed sidecar.** See §4 for the four reasons ADS
   lost and what the sidecar costs.
   **Icons: staying null, on purpose.** The interface is freedesktop-shaped in
   its signatures, not just its vocabulary; see §4.

7. **What is left, in the order it is worth doing.**
   - **`ITrashMaintenance`** — the Trash view, Restore and the sweep. The only
     remaining feature the README promises that Windows cannot do at all, and
     the reason there is no Trash entry in the sidebar. Needs source-generated
     COM against the shell namespace. **§6a settled the risk**: the AOT publish
     is clean with source-generated P/Invoke, so COM is the next thing to prove
     rather than a leap.
   - **`GetOpenWithOptions`** — `IAssocHandler`, the same COM decision, much
     smaller. Good first use of it.
   - **A per-file icon seam in Core** — see §4. Wanted on both platforms, so it
     is a Core design question rather than Windows work.
   - ~~**`IFileSharing`**~~ — **DONE**, along with `IRemoteMounts` and
     `INetworkDiscovery`; see §7a.
   - **Screens never opened on Windows** — search, properties, share. Compiled,
     never run. §5c is about exactly this class of bug.

7a. **The network three — DONE.** All three of `IRemoteMounts`, `INetworkDiscovery`
   and `IFileSharing` returned null, so the whole "Network and sharing" section
   of the README did nothing on Windows. The UI was already wired for all of
   them and simply received null, which is why the features were invisible
   rather than broken.

   - **`IRemoteMounts` → the redirector.** `WNetAddConnection2` to connect,
     `WNetOpenEnum` to list, `WNetCancelConnection2` to disconnect — the Windows
     shape of what gvfs does on Linux. Connections are made **deviceless**, with
     no drive letter, because `WindowsPlacesProvider` already lists
     `DriveType.Network` drives under Network and a lettered connection would
     put the same share on screen twice. Credentials go to Windows'
     own dialog via `CONNECT_INTERACTIVE | CONNECT_PROMPT`, so Vaktari never
     handles a password and Credential Manager comes free.
   - **`INetworkDiscovery` → `DnsServiceBrowse`.** Windows has run an mDNS
     responder since 10 version 1703, so `INetworkDiscovery`'s rule — do not
     implement mDNS, ask the responder that has been listening since boot —
     holds here exactly as it does for avahi. **Not** the SMB network
     neighbourhood: `WNetOpenEnum` over `RESOURCE_GLOBALNET` needs the Computer
     Browser service and SMB1, both off by default, and would never find a
     Vaktari share anyway.
   - **`IFileSharing` → Core.** `CopypartyShare` moved to
     `Vaktari.Core/Sharing` as predicted, behind a `CopypartyBackend` that
     carries the two things that genuinely differ: where copyparty is, and how
     to install it. About sixty lines a platform against four hundred shared.

7b. **The parity audit, and what it found.** With the network done, every
   `IPlatform` member was compared against its Linux counterpart. Fifteen of
   eighteen are implemented on both; the three nulls — `AccessEditor`, `Icons`,
   `TrashMaintenance` — are each documented decisions rather than omissions.

   The audit's value was in the members that are implemented and were still not
   equivalent, which no null check would have caught:

   - **Search ignored globs.** `LinuxSearchProvider` treats a query containing
     `*` or `?` as a pattern and anything else as a substring. The Windows walk
     had only the substring arm, so `*.cs` matched nothing at all — and looked
     like an empty result rather than an unsupported syntax. **Fixed**; both now
     use `FileSystemName.MatchesSimpleExpression` for patterns.
   - **`ImportExistingAsync` returned 0 at every startup.** Linux imports the
     user's KDE and GTK bookmarks. **Partly fixed**: the Links and Network
     Shortcuts folders are `.lnk` files and are now read, by the documented
     MS-SHLLINK format rather than through `IShellLink`. Quick Access, where a
     Windows user's real pins live, is a shell namespace extension over an OLE
     compound jumplist and still needs COM.
   - **Content search has no Windows half.** Linux hands the query to Baloo when
     KDE indexes; the fallback walk on both systems matches names only, so this
     is a missing *extra* rather than a regression. `SupportsContentSearch`
     correctly reports false — and is read by nothing in the UI, so neither
     platform currently tells the user which mode they are in. The Windows
     equivalent is the Search indexer, reachable only through an OLE DB provider.

   **One COM decision now gates four features**: the Trash view, the open-with
   list, Quick Access import and content search. That is worth stating as a
   single decision rather than four backlog items.

7c. **The COM decision, measured — and it was never a decision.** The claim
   above, and §7's "COM in particular needs source-generated interop or it will
   fail at runtime, not compile time", were reasoning rather than results.
   Nobody had run it.

   A spike settled it: an `IShellItem` enumeration of the Recycle Bin, declared
   with `[GeneratedComInterface]` and resolved through `StrategyBasedComWrappers`,
   works in a **published NativeAOT binary**. It returned the bin's display
   name, bound `BHID_EnumItems`, and enumerated a real deleted file with both
   its original path and its `$R` payload. Source-generated COM is exactly the
   route the doc named, and it does what the doc doubted.

   So the gate is open, and three of the four features are now ordinary work
   rather than blocked work. That is the whole value of the spike: an
   assumption held four features hostage for a release, and testing it cost an
   afternoon.

   **And then one of the three stayed blocked anyway, for a whole release.**
   The Quick access import kept a comment saying it was "waiting on the same
   COM decision as the Trash view and the open-with list" — written before this
   section existed and never re-read after it. Nothing was waiting on anything:
   the shell walk it needed took an afternoon once somebody looked. A stale
   reason is worse than no reason, because it answers the question "why is this
   not done?" convincingly enough that nobody asks again. Quick access is
   imported now; see `QuickAccess`.

   **The Trash view shipped without COM anyway, and that is not a contradiction.**
   Having proved the shell was available, it turned out to be the wrong tool for
   this particular interface. `ITrashMaintenance` wants a size and a deletion
   date per entry to run a policy sweep, and a restore that lands *beside* a
   name someone has taken back and reports where it went. The bin's own
   metadata carries the first two as plain fields; the shell's undelete verb
   decides the third for itself and reports nothing. `$I`/`$R` is the same
   shape as freedesktop's payload-plus-sidecar, so Windows satisfies the
   interface almost line for line — see `RecycleBin`.

   Measuring first is what made that judgement available. Without the spike the
   only honest options were "assume COM fails" or "assume it works"; with it,
   the choice was made on which tool fits.

   **Two things this cost that were not on the list.** `DnsQuery_W` does not
   resolve `.local` SRV records — the unicast resolver answers nothing for them,
   measured against a network of Chromecasts where every browse succeeded and
   every SRV lookup came back empty. `DnsServiceResolve`, a second callback API,
   is the only route. And a browse callback must check the record type before
   reading `Data`: the list carries SRV, TXT and A records alongside the PTRs,
   `Data` is a union, and in a TXT record the first four bytes are a string
   count — reading them as a pointer is an access violation on a DNS worker
   thread, which no `catch` will save.

---

## 8. What to verify, and how

- **`dotnet build` on Linux must still pass** after every step. The conditional
  references make it possible to break the Linux build from a Windows machine
  without noticing. CI covers this — `.github/workflows/build.yml` runs on every
  push — but see §8a for the faster loop.
- **Run the published binary on Windows**, not `dotnet run`.
- **`VAKTARI_TILE_DEBUG=1`** still works and is still the ground truth for
  whether a listing is virtualizing.
- The `[vaktari]` diagnostic lines all go to **stderr** — on Windows, run from a
  terminal or they vanish.

### 8a. Checking the other platform without waiting for CI

`-p:VaktariPlatform=` proves the *other* configuration compiles, but not that it
behaves. **WSL closes that gap on the Windows machine** and turns a push-and-wait
into about ten seconds:

```bash
wsl --install FedoraLinux-44          # matches the development distro
sudo dnf install -y dotnet-sdk-10.0 git
sudo dnf install -y fontconfig freetype dejavu-sans-fonts libX11 libICE libSM
```

**That second line is not optional if you want to RUN it**, only to build.
The WSL image is minimal and ships no fontconfig, and without it the binary
aborts in Avalonia's Skia initialisation before drawing anything — reported as
`Unable to load shared library 'libSkiaSharp'`, whose real cause is the
`libfontconfig.so.1: cannot open shared object file` line above it. BUILDING.md
§4 already lists fontconfig as a runtime requirement; a desktop distro simply
always has it. WSLg supplies the Wayland session.

**Clone inside the WSL filesystem, not `/mnt/d`.** Builds across the 9p mount are
slow and file watching is unreliable. Cloning *from* the Windows checkout is the
convenient way to carry a branch across, and git will refuse it as "dubious
ownership" until the mount is declared safe — note it wants the `.git` path, not
just the work tree:

```bash
git config --global --add safe.directory /mnt/d/git_projects/vaktari/.git
git clone /mnt/d/git_projects/vaktari ~/vaktari
```

**What this does and does not prove.** It runs the POSIX test suite and both
build configurations on real Linux, which is what §5's "Linux behaviour is
unchanged" needs. It is **not** Fedora KDE: no `kdeglobals`, no icon theme, no
`gio` or `avahi`, so theme, places and discovery fall back to built-in defaults.
It says nothing about desktop integration, and a green run here is not a
substitute for the published binary on a real desktop.

## 9. Things this document is not sure about

Stated plainly so they are not mistaken for findings:

- ~~The conditional `ProjectReference` syntax in §2 is **untested**.~~
  **Settled 3 August 2026: it works as written.** `$([System.OperatingSystem]::IsWindows())`
  and `$([System.OperatingSystem]::IsLinux())` both evaluate correctly in a
  `Condition` under the .NET 10 SDK. The MSBuild intrinsic
  `$([MSBuild]::IsOSPlatform('Windows'))` was probed alongside and also works —
  either is fine.
- ~~Whether `net10.0-windows` is worth adopting over plain `net10.0`.~~
  **Settled 4 August 2026: it stays on plain `net10.0`.** The forcing question
  was the registry, which `IThemeProvider` needs and which
  `Microsoft.Win32.Registry` only supplies in-box for a `-windows` TFM. The
  answer was to skip the BCL wrapper: `RegGetValueW` is one
  `LibraryImport` declaration, and native interop was already required for the
  Recycle Bin. Two P/Invokes cost less than a TFM change that would have taken
  the Windows project out of the Linux build and with it the free compile-check
  on every push.
- Whether Avalonia's Windows backend needs anything beyond the existing package
  references. It should not — `Avalonia.Desktop` covers all three desktop
  backends — but that is an assumption, not a verified fact.
