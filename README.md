<div align="center">

<img src="brand/icons/hicolor/scalable/apps/vaktari.svg" width="112" alt="Vaktari">

# Vaktari

**A fast, keyboard-friendly file manager for Linux and Windows.**

It reads the desktop you already have — your trash, your bookmarks, your icon
theme, your file types, whether a single click opens — rather than keeping a
second copy of all of it.

[Install](#install) · [Keyboard](#keyboard) · [Known limits](#known-limits) · [Changelog](CHANGELOG.md)

</div>

![Vaktari](docs/screenshot-grid.png)

<sub>Large grid at the root of a drive, on Windows.</sub>

---

## What it is like to use

A few things you would notice in the first ten minutes:

- **One zoom gesture covers everything.** Hold `Ctrl` and scroll, and you run
  from a dense list of names all the way up to a wall of 256-pixel thumbnails —
  the pane changes layout on the way rather than making you pick one first.
- **A search is somewhere you go**, not a popup over the folder. It has its own
  tab, its own place in Back and Forward, and it is still there after a
  restart.
- **Nothing waits its turn.** Start a second copy while the first is running,
  and cancel just the one you meant. (Pause belongs to the bar, and the bar
  follows whichever transfer you started last.)
- **One failure does not end the batch.** The rest goes through, and a *Retry
  3* button goes again on only the three that did not.
- **It tells you the truth.** When a search has no index behind it, it says so.
  When a limit is reached, it says that is a limit rather than an answer.
- **Nothing to install alongside it.** The published builds are self-contained
  — no runtime, no framework, no extra downloads.

---

## Getting around

**Tabs, splits and windows.** Open as many tabs as you like — drag to reorder
them, middle-click to close one, double-click the empty strip for a new one.
Right-click a tab for *Duplicate*, *Close other tabs*, *Close tabs to the
right* and *Reopen closed tab*; each side of the window remembers its last ten.
`F3` splits the window in two, each half with its own tabs, history, selection
and zoom, and `Tab` moves between them. `Ctrl+N` opens a whole second window on
the folder you are in. Every window is a peer, and all of them come back when
you next launch.

**A path bar that goes sideways and downwards.** Click any part of the
breadcrumb to jump there, or press the separator after a folder name to list
that folder's own subfolders and go straight into one. Every crumb has a menu,
including *This PC* at the very front — so the machine's other drive is one
press away without a trip to the sidebar. When the window is too narrow for the
whole path, the `…` that replaces the middle is itself a menu of exactly the
folders it stands for.

**Or type it.** `Ctrl+L` (or `Alt+D`) puts the cursor in the path. A list of
the folders you could mean drops down as you type; `Tab` completes shell-style.
Relative paths (`..`, `src`, `../sibling`) count from the folder on screen, and
`%ProgramFiles%`, `%SystemDrive%`, `~`, `$HOME` and `%Documents%` and its
neighbours are expanded. Type a path to a *file* and it opens that file,
landing you on its folder with the row lit.

**Back and forward remember the whole walk.** Right-click either chevron for a
list of where it goes, nearest first, twelve deep — and picking a row several
steps back leaves both buttons exactly as that many presses would have. A third
chevron beside them lists the folders you have recently been in, which reaches
across tabs and windows as Back and Forward cannot — those two belong to one
tab's own walk, though each tab's stacks are saved with the session and come
back with it.

If your mouse has the two buttons under the thumb, they go back and forward
too. In a split, they move whichever half the pointer is over.

**Backspace is yours to choose.** Out of the box it goes back, the way Explorer
does; Settings ▸ Navigation makes it go up to the parent instead, the way
Dolphin does. `Alt+←` and `Alt+↑` keep doing their own jobs either way, and the
`F1` sheet prints whichever one Backspace is currently doing.

**There is somewhere above a drive.** *This PC* (*This computer* on Linux) is a
real listing of your drives and their capacities — reachable by Up from the top
of a drive, from a crumb above every path, from the sidebar, or by typing its
name into the path bar.

**Type to jump.** Start typing in any listing and the selection moves to the
first matching name. Letters accumulate, so d-o-c finds Documents; press the
same letter again to cycle through the names that start with it.

**`F6` moves the keyboard** between the listing, the path bar and the sidebar
in turn — and `F1` lists every key the window answers, cross-checked against
the real bindings by a test so it cannot fall behind the application.

## Seeing your files

**Three layouts** — *List*, *Small grid* and *Large grid* — one click each on
the toolbar, or `Ctrl+Shift+1`, `2`, `3`. (`F8` flips between List and Large
grid.) The choice belongs to the pane, so the two halves of a split can differ.

Sizes are the pane's too. `Ctrl` and the wheel scale the pane under the
pointer, `Ctrl+Shift` and the wheel its icons alone, and `Ctrl`+middle-click
puts it back. If you would rather type a number than find it by scrolling, the
view-options menu has *Text size* and *Icon size* as steppers, with a chooser
above them saying whether they act on the left pane, the right one or both.

**The List layout chooses its columns.** Name, Type, Size, Modified and
Created, with Type and Created off until you ask for them; right-click the
headings or use *Arrange ▸ Columns*. All five sort. Clicking Size, Modified or
Created starts descending, so the download that just finished is at the top.
`file2` sorts before `file10`, and *Écoles* sorts beside *Ecoles* rather than
after *Zebra*. Sorting folders before files is a switch you can turn off, which
is what finally lets "sort by Modified" answer *what changed here* when the
answer is a folder.

**Folders open where they stand.** In the List layout, press the triangle on a
folder row — or `→` with the row selected — and its contents appear underneath
it, indented, without the listing moving. `←` closes it again. What you opened
survives a sort, a refresh, a rename and a paste.

**Grouping** by name, size, type or date, from *Arrange ▸ Group by*. Each
band's heading says how many rows are under it — TXT (12) — and clicking a
heading selects the whole band. This is a List-layout feature; the grids draw
no bands.

**Thumbnails** for pictures, and more besides. On Windows they come from the
same store Explorer draws from, so a video shows a frame and a PDF shows its
first page — whatever a handler on your machine can render. On Linux, Vaktari
decodes images itself and reads whatever your desktop's own thumbnailers have
already put in the shared cache. Either way, a picture too small to enlarge
cleanly keeps its icon rather than being blurred up, and there are size limits
you can set — with a separate one for network shares, since a thumbnail there
pulls the whole file across.

**Two names the eye cannot tell apart get marked.** `Ember Setup 0.1.0 .exe`
beside `Ember Setup 0.1.0.exe` differ by one space — legal, distinct to the
filesystem, and invisible in any listing including Explorer's. Vaktari labels
them *Look-alike*. Whitespace and case only, so an unusual name is not flagged
merely for being unusual.

**Extensions can be hidden, but not dangerously.** Turn *Show file name
extensions* off and `notes.txt` draws as `notes` — except for endings that mean
something *happens* when you open the row. A program, script, installer or
launcher keeps every character, so `invoice.pdf.exe` can never be listed as the
document beside it.

**Details panel** on `F11` — a large preview, the name, size, type, dates,
where a link points, and permissions on Linux or file attributes on Windows.
Drag the edge to resize it; each side of a split keeps its own. If the window
is too narrow, Vaktari can widen it to make room and shrink it back afterwards.
`Space` gives a quicker look without opening anything.

**Small touches worth knowing.** Hidden files (`Ctrl+H`) look hidden — ghosted
rather than standing at full strength beside real content. The Modified column
is shaded by how recently a file changed. A name too long for its column is cut
in the *middle*, so the extension survives. Folder rows can count what is
inside them. Tick boxes on rows are available if you want them, off by default.

**Folders can remember how you left them** — layout, sort, grouping, hidden
files, which columns were ticked and both zoom levels. It is off by default and
lives in Settings ▸ General, and the record is kept centrally rather than
written into your folders. A `.directory` file Dolphin already left in a folder
is still read, so a folder somebody configured elsewhere opens the way they
meant. *Use this view for all folders*, in the view-options menu, goes the
other way: it makes the pane you are looking at the way folders open from now
on, and forgets the views individual folders were given.

## Finding things

**`Ctrl+F` opens the search field**; Enter asks the question. Nothing runs
while you type, so there is no pause to type through. Results are ordinary rows
in an ordinary listing — select five of them, drag one out, rename in place,
sort by size, or open a second search beside the first in a split.

A band above the results says what was asked and where it looked, with a tick
box reading *Only in Documents*, on by default for a search started in a
folder. Clear it and the search leaves that folder. Ticking or clearing it
counts as a navigation rather than an edit in place, so Back takes you to the
previous question instead of making you retype it. Nothing is cached between
searches, so it is asked again. On Windows a *Match case* box sits beside it.

**It names what "everywhere" actually covers** — "searching every drive on this
machine" on Windows, "searching your home folder and any mounted drives" on
Linux. Network drives are deliberately left out of an unscoped search, because
a mapped drive whose server has gone away would block for the whole timeout; to
search a share, open it and tick *Only in …*.

Results appear as they are found, with a progress bar and a *Stop* that keeps
what it already has. A search that hits its ceiling says so — "stopped after
the first 10,000 matches — there are more" — with a *Keep looking* button that
is honest that nothing resumes and the walk starts again.

**When nothing is indexing the search, it says so**: "every folder in Documents
is read in turn — there is no index on this machine". That is true of every
search on Windows. On Linux the query goes to Baloo where KDE is indexing, and
falls back to reading folders when Baloo is absent, switched off, or answers
nothing at all.

**Right-click the magnifier for the searches you have run.** Choosing one asks
it again exactly as it was asked — the same words, the same folder, the same
answer about capitals — because what is kept is the whole search rather than
the words in it. Twelve are offered, fifty are kept, and it can be switched off
and emptied from Settings.

**Filter the listing you are looking at** with `Ctrl+I`, and nothing on screen
moves. `*` and `?` work as patterns here too, so `*.png` hides everything else;
the count underneath reads "filtered to 2 of 3"; and a filter that matches
nothing says so rather than claiming the folder is empty. `Enter` or `↓` hands
the keyboard to the rows and keeps the filter; `Escape` clears the text, and
again puts the box away.

**Recent files** and **recent locations** sit in the sidebar and open like any
other listing, each row carrying the folder it came from. Anything can be
forgotten from the right-click menu — *Forget (keeps the file)* — and the whole
record can be switched off. Only what you opened yourself is recorded: Back,
Forward, Refresh and a restored session are not choices about where to go.

**`Ctrl+D` pins the current folder** to the sidebar; right-clicking a pinned
place renames or removes it. Home, your drives and your network shares are the
desktop's rather than yours to drop — but a removable drive offers *Eject*, and
a mapped network drive offers *Disconnect*, which also forgets the sign-in so
it does not come back on its own.

## Working with files

Copy, cut, paste, rename, duplicate and delete behave as you expect. What is
worth knowing is what happens when things get big, or go wrong.

**While a copy runs**, the status bar shows a progress bar, how fast it is
going and how much longer it has — "10 MiB/s · about 4 min left". The speed is
measured over the last few seconds, so a copy that crosses from an SSD onto a
memory stick stops promising a speed it will never see again. A copy checks
there is room before it starts, keeps the file's dates — and its permissions on
Linux, its attributes on Windows — and leaves no half-written file under the
final name if it is cancelled.

**Nothing queues, and nothing is all-or-nothing.** An *Operations* button opens
the list of everything in flight — "Copying 12 items to Photos" — each row with
its own *Cancel*. A transfer can be paused and resumed as well, stopping
between files rather than finishing the one it is on. If a file cannot be
copied or deleted, the rest still goes through: the line names the first one
left behind and counts the others, *Details* lists every one with its reason
and full path, and *Retry 3* goes again on only those three. Where the refusal
was permissions, a second button appears — *Retry 3 as administrator* — and the
system puts up its own consent prompt. It never overwrites: a name already
taken is left alone and counted as still not done.

**Undo and redo say what they will do.** *Undo copy of 3 items*, *Redo move of
readme.txt* — menu rows rather than a key you press to find out. `Ctrl+Z` takes
back a paste, a move, a rename, a batch rename, a new folder or file, a
compress or extract, a shortcut you made, and a delete to the bin on either
platform. Renaming forty photos is one press of `Ctrl+Z`, not forty.

**When two files clash**, the prompt shows both — size and date — and says
which is newer, or that they look like the same file. For a folder arriving on
a folder the button reads *Merge*, with a line saying what merging keeps. *Keep
both* is the default, and *Do the same for the rest* starts unticked. What you
pasted comes back selected, under the names the files actually landed with, so
a *Keep both* arrival is picked out under its new name.

**Dragging follows Explorer's rules** — plain drag moves within a drive and
copies between drives, `Ctrl` copies, `Shift` moves, `Alt` or `Ctrl+Shift`
leaves a shortcut. A small label follows the pointer naming what you are
carrying, so a drag begun by accident does not look like the drag of twenty
files you meant. The folder under the pointer takes a ring; files can also be
dropped on another tab, which pauses and then switches, on a breadcrumb to move
them up the tree, or on the bin. A folder cannot be dropped into itself by any
route. Drag with the *right* button instead and the drop asks. On Windows a
drag straight out of 7-Zip or Explorer's own zip view lands too, even though
those files do not exist on disk until they are dropped. *Copy to* and *Move
to* send a selection somewhere without opening it first.

**`F2` renames on the row**, in any layout, with the extension left off the
offer. `Enter` commits, `Escape` cancels, and `Tab` commits and opens the
*next* file's name — so renaming a run of files costs one keystroke each. A
name the filesystem will not accept is explained as you type it, and what you
typed is kept. `Shift+F2` renames in bulk, with a live preview of every old
name and what it will become: numbered (each run of `#` becomes a zero-padded
counter) or find-and-replace, optionally a regular expression.

**The bin can restore.** Deleted files go to your desktop's own bin and appear
in Vaktari's bin view, each row showing where it came from — so *Restore* puts
it back where it belongs, beside a name that has since been taken rather than
over it. A single item can be thrown out with *Delete permanently* without
emptying the lot. Vaktari can also sweep it for you: delete anything older than
a number of days, and keep it under a share of the disk. Because a bin row
names where a file *used to be*, opening, renaming or dragging one is refused
rather than acting on whatever sits there now.

**Confirmations name what they are about to destroy** — "permanently delete
report.pdf? this cannot be undone" rather than "1 item(s)". A very long name is
shortened in the middle so the extension survives, since `.pdf` against `.exe`
is the part that changes what deleting it means. Each confirmation can be
switched off.

**Also on the right-click menu:** *Mount* for a disk image, which attaches it
and takes you inside, and *Unmount* when you are done. *New folder*, *New file*
and *New from template*, each opening straight into the rename box. *Compress
to ZIP* and *Extract all* — Vaktari's own, undoable, written beside what they
act on, and refusing any archive entry that points outside the folder it is
landing in. *Create shortcut*, made the way each platform makes them. *Open
with*, reading your system's own file-type database. *Run* and *Run as
administrator* for a program. *Open terminal here* on `F4`, with *Open admin
terminal here* beside it. Entries that need a selection are simply not offered
when there is none, and you can switch off the ones you never use.

On Linux, double-clicking a program asks before running it — *Run*, *Open* or
*Cancel* — because double-click has meant "open this" everywhere else and a
script is often a file you want to read. A file counts as a program only if it
carries an execute bit *and* its first bytes say so, so a FAT stick that
reports an execute bit on every file does not offer to run your photographs.

**Properties** counts a folder's size the moment you open it — that being the
figure people open the window for — and says which volume it is on and how much
of that is free. (A folder on a network share waits to be asked instead, since
measuring means a round trip per directory over SMB or SFTP.) It will compute
MD5, SHA-1 and SHA-256 on request. On Linux it edits permissions as nine tick
boxes, and owner and group as choosers, each live only where the change would
actually be allowed. On Windows, Properties for a single item opens Windows'
own sheet instead — the one with Security, Details and the Unblock checkbox —
while Vaktari's own window answers a multi-item selection, reading *mixed*
where the files disagree.

**Scripts.** Drop a script in Vaktari's scripts folder and it appears in the
right-click menu, receiving the current folder and the selection.

## Sharing and the network

**Connect to a server** and it appears in the sidebar and browses like a local
folder. On Linux that is `smb://`, `sftp://`, `ftp://` and `dav://` through
your desktop's own mounter; on Windows it is `\\server\share`, `smb://` and
`http://` for WebDAV, and Windows asks for a password in its own dialog and
remembers it.

**Scan the network** for shares announcing themselves — a NAS, another desktop,
a Vaktari share on another machine — without typing addresses. On Linux this
needs `avahi-browse` on the machine (the `avahi-tools` package on Fedora);
without it the button stays greyed out.

**Share a folder over HTTP** for another machine to fetch, with optional
upload. This uses [copyparty](https://github.com/9001/copyparty), which Vaktari
can fetch for you.

**Share by link.** For anything inside your Proton Drive folder, right-click ▸
*Share* offers *Share via Proton Drive*: the link lands on your clipboard,
ready to hand to someone. Unlike an HTTP share it outlives the application and
crosses the internet. Vaktari drives Proton's own tool rather than
reimplementing any of it, and the first click does whatever is missing in order
— fetches the tool, opens your browser to sign in, then makes the link.

**Every live share is listed.** A *Sharing* section appears in the sidebar
holding each HTTP share and each Proton link with its address, a button to copy
it again and one to stop it — so a share you started an hour ago is not
something you have to remember.

## Version control

Inside a git repository, files are marked with their status: **M** modified,
**A** added, **D** deleted, **?** untracked, **!** conflicted. A folder shows
the strongest state of anything inside it.

The marks appear in every layout and keep up as you work — when you edit a
file, and when you commit or switch branch. Status is read once per folder
rather than once per file, so it stays cheap on a large repository. The letters
carry the meaning and the colours are decoration, so the marks stay readable if
you cannot tell the colours apart. Needs `git` on the machine; without it the
marks simply never appear, and nothing in the interface explains why.

## Fitting your desktop

Vaktari reads what your desktop already wrote, and does not scribble on it:

| | |
|---|---|
| **Trash** | the standard desktop trash, shared with every other application |
| **File types** | your system's own file-type database |
| **Places** | imported at startup from Dolphin's and GTK bookmarks, or from Quick access, Links and Network Shortcuts |
| **Icon theme** | your themed icons on Linux, with hand-drawn fallbacks where a theme has none |
| **Single or double click** | follows your desktop setting |
| **Light or dark** | follows your desktop, or whichever you pick |
| **Interface text size** | follows your desktop's own, or set it yourself |

Change your icon theme and Vaktari changes with it. Nothing needs restarting.

**Icon themes work on both platforms.** Point Vaktari at any freedesktop theme
folder, install a `.tar.gz`, `.tar.xz` or `.zip` you downloaded, or have it
fetch Papirus for you — about 110 MB, GPL-3.0, light and dark variants
included. Letting Vaktari unpack it matters most on Windows: these themes are
built out of tens of thousands of symbolic links, which Windows will not create
without Developer Mode. Unpacked inside Vaktari the links are *read* rather
than made, so nothing fails and nothing is duplicated on disk. Windows can also
be told to use the icons Windows itself draws, so a program shows its own icon
and a shortcut carries its arrow.

**Colour and typeface are a deliberate exception.** The bundled scheme is the
default, because a file manager that repaints itself to match your desktop the
first time you launch it is a surprise rather than a courtesy. Turn on *Follow
desktop colours* and your scheme, accent and interface font are layered over it
instead. Light or dark is a separate choice in the same place — *Follow the
desktop*, *Light* or *Dark* — and the bundled scheme is drawn for both, so
neither is an inversion of the other. Sizes and dates keep the monospaced face
either way, so figures line up down a column.

**Vaktari can become the program that opens folders**, from Settings — so
double-clicking a folder anywhere opens it here, as a tab in the window you
already have rather than a second copy of the application. Pass it a file and
it opens that file's folder, which is what makes `vaktari
~/Downloads/thing.zip` useful from a script or a launcher.

One thing it cannot do, and no file manager on Windows can without replacing
parts of the shell: **"Show in folder" from Chrome, Edge and Firefox always
opens Explorer**, as do `Win+E` and the taskbar's File Explorer pin. Those call
a Windows function that opens an Explorer window directly rather than asking
the system what should open a folder. On Linux there is no such problem —
Vaktari answers the standard interface those buttons use.

**On Windows the right-click menu hosts the machine's own** — 7-Zip, VLC, *Send
to*, *Restore previous versions*, whatever your programs registered. It is the
last row, called *Windows menu*, exactly where Windows 11 puts *Show more
options*. It is built only when you open it, so an ordinary right-click never
pays for other people's code.

**Where Vaktari opens is yours to choose** — last session's folders, tabs and
windows (the default), your home folder, *This PC*, or a folder you browse for.
The same page decides how it opens: straight into a split, with the filter bar
showing, with the path bar already editable, or with the full path in the title
bar.

**Everything else is one dialog**, on `Ctrl+Shift+,`: sorting, what a click
does, previews and their size limits, confirmations, the status bar, which
entries appear in the right-click menu, extensions and selection boxes,
per-layout spacing, date style, the font and text size, light or dark,
version-control marks, per-folder view memory, the details panel, and how the
bin is swept. It can also show you the settings file itself, save a copy of it,
put one back from another machine, and restore every setting to its default.

## Keyboard

| | | | |
|---|---|---|---|
| `Enter` | open | `Ctrl+C` `Ctrl+X` `Ctrl+V` | copy, cut, paste |
| `Backspace` | back, or up — a setting | `Ctrl+Shift+C` | copy as path |
| `Alt+←` `Alt+→` | back, forward | `Delete` | move to the bin |
| `Alt+↑` | up one folder | `Shift+Delete` | delete for good |
| `Alt+Home` | home folder | `Ctrl+Z` `Ctrl+Y` | undo, redo |
| `Ctrl+L` `Alt+D` | type a path | `F2` | rename |
| `Ctrl+A` | select everything | `Shift+F2` | rename in bulk |
| `Ctrl+Shift+A` | invert the selection | `Ctrl+Shift+N` | new folder |
| `Ctrl+T` `Ctrl+W` | new tab, close tab | `Alt+Enter` | properties |
| `Ctrl+Shift+T` | reopen the last closed tab | `F4` | terminal here |
| `Ctrl+1`…`Ctrl+9` | jump to a tab | `F5` | refresh |
| `Ctrl+Tab` `Ctrl+Shift+Tab` | next, previous tab | `Space` | quick preview |
| `Ctrl+N` `Ctrl+Q` | new window, close window | `Ctrl+H` | show hidden files |
| `F3` / `Tab` | split view / switch side | `Ctrl+Shift+1` `2` `3` | list, small grid, large grid |
| `F6` | listing, path bar, sidebar | `F8` | change the view |
| `F9` `Ctrl+B` | show or hide the sidebar | `Ctrl` `+` `−` `0` | zoom in, out, reset |
| `Ctrl`+scroll | resize the pane under the pointer | `Ctrl+Shift`+scroll | its icons only |
| `F11` | details panel | `Ctrl+D` | pin this folder to places |
| `Ctrl+F` `Ctrl+E` | search | `→` `←` | open and close a folder in place |
| `Ctrl+I` | filter the listing | `Menu` `Shift+F10` | the right-click menu |
| `Escape` | clear the filter | `Ctrl+Shift+,` | settings |
| `F1` | every key, in the app | | |

`F1` is the authority — a test checks it against the real bindings in both
directions, and it prints whichever job `Backspace` is currently doing.

## Install

Everything is on the
[releases page](https://github.com/dkflint723/vaktari/releases). Both platforms
are 64-bit Intel/AMD only.

### Linux

Download **`vaktari-linux-x64.tar.gz`** — that is the one with the installer in
it. (`vaktari-linux-x64-fedora.tar.gz` and `vaktari-linux-x64-arch.tar.gz` are
build trees the CI publishes; they contain no installer.)

```bash
tar -xzf vaktari-linux-x64.tar.gz
cd vaktari && ./install.sh
```

It installs under `~/.local`, needs no root, touches nothing outside `$HOME`,
and adds a menu entry. Fedora users can install the `.rpm` on the same page
instead — CI installs it into a clean Fedora container and runs it before
publishing, so its dependency list is proven rather than asserted.

**Pick one or the other.** `~/.local/bin` comes before `/usr/bin` on most
systems, so a copy installed by hand keeps running even after you upgrade the
package. `vaktari --version` prints the version *and* the file it came from,
which is the quickest way to tell which one you have.

Your tabs, places, folder views and settings live in `~/.local/state/vaktari`.
There is no uninstaller for the tarball — removing it by hand means deleting
`~/.local/bin/vaktari`, `~/.local/lib/vaktari` and
`~/.local/share/applications/vaktari.desktop` and the icons under
`~/.local/share/icons/hicolor/*/apps/vaktari*`.

### Windows

Run `vaktari-<version>-win-x64-setup.exe`. It installs for your account only,
so no administrator and no UAC prompt, and it removes from *Installed apps*
like anything else. The first page offers *Install for all users* if you would
rather have it on the machine than the account.

The installer is **not code-signed**, so the first run shows SmartScreen's
"Windows protected your PC" — *More info* ▸ *Run anyway*. The installer will
also stop rather than overwrite a copy of Vaktari that is currently running.

Uninstalling leaves your tabs, places, folder views, settings, recents and your
own `scripts\` folder alone, under `%LOCALAPPDATA%\vaktari`, so reinstalling or
upgrading picks up where you left off. Delete that folder by hand if you want
them gone — but note what is in it first.

### Building it yourself

You need the .NET 10 SDK:

```bash
git clone https://github.com/dkflint723/vaktari.git
cd vaktari
dotnet run --project src/Vaktari.Ui
```

Release builds, other distributions and packaging are in
[BUILDING.md](BUILDING.md).

## Known limits

Vaktari is used daily by its author, but **there has been no stable release** —
the current version is 0.10.1 and version numbers are not a compatibility
promise yet. Worth knowing before you decide:

**Platform**

- 64-bit Intel/AMD only, on both platforms. No ARM build, and no macOS build.
- On Linux, following the desktop only works on **KDE Plasma**. Colours,
  accent, the interface font, the text size and the single-click preference are
  all read from `kdeglobals`; on GNOME, Xfce or Cinnamon, "follow the desktop"
  for light/dark resolves to dark. The icon theme chooser and everything else
  in Settings work everywhere.
- Fedora is the only distribution with a real package. There is no `.deb`, PPA,
  Flatpak, Snap or AppImage; everyone else uses the tarball. That tarball links
  against the glibc of GitHub's current Ubuntu runner, so it will not start on
  a long-term-support distribution several years older.
- The interface is **English only**.
- Vaktari never checks for updates. Upgrading means going back to the releases
  page.

**Searching**

- **Nothing in the interface offers to search inside files** — there is no
  contents box anywhere. On Windows that is the whole story: search matches
  names. On Linux it is not, because a plain word goes to Baloo, whose index is
  full-text, so a result there may be a file matched on its contents rather
  than its name. Patterns are names only: `*` and `?` go past Baloo to a
  filename walk on both platforms. No regex, and no searching by size, date or
  type.
- **Windows has no search index at all.** Every search is a live directory
  walk, capped at 10,000 matches, so an unscoped search over every drive is
  slow and the answer is a shallow slice rather than a complete one. On Linux,
  Baloo answers where KDE is indexing.
- *Match case* is Windows-only — it is the only backend that honours it.
- Searches cannot be saved. The history offers the last twelve to run again;
  there is no named saved search.
- Recent files and locations are Vaktari's own record. Files you opened in
  other applications do not appear.

**Files and views**

- Compress writes ZIP and nothing else, and *Extract all* only opens `.zip`.
- Permissions, owner and group can only be edited on **Linux**. On Windows that
  belongs to the Security tab of Windows' own sheet.
- Checksums are effectively Linux-only: on Windows, Properties for a single
  item hands off to Windows' own sheet, and Vaktari's checksum panel is on the
  window that only appears for multi-item selections.
- There is **no folder tree** in the sidebar. Expandable folders in the List
  layout are the substitute, and they are List-only — as is grouping.
- Split view is exactly two panes, side by side. No third pane, no over/under,
  and closing always keeps the left.
- A tab can be dragged within its own strip, but not to the other half of a
  split, to another window, or off into a new one.
- Columns cannot be resized or reordered by hand.
- The Small grid draws no thumbnails, and on Linux Vaktari does not *generate*
  video or PDF thumbnails — it only reads ones your desktop's thumbnailers
  already made.
- **Keyboard shortcuts cannot be rebound.** `F1` shows the list; nothing
  changes it.
- Nothing queues: every transfer you start runs at once.
- Places are re-imported from your desktop at every startup, so a place you
  remove in Vaktari that still exists in Dolphin's or Explorer's own list will
  come back.
- Inside a git submodule or a linked worktree, the version-control marks wait
  for `F5` after a commit rather than updating on their own.
- Accessibility is partial: the main window carries automation names, the
  Settings dialog does not.

Bugs and ideas are welcome on the
[issue tracker](https://github.com/dkflint723/vaktari/issues).

## Licence

MIT — see [LICENSE](LICENSE).

Built with [Avalonia](https://avaloniaui.net). Published binaries include
SkiaSharp, HarfBuzzSharp and the Inter typeface; their licences are in
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which ships inside every
tarball, installer and package.
