# Changelog

What changed in each release, from the point of view of someone using Vaktari.
Entries describe behaviour, not commits — if a change is invisible from the
outside, it belongs in the git history rather than here.

Newest first. Dates are the day the tag was cut. Versions follow
[semantic versioning](https://semver.org), with the caveat in
[Status](README.md#status): there has been no stable release, and the numbers
should not be trusted for compatibility yet.

## [0.9.13] — 2026-08-29

### Added

- **Share a file or folder by Proton Drive link.** For anything inside your
  Proton Drive folder (point Settings at it), right-click → Share offers
  *Share via Proton Drive* — the link lands on the clipboard, ready to hand to
  someone. The same submenu copies or stops an existing link, and every live
  link is listed in the sidebar's sharing section beside the copyparty
  shares, with the same ✕ to end it from there. Needs the
  [Proton Drive CLI](https://proton.me/support/drive-cli) on the machine; when
  it isn't signed in, Vaktari opens Proton's sign-in page in your browser and
  finishes the share once you have — your password never passes through
  Vaktari.

### Changed

- **The right-click menu is regrouped around what people actually reach
  for.** *Run as administrator* now shows for any executable on a plain
  right-click — only the admin terminal still wants Shift, which is Explorer's
  own convention for extended verbs. *Open in new tab* sits at the top instead
  of under "More". One flat *Share* submenu lists the ways to share, with no
  submenu behind it. The four transfer rows folded into *Copy to* / *Move to*,
  where the other pane leads the targets whenever the window is split. And
  "More" is gone: Scripts and *Add to places* stand under their own names, one
  visible row each.

- **The hosted Windows entries stopped repeating the menu above them.** The
  submenu is now called *Windows menu* — named for what it holds — and it no
  longer re-lists Open, Open with, Cut, Copy, Paste, Delete, Rename,
  Properties, Copy as path or the Windows share sheet, all of which already
  sit in Vaktari's own menu with their shortcuts beside them. What only
  Windows can offer — 7-Zip, Send to, Create shortcut, Restore previous
  versions and whatever else is installed — stays.

## [0.9.12] — 2026-08-28

### Added

- **Dragging with the right mouse button asks what to do.** Release it over a
  destination and a menu offers *Move here*, *Copy here*, *Create shortcuts
  here* and *Cancel*, with what a plain drop would have done shown in bold —
  Explorer's oldest answer to "did I just move that or copy it". Closing the
  menu without choosing does nothing.

- **Ctrl+Shift+drag creates shortcuts** — real `.lnk` files on Windows, named
  the way Explorer names them; symbolic links on Linux. Works for a whole
  selection at once, and both gestures skip files that live inside an archive's
  temporary folder, where a shortcut would break within the second.

### Fixed

- **The look-alike mark now actually appears — and in every view.** It shipped
  in 0.9.7 and never rendered once: the set of colliding names was only
  computed while a filter was being typed, and an ordinary folder load never
  goes down that path. It now appears on any plain navigation, updates as files
  arrive and leave, and shows in the details, compact and icon views alike —
  the tiles carry a small ≈ badge where the rows carry the word.

## [0.9.11] — 2026-08-24

### Added

- **The tab strip shows arrows when tabs overflow.** The wheel scrolls the tabs
  and the thin line shows where you are, but neither looks clickable — so the
  strip now grows a small arrow at each end the moment there are more tabs than
  fit. Click to step, hold to sweep; each arrow dims at its own end of the
  strip, and both disappear when everything fits. Switching to a tab that had
  scrolled out of view now also brings it into view.

### Changed

- **Dragging a large archive out of 7-Zip no longer pauses the window.** The
  files 7-Zip hands over have to be taken before it deletes them, and taking
  them used to mean copying — for as long as the archive was large. They are
  now moved instead, which costs nothing regardless of size.

- **Icon caches tidy themselves.** The index kept for a chosen icon theme —
  sixteen megabytes for a big one — now goes away when its theme does, instead
  of accumulating forever.

### Fixed

- **Navigating to the folder you are already in leaves it alone.** Spelled with
  a trailing separator or different capitals, it reloaded the listing and added
  a Back entry that went nowhere.

- **Linux: "Keep both" on a folder with a dot in its name keeps the whole
  name** — `my.photos` became `my.photos (1)` rather than `my (1).photos`.

- **The Properties window stops working when closed.** Its folder measurement
  and checksum used to keep reading the disk after the window was gone.

## [0.9.10] — 2026-08-23

### Fixed

- **Dragging files out of 7-Zip works.** It failed every time, naming a file it
  had just been given. 7-Zip does not hand over an archive's contents — it
  extracts them to a temporary folder of its own and deletes that folder the
  instant the drop finishes, which was before Vaktari had finished copying.
  Vaktari now takes the files while the drop is still happening.

- **The path box completes Windows paths.** Pressing Tab did nothing for a path
  like `%LOCALAPPDATA%\GOG.com\Galaxy`, while pressing Enter went straight
  there. Completion understood neither `%VARIABLES%` nor backslashes, so on
  Windows it silently offered nothing for any path at all.

- **A folder keeps updating itself when its path ends in a separator.** Files
  appearing, disappearing or being renamed by other programs went unnoticed
  until F5 — and completing a path with Tab produced exactly such a path.

- **Files other programs create are shown, and hidden ones stay hidden.** On
  Windows a dotfile written by another program did not appear until F5, renaming
  something to a dotted name made its row vanish, and hidden files that Word and
  Office leave behind while a document is open were shown when they should not
  have been.

- **Emptying the Recycle Bin no longer part-destroys read-only items.** A
  recycled folder holding read-only files — a cloned git repository is the
  common case — was emptied as far as the first one, then stopped, leaving the
  entry listed at its original size and restoring only the remains. Vaktari
  reported removing nothing. Such an item could never be removed at all, so
  emptying never finished the job.

- **Linux: "Keep both" on a folder puts the contents in the new folder.** They
  went into the folder you asked to keep separate, leaving the new one empty —
  and on a move, that emptied the original.

- **Linux: undo puts back what actually moved.** After answering "Keep both" or
  "Skip", undo moved the wrong file — including files that were never part of
  what you did, and the very file you had chosen to leave alone.

- **Linux: `F4` no longer closes Vaktari** when the terminal it tries first
  refuses to start. It now tries the others.

- **Linux: search says "no results" only when there are none.** With KDE's
  search tool installed but no index built, every search reported nothing found
  rather than looking through the folder.

- **A folder you have no permission to open says so** instead of showing as
  empty. Both platforms.

- **Copying or moving a folder on Linux no longer follows the links inside it.**
  A link to another folder was treated as that folder: copying duplicated
  everything behind it, and *moving* emptied it.

- **Two file operations at once keep their progress bar.** Starting a second one
  hid the bar for the first and left Cancel doing nothing while it was still
  running.

- **Dragging to another drive on Linux copies rather than moves.** Every path
  there shares the root "/", so the "same drive?" test always said yes and a
  plain drag to a USB stick or a network share moved the files.

- **Grouping by kind shows the whole extension.** It showed "XT" for .txt and
  "S" for .cs, and filed every single-letter extension under "No extension".

- **Batch rename reports what it could not do.** Failures were counted as
  successes, so a rename that changed nothing said it had changed everything.
  It also now spots a name already taken by a file that is not part of the
  batch, which the preview never checked.

- **A cut in Explorer pastes as a move**, and a cut in Vaktari moves when pasted
  in Explorer. Windows' own cut marker was neither read nor written.

- **Restoring from the Recycle Bin names things properly.** A second copy of a
  folder called `my.project` came back as `my (1).project`, and a second
  `.bashrc` as ` (1).bashrc`.

- **Emptying the Recycle Bin and the recents and bin listings no longer freeze
  the window** while they work.

- **Applying permissions down a tree on Linux says how much it could not
  change**, rather than reporting success over a tree that refused everything.

- **A failed network mount on Linux says why**, in the words of the tool that
  tried, instead of always blaming credentials.

- **The preview pane cannot be overwritten by a slower, older preview** when you
  move through files quickly.

### Changed

- **The tab strip's scrollbar is a thin line** at the bottom edge instead of a
  full-height bar with arrows painted across the tab names, and **the mouse
  wheel now scrolls the tabs**, which it never did before.

## [0.9.9] — 2026-08-23

### Changed

- **Choosing an icon theme no longer freezes the Settings window.** Browsing to
  a theme folder checked it there and then, and checking means reading the whole
  theme — around three seconds for a large one, with the dialog locked and
  nothing on screen to say why. It now reads in the background and says
  *Reading that theme…* while it does, and what it reads is kept, so the next
  launch opens with those icons already in place.

- **Opening Settings no longer stops to re-read the theme you already chose.**
  The check is there to notice a theme folder that has been moved or deleted,
  and noticing that takes no time at all. Only the deeper question — whether the
  folder still works as a theme — now waits behind the dialog, and it speaks up
  only if the answer turns out to be no. When it does, it names the likely
  cause: a theme keeps most of its icons as links to another theme, and that
  other theme having been removed is what usually breaks it.

## [0.9.8] — 2026-08-23

### Changed

- **Vaktari opens roughly six times faster when you use an icon theme.**
  Choosing one made every launch read the whole theme before the window was
  allowed to appear — and a theme is not a small thing: Papirus-Dark falls back
  to Papirus behind it, a quarter of a gigabyte across some fifty thousand
  files. Measured on the machine that reported it, reading them took 2.8–3.1
  seconds while every icon lookup afterwards took none at all, and a launch that
  should have taken 300 ms took 1,750.

  The reading is now remembered between launches, so an ordinary start pays
  about twenty milliseconds for it and the icons are right from the first frame.
  Only the first launch after choosing a theme, or after replacing one, reads it
  the long way — and that one no longer blocks the window: it opens on your
  desktop's own icons and changes to the theme once, a moment later.

  If a theme's folder is replaced or edited, what was remembered is discarded
  and read again, so an updated theme shows up rather than the old one.

## [0.9.7] — 2026-08-17

### Added

- **The size controls say which pane they change.** The menu holding them lives
  on the rightmost pane, and opening it makes that side active — so *this pane*
  could only ever mean the right one, and the left half of a split could not be
  sized at all except with the wheel. There is now a *left / right / both*
  choice above them, shown only when the window is split. `Ctrl+0` still resets
  whichever pane you are working in, whatever the menu is set to.

- **`F1` lists every keyboard shortcut.** Vaktari calls itself keyboard-friendly
  and had nowhere to look them up: a shortcut showed beside a right-click entry
  when it happened to have one, and the rest — the filter, the view, redo, the
  two spellings of the path bar — were not findable at all. A test checks the
  list against the real bindings so it cannot fall behind, and it caught a wrong
  key the first time it ran.

- **Names that differ only by spacing or capitals are marked** in the details
  view. Two files really can sit side by side looking identically named — one
  space before an extension is legal, distinct, and unreadable in any listing,
  including Explorer's.

### Changed

- **Vaktari no longer answers in .NET.** A folder it could not open used to
  report `UnauthorizedAccessException: Access to the path ... is denied.` while
  the listing behind it — from the very same place in the code — said "you do
  not have permission to open this folder". The plain sentence is now used
  everywhere: a full disk, a file another program has open, a missing folder and
  a refused one all say so in words.

- **A name typed while renaming is tidied**, the way Explorer tidies it. Windows
  discards a trailing space or dot anyway, so a name typed with one asked for a
  file and got a different one.

## [0.9.6] — 2026-08-17

### Added

- **Dragging files straight out of 7-Zip now works**, and out of Explorer's own
  zip view. Those files have no location on disk until something asks for their
  contents, which is why the drag appeared to do nothing — Vaktari now asks,
  writes them out, and moves them where you dropped them.

  If a future Avalonia closes the route this depends on, the drop goes back to
  explaining that the files are inside an archive rather than failing.

## [0.9.5] — 2026-08-15

### Added

- **Middle click opens a folder in a new tab, and closes a tab.** What every
  browser does, and what Explorer does now that it has tabs.

- **Drop files onto a place in the sidebar.** Downloads, a drive, anything
  pinned. The sidebar accepted nothing at all before, so a drag simply died over
  it.

- **Redo, on `Ctrl+Y` and `Ctrl+Shift+Z`.** Undo existed and redo did not,
  which is half an undo: a move reversed by mistake could only be done again by
  hand. Any new work abandons it, as it does everywhere — once the history has
  been departed from, putting something back would apply to a state that no
  longer exists. Restoring from the bin cannot be redone: the trash entry it
  came from is gone, so re-trashing would not be the same act.

- **Cut files are greyed until they are pasted**, the way Explorer greys them.
  A cut used to look exactly like nothing having happened. A copy, a paste or
  `Escape` clears the marks.

- **`Alt+D` and `Ctrl+E`** alongside `Ctrl+L` and `Ctrl+F`. Which one somebody
  reaches for depends on where they learned it; Explorer answers both.

### Changed

- **Renaming selects the name and leaves the extension alone.** Press `F2` and
  type, and `notes.txt` no longer becomes whatever was typed with no extension.
  Folders are still selected whole, as are dotfiles like `.gitignore`.

- **`Ctrl`+dragging onto the folder a file is already in makes a copy**, which
  is how Explorer duplicates. Vaktari discarded those paths whatever key was
  held, so the gesture did nothing.

- **A plain drag now moves within a drive and copies between drives**, which is
  what Windows does. Vaktari moved for any drag that started inside the
  application, so dragging onto a place on another disk moved the file across
  volumes when Explorer would have left the original alone. Holding `Ctrl` or
  `Shift` still decides outright.

## [0.9.4] — 2026-08-15

### Added

- **You are asked before something is overwritten.** Copying or moving onto a
  file that is already there now offers *Overwrite*, *Keep both*, *Skip* or
  *Cancel*, with both files described — size, when each was changed, and which
  of the two is newer — and a *do the same for the rest* for when there are many.

  Until now every copy and move answered *keep both* on your behalf, so a newer
  file dropped over an older one silently became "name (1)" and there was no way
  to say otherwise. *Duplicate* still keeps both without asking, which is what
  it is for.

### Fixed

- **A drop that cannot be taken now says why.** Dragging files out of a zip
  opened in Explorer did nothing at all, which is indistinguishable from a drop
  that missed — Windows offers those files without any location on disk, and
  Vaktari cannot copy what has no path. It now says so and suggests extracting
  them first. Dropping a file into the folder it already lives in says that
  too, rather than silently doing nothing.

- **Some icon themes were refused outright, and needn't have been.** A theme
  may give an icon a name that points at another name rather than at a file,
  several deep — Kora is built that way, and Vaktari could not see it at all,
  while its folder icon sat there as an ordinary file that would have worked.
  Those chains are now followed.

- **The download progress bar sat at zero.** GitHub builds the Papirus archive
  as it sends it and never says how large it will be, so there was no percentage
  to show and nothing was shown — which reads as a stalled download rather than
  a working one. It now counts megabytes and moves, and still shows a real
  percentage where the server does give a size.

## [0.9.3] — 2026-08-14

### Added

- **The mouse's side buttons go back and forward.** The two under the thumb, as
  they do in Explorer and every browser. In a split they move whichever half the
  pointer is over, the same rule `Ctrl`+wheel already follows, and they leave the
  active pane alone — a navigation button is not a click.

## [0.9.2] — 2026-08-14

### Added

- **`.tar.xz` archives**, which is what most themes on the KDE Store are
  published as. .NET has no xz decoder at all — gzip and zip are in the
  framework and xz is not — so without this, half the themes anybody finds had
  to be recompressed before Vaktari would look at them.

### Fixed

- **An archive read a few bytes at a time was rejected as corrupt.** Two
  separate faults in the plumbing that puts a sniffed file header back in front
  of the archive, both invisible to gzip and zip and both fatal to xz: it
  returned less than it was asked for, and it misreported how far through the
  file it was. The second made a perfectly intact 5 MB theme fail with "Block
  check corrupt", which reads as a damaged download and is nothing of the sort.

## [0.9.1] — 2026-08-14

### Added

- **Pick an installed theme from a list.** Fetching Papirus brings three themes,
  and reaching the other two meant remembering where they went and browsing to
  them. Settings now lists what is actually on disk — found each time it opens,
  so a folder you deleted by hand stops being offered and one you browsed to
  joins the list rather than disagreeing with it. Choosing *Vaktari's own icons*
  is how you go back.

- **Install a theme you downloaded yourself**, from *Install from a file…* — a
  `.tar.gz` or a `.zip`, through exactly the same unpacking a fetched one gets.
  That is the point of offering it: a theme from anywhere meets the same
  symbolic-link wall on Windows, and this is what gets past it. The format is
  read from the file's first bytes rather than its name, so a mis-named archive
  still works and something that is not an archive at all says so plainly.

  A `.zip` cannot carry symbolic links, so a theme from one arrives with
  whatever its publisher chose to duplicate. Variants still work: they fall back
  to the theme they are named after.

- ***Open the icon folder***, for looking at what is installed.

### Fixed

- An entry claiming to be small and then supplying a great deal more is now
  stopped as it is written rather than after.

## [0.9.0] — 2026-08-14

### Added

- **Fetch an icon theme from Settings**, instead of finding one, downloading it
  and extracting it yourself. Papirus is offered; one click brings its light and
  dark variants too, and all three go to
  `%LOCALAPPDATA%\Vaktari\Icons\<pack>`. About 110 MB and twenty seconds.

  This exists because doing it by hand barely works on Windows. Papirus is built
  out of some fifty thousand symbolic links, and Windows creates none of them
  without Developer Mode — so an ordinary extraction fails fifty thousand times,
  says so at length, and leaves a theme with holes exactly where the file and
  folder icons should be. Unpacking it inside Vaktari means those links are read
  rather than made: no privilege needed, nothing duplicated on disk.

  Only `index.theme`, `.svg` and `.png` are ever written, so the makefiles and
  shell scripts a source repository carries never land at all. Every path in the
  archive must resolve inside the folder it is unpacked into, and the work is
  published only once it has finished.

- **`%ProgramFiles%`, `%SystemDrive%`, `~` and the rest, in the path bar.**
  Explorer takes them and Windows names its own folders that way everywhere
  else, so typing one here answering "no such directory" made Vaktari the odd
  one out. `$HOME` and `${HOME}` work too, and so do `%Documents%`,
  `%Pictures%` and `%Music%` — which read exactly like environment variables and
  are nothing of the sort.

  A name that means nothing is left as you typed it. Expanding it away would
  turn `%ProgramFilez%\Vaktari` into `\Vaktari`, which is the root of your
  drive.

### Fixed

- **Submenus under *More options* blinked and never opened.** 7-Zip's, Send
  to's, and every other extension that cascades: hovering one rebuilt the whole
  menu, which threw away the popup that had just appeared.

- **An icon theme you had already extracted yourself now works far better.** A
  dark variant is mostly links to the theme it is based on, so on Windows it
  arrived with no file or folder icons at all and Vaktari refused it outright.
  It now uses the theme sitting beside it, which is what those links meant.

- **Folders with something in them ignored *use my desktop's icons*.** Empty
  folders obeyed the setting and full ones did not, so a listing came out in two
  icon sets at once.

- **Icons could be drawn far too small.** A theme that had, say, only a
  16-pixel version of an icon was allowed to answer for a 48-pixel row while a
  perfectly good large one sat in the theme behind it.

- A row in the desktop's menu whose submenu could not be read is now shown
  greyed rather than looking as though it can be clicked. Choosing one asked the
  shell to run a command belonging to a different extension.

## [0.8.1] — 2026-08-14

### Fixed

- **0.8.0 would not start if you were upgrading.** A settings file written by
  an earlier version has no entry for the icon-theme folder, and that arrives as
  nothing at all rather than as the empty default — which threw before the
  window was ever created. A fresh install was unaffected, which is why it was
  missed. Settings files are now repaired as they are read, so a key added in
  any future version cannot do this again.

## [0.8.0] — 2026-08-14

### Added

- **Pick which terminal opens.** Vaktari now finds the terminals installed on
  this machine — Windows Terminal, Warp, PowerShell, Command Prompt, WSL, Git
  Bash, Alacritty, WezTerm and others — and offers them under *Open terminal
  here* when there is more than one. Settings has a *Terminal* choice that F4
  follows. Before this it tried Windows Terminal, then PowerShell, then cmd, and
  opened whichever started first, with nowhere to say otherwise.

- **The desktop's own menu, under *More options*.** 7-Zip, Send to, Restore
  previous versions, Edit with Notepad++ — whatever the shell extensions on your
  machine offer. Behind one hover rather than merged in, because hosting them
  runs their code in Vaktari, and a slow or broken extension confined to that
  submenu spoils only it.
- **Use your desktop's icons instead of the bundled set**, from Settings. A
  program then shows its own icon, a shortcut carries its arrow, and a folder
  you gave a custom icon keeps it. Thumbnails are unaffected — those stay
  Vaktari's, with its own cache and size rules.
- **Import an icon theme you downloaded.** Point Settings at the folder holding
  index.theme — Papirus, Tela, Numix and most Linux icon sets are published in
  that format — and Vaktari uses it for every file. A link beside it opens a
  catalogue of themes that work. An imported theme wins over both the bundled
  set and your desktop own icons, being the most deliberate of the three.
- **Administrator actions on Shift+right-click**: *Run as administrator* for
  things Windows can actually start elevated, and *Open admin terminal here*.
  Vaktari never holds administrator rights itself — these ask the system, which
  shows its own consent dialog and decides.

### Changed

- **The right-click menu is less thin.** *Copy as path* (on Windows' own
  `Ctrl+Shift+C`), *Duplicate*, *Rename in bulk* and *Open terminal here* are
  back at the top level. Consolidating the menu had pushed them behind *More*,
  which made it tidy and made the things people reach for a hover further away.

### Fixed

- **Using your desktop's icons never actually worked** until now: the call that
  reads an icon's pixels was bound to a function name Windows does not export,
  so every icon came back empty and fell through to the drawn set — which looks
  exactly like the setting being off.
- **Duplicate could copy into a folder named after the listing.** In Recent on
  Linux it created a directory called `vaktari:recent-files` and copied into it;
  from the bin it copied whatever now occupies the row's old path. It was the
  one file operation the bin guards had missed.
- **Opening a terminal could crash the application.** If the chosen terminal
  refused to start, the fallback picked the same one again and the two paths
  called each other until the stack ran out.
- Hovering a nested entry inside *More options* rebuilt the shell menu
  underneath the pointer, because the submenu-opened event bubbles.
- *More options*, the administrator entries, and Copy to / Move to / Open in new
  tab no longer linger in the Recycle Bin, where the rows name paths the files
  no longer occupy.
- A folder that cannot be listed says so in the listing. A tab whose folder had
  been deleted showed column headings over nothing.
- The status bar no longer reads "qa —" in split view: the folder name was
  printed with a dash and an empty status after it, permanently.
- On Linux, `$TERMINAL` is no longer launched with Konsole's flags whatever it
  names — `TERMINAL=alacritty` produced a command alacritty rejects.
- The Windows icon cache is bounded. Folders, shortcuts and executables are
  cached per file, so walking a large drive used to hold a bitmap for every
  folder seen, for the life of the session.
- **The settings, split and details buttons are on the rightmost pane again.**
  They were moved there deliberately and an audit put them back on both sides,
  reading the panel toggle's "for this side" tooltip as meaning the left half
  had lost the feature. It had not: F11 toggles whichever side is active.

## [0.7.1] — 2026-08-11

### Fixed

- **Clicking *New* in the right-click menu killed the application.** Also
  *More ▸ Scripts*, if you had any scripts. Consolidating the menu moved those
  entries underneath other entries, and a style written to give each item in a
  submenu its command turned out to reach the submenu itself as well — so the
  submenu ended up holding a command meant for one of its own items, which
  Avalonia calls the moment the parent opens. Nothing caught it: the markup
  compiles, the bindings resolve, and the submenus that were not moved go on
  working.

## [0.7.0] — 2026-08-11

### Added

- **The details panel can be resized.** Drag the edge between it and the
  listing. The width is remembered per side and comes back with your session.
- **Places can be removed.** Right-click one you added and choose *Remove from
  places*. Adding was reachable two ways and removing none, so the only way to
  drop a place was editing a file by hand.
- **Keyboard shortcuts appear in the right-click menu**, beside the entries that
  have them.
- **Handing a folder to the running copy works.** Opening a folder from
  elsewhere — as the default file manager, or `vaktari ~/Downloads` from a
  script — now opens a tab in the window you already have. It never once did.

### Changed

- **The right-click menu hides what does not apply.** Right-clicking empty space
  no longer offers Open, Copy, Cut, Rename or *Move to bin*, all of which were
  enabled and did nothing. Right-clicking a file still selects it first, so
  nothing disappears when you actually mean it.
- **Sharing is one entry** covering all three of its states, including while
  copyparty is installing — during which it used to vanish from the menu with
  nothing said.
- Settings calls the path bar the path bar, and the sorting checkbox is
  *Arrange (sort and group)*, matching the menu it governs.
- The README calls it *places* throughout, matching the interface, rather than
  alternating between places and pins.

### Fixed

- **The bin refuses operations that would destroy the wrong file.** A row in the
  bin carries the path the item came from, so deleting or renaming one acted on
  whatever occupies that path *now*. Trash `notes.txt`, write a new one, delete
  the bin row, and it was the new file that went. Restore and Empty are what the
  bin offers; the rest is refused and says so.
- **Pasting into the bin or a Recent listing** created a folder literally named
  `vaktari:trash` in the working directory, moved the files into it, deleted the
  originals and reported success. Only on Linux — Windows was saved by a colon
  being illegal in a path.
- **Uninstalling no longer leaves folders unopenable.** Registering as the
  default file manager writes a shell verb pointing at the executable, and
  removing the program left the verb behind, so every double-clicked folder
  failed with an error naming a missing file rather than the program that
  registered it.
- **The details panel's resize handle did nothing** — it painted a bar and set a
  resize cursor over a control that could not move anything.
- **Emptying the bin said nothing when it failed**, which was indistinguishable
  from an already-empty bin. Restoring counted only its successes, so a file
  that could not go back looked like one you had not selected.
- **"Show tooltips on rows" only silenced some tooltips**; the path tooltip
  ignored it.
- Scrolling a folder of pictures no longer reads image headers on the thread
  drawing the scroll, and the thumbnail cache is bounded by memory rather than
  by a count that meant anywhere between 40 MB and 2.4 GB depending on the
  layout you happened to be using.
- The sidebar no longer builds its list of places on the drawing thread at
  startup, where a disconnected network drive froze the window for as long as
  the network took to give up.
- Navigating away from a large repository no longer leaves a `git` process
  running behind it.
- Both panes show the details-panel toggle in split view. It was treated as a
  window-level control, so the left half had a panel and no way to open it.
- `BUILDING.md` no longer warns that a tracked file is untracked, and
  `brand/install.sh` no longer defaults to a path under a project directory from
  two renames ago.
- The Arch package supersedes the old one and ships the symbolic icon the RPM
  already did.
- The test suite ran two headless tests at once on a single UI thread, which
  made it fail twice for reasons unrelated to what it was testing.

## [0.6.1] — 2026-08-09

### Changed

- **Renamed from Heimdall to Vaktari.** The old name is widely used by other
  projects. Settings, tabs and folder views carry over; on Windows the installer
  replaces the old installation rather than leaving two copies, and hands the
  folder classes back if the old one had claimed them.

## [0.6.0] — 2026-08-09

### Added

- **Properties opens the Windows shell's own sheet**, with its tabs, its
  security page and its handlers, instead of an imitation.
- **Offer to open folders**: Vaktari can register as the program that opens
  folders and drives.
- **A light scheme**, and a *Follow the desktop / Light / Dark* choice in
  Settings.
- **Open with** lists the applications actually registered for the file type,
  with a way out to the system's own picker.
- The search field folds away into its icon when empty, and the path bar keeps
  the end of a long path rather than the beginning.
- The Windows installer stops before overwriting a running copy.

### Changed

- New file-type icons across seventeen categories; folders show a page when they
  have something in them; the sidebar icons and the two storage icons were
  redrawn as families.
- Denser rows, two typefaces and three columns, following the interface
  proposal. The column browser and tags were removed.
- The Recycle Bin is called the Recycle Bin on Windows.
- The window's own controls sit on one side in split view.

### Fixed

- Escape cancels the bin prompt.
- The whole row is clickable, not only the filename.

## [0.5.1] — 2026-08-06

### Fixed

- The design scheme overwrote a font chosen moments earlier in Settings.

## [0.5.0] — 2026-08-06

### Added

- **Network shares on Windows**: SMB, and WebDAV where the WebClient service is
  running. Discovery, connection and the credential prompt are the system's own.
- Search accepts globs, and Windows bookmarks kept as files are imported.

### Fixed

- Git for Windows is routinely installed without going on `PATH`; it is found
  anyway.
- A console window flashed on every folder listing.
- Several sidebar rows trimmed their labels wrongly or could not be opened once
  connected.

## [0.4.0] — 2026-08-05

### Added

- **Windows is a supported install**, packaged with Inno Setup, with an icon on
  the executable and a checksum on the download.
- The design reference's palette, typeface, icons and stroke weights, applied
  verbatim.
- Toolbar search and an inline filter.

### Fixed

- Breadcrumbs use the platform's own separator, and none after a root.
- Search walks off the drawing thread, and debounces.

## [0.3.1] — 2026-07-30

### Added

- `--version` prints the build and the file it is running from, which is how you
  tell a stale local install from a packaged one.

## [0.3.0] — 2026-07-30

### Added

- Rubber-band selection in every layout, including the list.
- Compact view is virtualized.

### Changed

- Settings that did nothing were removed, and silent failures now say something.

## [0.2.0] — 2026-07-29

### Added

- **Version-control marks in every layout**, refreshed as files change and as
  the repository does, with a settings toggle.
- The bin's action bar.

### Fixed

- *Go up* is disabled on listings that are views rather than folders.
- The desktop entry and the window's `WM_CLASS` match, so the panel shows the
  right icon.

## [0.1.2] — 2026-07-28

First tagged releases. Linux tarball and RPM.

[Unreleased]: https://github.com/dkflint723/vaktari/compare/v0.8.1...HEAD
[0.8.1]: https://github.com/dkflint723/vaktari/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/dkflint723/vaktari/compare/v0.7.1...v0.8.0
[0.7.1]: https://github.com/dkflint723/vaktari/compare/v0.7.0...v0.7.1
[0.7.0]: https://github.com/dkflint723/vaktari/compare/v0.6.1...v0.7.0
[0.6.1]: https://github.com/dkflint723/vaktari/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/dkflint723/vaktari/compare/v0.5.1...v0.6.0
[0.5.1]: https://github.com/dkflint723/vaktari/compare/v0.5.0...v0.5.1
[0.5.0]: https://github.com/dkflint723/vaktari/compare/v0.4.0...v0.5.0
[0.4.0]: https://github.com/dkflint723/vaktari/compare/v0.3.1...v0.4.0
[0.3.1]: https://github.com/dkflint723/vaktari/compare/v0.3.0...v0.3.1
[0.3.0]: https://github.com/dkflint723/vaktari/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/dkflint723/vaktari/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/dkflint723/vaktari/releases/tag/v0.1.2
