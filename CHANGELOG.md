# Changelog

What changed in each release, from the point of view of someone using Vaktari.
Entries describe behaviour, not commits — if a change is invisible from the
outside, it belongs in the git history rather than here.

Newest first. Dates are the day the tag was cut. Versions follow
[semantic versioning](https://semver.org), with the caveat in
[Status](README.md#status): there has been no stable release, and the numbers
should not be trusted for compatibility yet.

## [Unreleased]

### Added

- **A copy or move that left something behind can be asked to go again.** The
  batch never stopped to ask — it carried on past a locked file and named what
  it could not do — but the only verbs on offer were the two it already had:
  skip, which is what it did, and cancel, which had been on the bar all along.
  The missing one is the one that needs you to go and do something first, close
  the program holding the file, plug the drive back in, and that is exactly the
  answer no dialog in the middle of five thousand items can take. So the offer
  is made afterwards, as a *retry 3* button beside the message, and it goes
  again on those three and nothing else: re-running the successes would ask
  every conflict they already answered a second time. A retried item lands
  where this run decided to put it, so an item retried after *Keep both* goes
  into the folder that was kept separate rather than merging into the one you
  asked to leave alone. A clean run offers nothing, and so does a cancelled
  one — someone who pressed cancel is not asking for the work back.

- **On Linux, other applications can ask Vaktari to show a file.** A browser's
  "Show in folder", a chat client's "Open containing folder", and the same
  entry in most freedesktop applications go through a standard interface that
  Vaktari did not implement, so those buttons either did nothing or opened
  whichever file manager was installed instead. Vaktari now answers, and can be
  set as the desktop's handler; asked to show a file, it opens the folder with
  that file selected rather than merely opening the folder.

- **Search results are a listing, not a popup.** They used to be a floating
  list drawn over the folder they were meant to help you act on, and it was the
  one collection of files in Vaktari you could do nothing with: no
  multi-select, no drag, no context menu, no columns, no sorting, and nothing
  you could reach without a mouse — Enter and Down did nothing at all, so
  type-then-Enter dead-ended. A search is now somewhere you go. It has a path,
  which means a tab, a title, Back and Forward, and a place in the session, so
  it is still there tomorrow. The rows are the pane's own rows, so everything
  you can do to a file you can do to a result: select five and copy them, drag
  one out, rename in place, open a second search beside the first in a split.

  Above them is a band saying what was asked and where it looked, with a *This
  folder only* box that finally does something — it had no control bound to it
  at all, so searching your whole machine was built, tested by neither
  platform, and unreachable. Ticking it is a navigation, so Back returns to the
  wider answer instead of running it again.

  A search that is running says so, with a bar and a *Stop* beside it. Nothing
  said it before: the status line changes its words only when a batch lands, so
  between batches a walk still going looked exactly like one that had finished
  — and from This PC, which reads every drive, that walk is unbounded. Stop
  keeps what it found, which is the whole difference between it and leaving.

  Typing is separate from searching now. Nothing runs until you press Enter, so
  there is no pause to type through, and one character is a question like any
  other — "b" for a folder of build outputs used to be refused with "keep
  typing…". Ctrl+F over a search opens the field holding the question you are
  looking at, so refining it means editing rather than retyping. Escape puts
  the field away and leaves the results standing. Results carry the folder they
  came from in their own column, the way Recent does, and *Open file location*
  takes you there.

- **There is somewhere above a drive root, and it lists your drives.** Up was
  disabled at the top of `C:\` by construction, the breadcrumbs stopped at the
  drive, and typing "This PC" answered that no such directory existed — so the
  only way to another drive was the sidebar, which you cannot sort, select in,
  or open two of in tabs. This PC ("This computer" on Linux) is now a listing
  like any other, built from the same drives the sidebar shows, with its own
  row directly under Home, a crumb above every path, and each drive's capacity
  in the size column. A drive has no modified date, so that column is now empty
  there rather than reading "31 Dec 1969", which looked like a fact.

- **Tabs behave like tabs.** They stayed in the order they were opened in —
  pressing one and dragging did nothing at all, while the order was already
  remembered between sessions with no way to alter it. You can drag them into
  place now, and the tab you dragged stays the tab you are looking at. Closing
  one used to throw its whole state away, so Ctrl+Shift+T had nothing to put
  back; each side now remembers its last ten, newest first. A tab has a
  right-click menu — *Duplicate*, *Close other tabs*, *Close tabs to the
  right*, *Reopen closed tab* — and none of those can leave a side with no
  tabs. Middle-clicking a sidebar place or a breadcrumb opens one too, behind
  the tab you are in, which is what that gesture has always meant.

- **A drag says where it is about to land, and has somewhere new to land.** The
  only thing marked while you dragged was the pane, and which half of the
  window your pointer is in was never the question — what you could not tell is
  whether releasing puts the files into the folder under the pointer or into
  the folder being listed, and finding out meant releasing and going to look.
  A folder row now takes a ring in all three layouts and a sidebar place takes
  a wash of its own, neither of which moves the row while you aim at it. Files
  can also be dropped onto another tab, which rests for a moment and then
  switches so you can see where you are aiming, and onto the bin, where they go
  to the bin the same as pressing Delete.

- **The details view lets you choose its columns, and there is a Type column to
  choose.** Right-click the headings, where Explorer and Dolphin both put it:
  Name, Type, Size, Modified — Name ticked and greyed, because a listing of
  sizes with no names is not a listing of anything. Nothing could turn a column
  off before, and sorting by type had been there from the start with no heading
  to click. The new column spells the extension the way Explorer does when it
  has nothing better — "PNG file", "Folder", "File" — and a symlinked folder,
  a junction or a mount point reads "Folder link" rather than being drawn
  exactly like the thing it points at. The choice belongs to the pane you
  opened the menu over and travels with the tab into your next session, and a
  narrow pane still drops columns to keep the name readable whatever is ticked.

- **Ctrl+Z takes back a paste, a new folder, and — on Windows — a delete.**
  Copies were deliberately not undoable, because undoing one means removing
  files; but pasting into the wrong folder is one of the easiest mistakes a
  file manager lets you make, and Ctrl+Z doing nothing left them there for you
  to find and remove by hand. The undo now sends what that paste created to the
  bin, where it is recoverable — only what it created, and only if it is still
  there. Ctrl+Shift+N, a new file and new-from-template can be taken back the
  same way. Recycling something on Windows also left nothing to undo, behind a
  note explaining that putting it back was impossible, which had stopped being
  true long before; Vaktari now reads the bin either side of the delete so it
  knows exactly which entries it made. A move still undoes as a move, and there
  is no redo yet.

- **Back and forward remember the whole walk, not just one step.** Both buttons
  offered a single step at a time out of a history the pane had kept all along,
  so a pane ten folders deep could only be walked out one press at a time with
  no way to see where the next press went. Right-click either chevron for a
  list of where it goes, nearest first, twelve deep — and picking a row several
  steps back leaves both buttons exactly as that many presses would have, so
  forward walks back through everything you stepped over.

- **A copy can be paused now, not just cancelled.** The machinery was there
  from the day it was written — both engines stop between items and inside the
  byte loop, so holding a large transfer mid-flight always worked — and nothing
  in the window could ask for it. There is a *pause* button on the transfer bar
  beside *cancel*, and it reads *resume* while the transfer is held.

- **Search can leave the folder you are standing in.** The results panel now
  carries a *This folder only* tick box, on by default; clear it and the search
  runs over the whole machine. Both platforms could already search unscoped —
  the switch for it was bound to no control at all, so every search you ever
  ran was confined to the current folder whether that was what you wanted or
  not.

- **A drive you plug in appears on its own, and there is a way to remove it
  safely.** The sidebar never watched for devices, so a stick showed up
  whenever something else happened to rebuild the list — and *Eject* did
  nothing on either platform. Removable drives now carry an eject button on the
  row and an *Eject* entry in the menu, and Vaktari steps every tab off the
  drive first, so its own folder watch cannot veto the removal and blame a
  program you would never find. The answer says which of three things happened:
  safe to unplug, unmounted but still held by the system, or something still
  has a file open — the middle one being the state where pulling the stick is
  what loses data. On Linux, removability was read from the mount point alone,
  so a USB disk given a stable line in fstab or mounted by hand came out a
  fixed disk with no eject button; the kernel is asked as well now, anything
  reached over the USB bus counts, and the disk you are running from is never
  offered.

- **On Linux, the sidebar lists volumes you have not mounted.** The mount table
  was the only thing it read, so a partition nobody had mounted did not exist
  as far as Vaktari was concerned — and on a desktop that does not automount, a
  stick you plugged in never appeared at all. Every device carrying a
  filesystem is now listed under devices, dimmed and in place rather than
  silently dropped, and clicking one mounts it. Plugging one in is noticed as
  it happens. A data CD appears too: discs were being filtered out by the rule
  that hides snap images, which are not that kind of image at all.

- **Mount an .iso from the right-click menu, and put it away again.** Choose
  *Mount* and the pane opens what is inside the image; the same row reads
  *Unmount* while it is attached. Double-click is deliberately left alone, so
  an image you had opening in an archive tool still does. What is mounted is
  asked of the machine rather than remembered, so an image something else
  attached reads as mounted here too, and can be put away from here.

- **The keyboard reaches the menu, the listing and your home folder.** The Menu
  key and Shift+F10 raise the right-click menu on the row you are standing on;
  until now it was reachable with the mouse and nothing else. F6 puts the
  keyboard back in the listing, which had no way in at all — at startup, or
  after Ctrl+L and Escape, the arrow keys were dead until you touched the
  mouse. Alt+Home goes home, which was only ever a fallback starting point
  rather than somewhere you could ask to go, and Ctrl+Q closes the window,
  which the keyboard could not do at all.

- **Refresh, Select and the sidebar have somewhere to be clicked.** Refresh had
  answered F5 all along and appeared in no menu, which is a problem exactly
  when a listing has gone stale; it sits low in the right-click menu now,
  because the folder watcher covers most of what it is for. Choosing things had
  one command and no menu home, and no way at all to clear a selection or turn
  one inside out, so there is a *Select* menu with all three and Ctrl+Shift+A
  for Invert. The sidebar could only be hidden with Ctrl+B, which nothing said
  — and getting it back was the same problem with the sidebar gone; the view
  flyout carries a *Sidebar* tick beside *Show hidden files*, F9 does the same,
  and that flyout also has a row for the shortcut list, which until now could
  only be reached by already knowing one of the shortcuts. The F1 sheet gained
  six more that worked all along and were never listed.

- **Hover something too narrow to read, and it will tell you what it says.**
  The name was the one thing in a row that trimmed itself and then said nothing
  about it: in a narrow split pane, or a grid tile that gets two lines and an
  ellipsis, reading one meant pressing F2 for the edit box and Escape to get
  out again. All three layouts pop the whole name now, after a long pause,
  because a tip over a name that already fits is noise. A sidebar place shows
  its path the same way — two of them are easily both called Documents, one
  local and one on a share, and nothing said which was which.

### Changed

- **A drive's tab is called what the sidebar calls it** — "Windows (C:)" rather
  than "C:". The title falls back to a path's last segment, which for a drive
  root is the root, while the sidebar three inches away had the volume label
  all along because building that list is where it gets read.

- **The Type column says what a file is**, rather than its extension in
  capitals. Every row read "&lt;EXT&gt; file" — the extension the column sits
  beside, spelled louder — so a column whose every value could be read off the
  Name column beside it. Programs say Application, a .txt is a Text document, a
  .png a PNG image. Sorting and grouping by kind follow, so programs file
  together instead of under E. An extension nobody has named still falls back
  to what it said before.

- **The status bar says how many folders and how many files**, rather than a
  single count of "items" — a folder of 200 is a different place depending on
  whether it holds two subfolders or two hundred. A selection that holds both
  kinds says how many of each; one that holds only files does not repeat itself.

- **A folder's size is counted when you open its properties**, not when you ask
  for it — the one figure people open that window for was the one thing not on
  the page. Folders on network shares still wait to be asked: measuring walks
  the whole tree, which over SMB or SFTP is a round trip per directory, and the
  button is right there for when you want it.

- **A selected row can be dragged from anywhere on it.** In details view, the
  gaps around the Size and Date text are row background, and those gaps are most
  of the width of both columns — so reaching for a selection you had just built
  started a rubber band and cleared it instead. A row that is already selected
  now drags; one that is not still starts a band, which is what keeps
  rubber-band selection reachable in a listing that fills the window.

- **Tab keeps the name and renames the next file.** Renaming a run of them cost
  three keystrokes each — Enter, arrow, F2 — and the arrow was the worst of the
  three: a rename can re-sort the folder, so the row under the one just
  finished is not the file that was under it a moment ago. Tab now commits and
  opens the next row's name, Shift+Tab the one before, and the run stops at
  either end of the listing rather than wrapping round to a name you have just
  settled. A name the disk refuses — one that is already taken, most often —
  stops the run where it stands instead of skipping that file in silence.

- **F6 moves the keyboard between the listing, the address bar and the
  sidebar**, the way Explorer cycles its regions. It used to put the keyboard
  in the listing and nothing else, so pressed from the listing — where it had
  just put you — it did nothing. Its original job survives: pressed when the
  keyboard is on a toolbar button, or nowhere at all, one press still puts you
  on the rows. With the sidebar hidden the cycle is two places, not three.

- **Ctrl+L pressed again re-selects the path** instead of doing nothing. The
  first press opens the address bar and selects what is in it; the second used
  to reach nothing at all, because only the box appearing put the caret there
  and by then it was already on screen. Both references answer a repeat press
  by re-selecting, which is how you replace a path you have half-edited without
  going for the mouse. What you typed is still kept, as it has been.

- **Ctrl+Page Up and Ctrl+Page Down move through tabs**, the other way every
  browser does it. Ctrl+Tab has always worked; this pair simply was not bound.

- **Ctrl+Shift+1, 2 and 3 go straight to a layout.** F8 cycles the three in
  order, so reaching the one you want cost up to two presses through a layout
  you did not ask for — and otherwise they were reachable only by the toolbar
  chip, with the mouse. The numbers follow that chip from left to right.

- **Enter or ↓ in the filter box hands the keyboard to the rows.** Both keys
  somebody presses to leave that box did nothing, so the way out was Tab, F6 or
  the mouse — the filter sat holding the keyboard over a listing it had just
  narrowed. The first row is selected as you arrive, so the arrow keys have
  somewhere to move from and Enter opens something; the filter and its text
  stay where they are, since Escape is what clears them.

- **The keyboard sheet answers the question F3 raises.** Explorer opens search
  on F3; Vaktari splits the window on it, matching Dolphin. Somebody arriving
  from Explorer presses it, gets a second pane, and opens the sheet to find out
  what happened — landing on a line that answered only the question they did
  not ask, with the key they wanted two headings further down and off the
  bottom of the sheet. That line now says where search is.

- **Ctrl and the number pad's nought reset the zoom**, as Ctrl and the pad's
  plus and minus already did. Those two worked because Avalonia matches the
  pad's arithmetic keys against the top row's; nothing does that for the
  nought, so the one key of the three that somebody with a hand on the pad
  actually reaches for was the one that did nothing.

- **On Windows, "whatever the desktop is set to" now means it.** That setting
  worked on KDE and quietly meant double-click on Windows however Folder
  Options was configured, on the belief that Explorer's preference was not
  recorded anywhere readable. It is, and Vaktari reads it at startup and after
  every settings save — so if your Explorer opens items with a single click,
  Vaktari now does too. That is the one change here you may notice the moment
  you upgrade. When the preference cannot be read, your own setting decides, as
  before.

- **Moving files within one drive is instant.** A move copied every byte and
  deleted the original, so shifting a folder of video across one disk rewrote
  the whole folder — while *undoing* that same move took no time at all,
  because undo had always renamed rather than copied. A move that stays on one
  volume now renames too, and the progress bar still advances through it.

- **The first click on Date modified puts the newest file on top.** Every
  heading started ascending, so the download that had just finished — the
  reason anybody clicks that heading — landed at the very bottom of the folder
  and took a second click to find; Size did the same with the empty files.
  Dates and sizes now start descending, while Name and Type still start at A,
  because A to Z is what those two mean. Clicking again still reverses.

- **A new tab keeps the view you were working in.** Ctrl+T reset hidden files,
  the layout, the sort, the grouping and the zoom, so a new tab was a tab you
  had to set up again; it carries all of that across now. *Open in new tab*
  also leaves you where you are rather than jumping to the tab it just opened —
  asking for a tab is precisely how you carry on with what you were doing —
  while Ctrl+T still takes you there, because that one is a request for
  somewhere to work.

- **The confirmation names what it is about to destroy.** "permanently delete 1
  item(s)?" asked you to approve something irreversible with a sentence that
  would have read identically for any file on the machine, so selecting the
  wrong row and pressing Shift+Delete cost a keystroke and told you nothing.
  One thing is named now and several are counted, and the parenthesised plural
  has gone with it. A very long name is elided in the middle so the extension
  survives: `.pdf` against `.exe` is the part that changes what deleting it
  means. Delete, move to the bin and empty the bin all did it. A refused
  *New file* also spoke .NET where *New folder*, one menu row away, spoke
  English: "could not create file: Could not find a part of the path '…'"
  against "that folder is not there any more". Both ask the same describer
  every other refusal in Vaktari already used.

- **Hidden and system files look hidden.** With "show hidden files" turned on,
  desktop.ini, thumbs.db, .DS_Store and every dotfile stood in the listing at
  full strength, indistinguishable from the folder's real contents — which is
  the whole reason turning that setting on is survivable elsewhere. Their names
  are ghosted now, faded rather than recoloured, so it reads the same on a
  selected row and in every theme.

- **On Windows, a shortcut is called a Shortcut, and one to a folder opens
  here.** Windows hides the .lnk extension everywhere else on the machine, so
  Desktop and the Start Menu — folders that hold nothing else — read here as a
  wall of "Chrome.lnk" rows typed "LNK file", in a window sitting beside others
  that all disagreed with it. Names drop the extension in every view now, and
  the Type column and the properties window both say Shortcut. Double-clicking
  a shortcut to a folder used to hand it to Windows, which opened a separate
  Explorer window over the top of you; it navigates the pane instead. A
  shortcut to a program is still the system's to launch, and on Linux a .lnk
  stays an ordinary file from another system, because nothing here can follow
  it.

- **An empty bin no longer calls itself an empty folder.** "This folder is
  empty" was printed over the bin, over Recent and over This PC, none of which
  is a folder — and over an empty bin it invites the reading that a folder
  somewhere has lost its contents, when all it means is that nothing has been
  deleted lately. Each listing says its own line now: "the Recycle Bin is
  empty", "no drives found", "no files opened lately".

- **Group by is offered only in the details view.** Choosing a grouping in the
  icon or compact view reordered the tiles and drew no headings, so the runs it
  made were invisible and the menu looked as though it had done nothing.
  Headings for those two layouts are a feature in their own right rather than a
  gate, so the entry is hidden there until they can draw them.

- **The properties window is titled with what it describes.** Every one of them
  was called "properties", so opening three files side by side to compare them
  — which is the reason to open three — gave three identical taskbar buttons.

### Added

- **A copy says how fast it is going and how much longer it has.** The
  transfer bar counted items and bytes — *34/1200  1.2 GiB/4.9 GiB* — which is
  the one thing you can work out for yourself by looking at it twice. What it
  never answered is whether this is a two-minute job or an hour, which is the
  question behind *wait for it, or go and do something else*. There is now a
  bar, a speed and an estimate: *10 MiB/s · about 4 min left*. The speed is
  measured over the last few seconds rather than averaged across the whole
  run, so a copy that crosses from an SSD onto a memory stick stops promising
  a speed it will never see again — and a transfer that stalls stops claiming
  a speed at all, rather than sitting there looking busy. A delete fills its
  bar from the count of files, since it has no bytes to measure, and promises
  no time it cannot know.

- **Undo and Redo are in the menu, and they say what they will do.** Ctrl+Z
  was the only route to either, so the whole feature was invisible to anyone
  who had not read the shortcuts sheet — and pressing it told you nothing
  about what it was going to take back, so after a copy, a rename and a delete
  in quick succession the only way to find out which one came back was to
  press it and look. The rows read *Undo copy of 3 items* and *Redo move of
  readme.txt*, they are dead when there is nothing to take back rather than
  disappearing, and afterwards the status line says what happened.

- **A sidebar section can be folded away.** Places, Devices, Shares, Network,
  Remote, Sharing and Recent do not all fit on a laptop screen, so reaching the
  bottom of the list meant scrolling past four headings that were never going to
  be clicked. Every heading is now a fold: click it and the section closes to
  its title, click again and it comes back. What you folded is remembered
  between runs, and it survives the rebuild that plugging in a drive causes —
  which is when the sidebar is at its longest. Nothing is folded until you fold
  it, and the keyboard walking into the sidebar still lands on a place rather
  than on a heading.

### Fixed

- **The sidebar can be walked with the keyboard, and stops taking the
  listing's keys.** F6 put the keyboard in the sidebar and left it there: the
  arrow keys did nothing, so the only way on from a place row was to Tab
  through every button in the panel. Up and Down now move between places,
  Home and End reach the ends, and the Menu key opens the menu for the row you
  are on rather than for the folder behind it. The keys that used to act on
  the listing from there are refused — Delete in the sidebar was trashing
  whatever happened to be selected in a folder that no longer had the
  keyboard, and the confirmation named files you could not see. Navigation and
  undo are unaffected: those are not about the listing's selection.

- **One thing can be deleted out of the bin.** The only ways out of it were
  *Restore* and emptying the lot, so getting rid of a single item meant
  destroying everything else in there with it — and Shift+Delete on a bin row
  made that worse: it asked "delete permanently?", took the yes, and then
  refused, because a bin row carries the path the file used to occupy and the
  file operations cannot act on one. Asked, answered, and nothing happened.
  There is now a *Delete permanently* entry in the bin's own menu, Shift+Delete
  does the same thing from the keyboard, and both act on the rows you actually
  picked. A file that was trashed, restored and trashed again leaves two rows
  with the same original path; the one you selected is the one that goes.

- **The bin says what it did, after it has done it.** Restoring three items,
  emptying, and now deleting for good all reload the listing when they finish,
  and a finished listing clears the status line — so the report was wiped by
  the very reload it was reporting on, and the line was blank a moment later.

- **F2 stops throwing away the name you are typing.** The rename bar is inline
  and the listing behind it stays live, so pressing F2 again — easy to do while
  hunting for the right row — discarded what you had typed and re-pointed the
  bar at whatever was selected now, which is a different file the moment
  another row has been clicked. Shift+F2 opened the batch-rename dialog on top
  of the open bar. Both now wait their turn, and neither fires while the
  keyboard belongs to a text box, so F2 in the address or filter bar no longer
  renames a row hidden behind it.

- **The back and forward buttons drive the pane you are looking at.** Pressing
  back while the pointer happened to be over the third tab's label rewound the
  third tab instead: the listing on screen did not move, nothing said anything,
  and the only trace was a title quietly changing on a folder that was not
  open. Every browser drives the page you are looking at, whichever piece of
  chrome the pointer is over. In a split, pointing at one side still navigates
  that side. The same fault took the tab strip's *+* button to the wrong half
  of a split.

- **Switching tabs stops moving the keyboard into the filter box.** Ctrl+Tab
  back to a tab that had the filter open put the caret in the filter field, so
  arrow keys, Enter, Delete and type-ahead were all dead in a listing that
  looked ready for them — with nothing on screen to say the keystrokes were
  going into a 200-pixel box up in the path bar. The box takes the keyboard
  when you ask for it and not when it merely reappears.

- **Double-clicking the blank half of the tab strip opens a tab**, as it does
  in Explorer, Dolphin and every browser. It did nothing here — which is worst
  exactly when you reach for it, with a dozen tabs open and the *+* scrolled
  out of sight.

- **A file kept across a move is numbered, not called a copy.** Moving a file
  into a folder that already had one of that name and choosing *Keep both*
  produced "report - Copy.txt" — after an operation that copied nothing, in a
  folder where no report.txt of yours had ever been. Explorer distinguishes the
  two and so does this: " - Copy" is what duplicating a file in place makes,
  where the word is true, and a conflict kept apart is "(2)", counting from two
  because the file already sitting there is the first.

- **The bin's icon says whether it has anything in it**, which is the one
  question you ask a bin and the one thing it would not answer — it drew the
  same glyph holding a thousand items as holding none. A symbolic link now
  carries a small arrow too, so a link to a folder is distinguishable from the
  folder.

- **The icon-size number matches the icons on screen.** The box quoted 26 next
  to 18-pixel icons in Details, and quoted 26 in Grid and Compact as well,
  where the icons are 72 and 36 — a number no layout had drawn in some time. It
  now reports the active layout's own size, and updates when you switch layouts
  even if the zoom did not move.

- **"Open in new tab" opens all the folders you chose.** Selecting five folders
  and picking it opened one — the focused one — and said nothing about the
  other four. It opens every folder in the selection now, still in the
  background, and still refuses politely rather than silently when the
  selection is large enough to be a wall of tabs. The entry also used to
  disappear whenever the *focused* row was not a folder, so clicking a file and
  then ctrl-clicking two folders took away the very row that would open both.

- **Paste goes grey when there is nothing to paste.** The row was live in every
  listing but the bin, and picking it answered "clipboard has no files" — which
  is something the row could have told you by looking grey, the way Explorer's
  does. Ctrl+V still speaks, because with no menu open there is nothing to go
  grey.

- **Searching no longer reopens a sidebar you deliberately hid.** Ctrl+F, Ctrl+E
  and the toolbar magnifier each forced the sidebar back to full width, undoing
  a Ctrl+B or an F9 without a word — and because the sidebar's width is
  remembered between sessions, it was still there on the next launch. The
  search field lives in the path bar and every result carries its own full
  path, so there was nothing in the sidebar it needed.

- **Inside a git repository the "name" heading lines up with the names again.**
  Every filename in a repository is indented to leave room for its
  version-control letter, so names stay aligned whether or not a file is
  marked. The heading was not, so the word "name" sat over the marks instead of
  over the names — and only inside a repository, which is the one place those
  marks are the reason you are looking.

- **On Linux, "use my desktop's icons" is no longer offered where it does
  nothing.** The setting reaches the listing through a per-file icon lookup
  that Windows has and freedesktop does not, so on Linux the box could be
  ticked, saved, and found still ticked, while every row went on drawing
  exactly what it drew before — and what the label promises was already true
  there, because a Linux listing draws with your icon theme anyway. Its
  explanation also named Windows on both platforms. The icon-theme chooser
  below it is unaffected; that one works on both.

- **A click means what it means everywhere else.** Ctrl+click and Shift+click
  opened what they were selecting: neither handler looked at the modifiers, so
  extending a selection launched whatever it passed over, and Shift+click
  opened the far end of the range. A modified click is a selection gesture and
  never an open. Right-clicking the blank half of a row inside a five-file
  selection cleared the selection before the menu opened, so the Delete you
  chose next took one file — the rule that clicking empty space means never
  mind ran for every button, and a details row spans the width of the list.
  Pressing the scrollbar did it too, and dragging the thumb drew a selection
  band down the side of the list while the view scrolled underneath. A plain
  left click on the background, which is the one that ought to clear, cleared
  nothing at all, and now does. And there is deliberately no time limit on the
  two halves of a double-click, so a folder you had opened stayed remembered as
  "clicked once": press Back, click it a single time to rename it, and it
  opened again, minutes later if you liked.

- **A file drag starts on a file, carries all of them, and keeps what it
  sweeps.** Any left-drag inside a pane that was not on a row began dragging
  the current selection — the column headings, the tab strip, the transfer bar,
  the preview overlay — so a six-pixel twitch on any of them started a file
  drag, and a drop at the end of it moved real files. Pressing on a row also
  collapsed the selection to that one row before the drag had read it, so a
  five-file drag arrived as one file, while the right-button drag carried all
  five and made it look like the listing's fault; what was selected when the
  button went down is what travels, and pressing a row that was *not* selected
  still means just that one. And the rubber band was anchored to the window
  rather than to the content, so dragging to the bottom edge scrolled the list
  out from under the rectangle and dropped every row that left the view —
  sweeping two hundred files kept the last screenful and lost the rest without
  saying so. Rows the band has taken stay taken now; drag back up over one
  still on screen and it comes off again.

- **Open, Open with and Run as administrator act on everything you picked.**
  Selecting five images and pressing Enter opened one of them, silently, with
  nothing to say the other four had been dropped — and choosing an application
  for them, or running them as administrator, did the same. All three take the
  whole selection now, up to fifteen things at once; past that the status line
  says so rather than starting four hundred processes. A folder caught up in a
  multi-selection is left alone, since there is no navigating into five folders
  at once.

- **Duplicating a folder no longer wrecks the folder it copies.** Copy and
  paste a folder without leaving the folder it lives in, and you were left with
  an empty "A - Copy" while the *original* filled up with "x - Copy.txt" beside
  every file and "sub - Copy" beside every subfolder — reported as a success
  throughout. Answering *Skip* for a folder that already existed was its own
  disaster: the folder entry was skipped and every file planned inside it still
  went in, merging two trees nobody asked to merge, and on a move the emptied
  source folder was then deleted by the tidy-up, so Skip lost the folder
  outright. And copying and pasting a file into the folder it already lives in
  could not work at all, since the conflict prompt could only offer to replace
  the file with itself. All three do what Explorer does now: a real duplicate
  beside the original, and a skipped folder untouched at both ends.

- **A folder cannot be copied or moved into itself.** Only a drag-and-drop
  refused it: Ctrl+V, *Copy to* and *Move to* all went ahead, and a cut folder
  pasted into one of its own subfolders was scrambled — the plan is built by
  walking the source, so a copy feeds itself its own output and a move
  dismantles the tree it is halfway through reading. The routes that claimed to
  check only asked whether the two paths were equal, which catches dropping a
  folder onto itself and misses the case that actually goes wrong, and one of
  them had no check at all. Every route now refuses by name, including dropping
  a selection onto a folder that is part of that selection, which a six-pixel
  twitch used to be enough to start. Copying into the parent is untouched, so
  Duplicate still works.

- **One file that cannot be copied or deleted no longer ends the batch.**
  Copying twelve files with the third open in another program copied two and
  gave up on nine, naming neither the file nor what was left undone; deleting
  did the same, and named the error rather than the file. Each item now stands
  or falls alone and the status line names the first one left behind and counts
  the others, the way copying should have all along. A folder you are not
  allowed to read is skipped and named too, before the copying starts — on
  Windows one protected directory anywhere under the selection used to stop the
  whole operation while it was still being planned, and on Linux it was
  swallowed, so the copy reported success having quietly left files behind. On
  Windows a refused move to the bin used to read "SHFileOperation returned 32",
  naming no file and quoting a number from an interface you have never heard
  of; the batch is now retried one file at a time so the line can say which one
  and what stopped it. That refusal also used to take the undo with it, leaving
  every file ahead of it with no way back at exactly the moment you reach for
  Ctrl+Z.

- **A copy keeps the file's dates and permissions, and leaves nothing behind
  when it fails.** Only the bytes were carried, so every copy landed dated
  today with default permissions: a copied shell script lost its executable
  bit, a private key came out readable by everyone, and a copied photo library
  was re-dated entirely — which nobody notices until the sort order is wrong
  months later. A cancelled or failed copy also left a truncated file under the
  final name, which opened and was silently incomplete, and worse when you had
  chosen to replace, because the original was already gone to make room for it.
  And fifty gigabytes onto a drive with room for thirty used to fill the disk
  and then fail somewhere in the middle; Vaktari asks for the room first now
  and says how much is needed against how much there is.

- **On Windows, deleting "report " no longer deletes "report".** A name ending
  in a space or a dot is legal on NTFS and arrives routinely from WSL, from a
  Linux SMB client and from git — and Windows quietly strips that last
  character before the question is asked, so an operation aimed at "report "
  landed on its neighbour instead. The listing showed the true name, and
  nothing on screen said the row you clicked and the file that went were two
  different things. Delete, trash, rename, copy and move now refuse such a name
  and say which character is the problem, which is what Explorer does. Delete
  refuses one file at a time so the rest of the selection still goes; copy and
  move stop before they start, because a move deletes what it believes it has
  copied.

- **A refused rename keeps what you typed, and refuses before it reaches the
  disk.** Typing a colon, or CON, or "..", tore the edit box down and reported
  the refusal as a status line a moment later, so correcting one character
  meant F2 and retyping the whole name; a name of nothing but spaces vanished
  without a word. The box stays open with the reason under it now, and the
  reason follows what you type, so a colon is answered the moment it appears.
  On Windows the check itself was empty-or-a-slash and nothing else, so a colon
  reached the filesystem and came back as the raw "The parameter is
  incorrect." — and `d:notes` was worse than an error, being drive-relative:
  the file left the listing altogether for whatever the current directory of D:
  happened to be. Device names, `.` and `..` are refused as well, and
  "CON.tar.gz" no longer slips past by having two extensions. On Linux a colon
  in a name stays perfectly ordinary.

- **Renaming several files at once works.** Renumbering img001, img002, img003
  to start at 2 reported "stopped after 0": the preview was right to let img001
  take img002's name, since img002 was being renamed too, but the rows were
  then applied in the order they were shown and the filesystem refuses a rename
  onto a name that is still occupied. The renames are ordered now, each chain
  worked from its far end, with a temporary name only where two files genuinely
  swap. F2 with several files selected also reaches that dialog rather than
  renaming the focused row and ignoring the rest without a word — and
  *Rename in bulk…* has stopped being offered for a single file, directly under
  the entry that handles that case.

- **The address bar can copy and paste, and understands what you type.** Ctrl+C,
  Ctrl+X, Ctrl+V, Ctrl+Z and both spellings of redo were claimed by the window
  before the box you were typing in ever saw them. Ctrl+V pasted the *files* on
  the clipboard into the folder behind the bar, or said "clipboard has no
  files" and left the field looking dead; Ctrl+C replaced the clipboard with
  whatever was selected in the listing, so copying a path out of the bar
  destroyed the thing you were copying; and Ctrl+Z, pressed to take back a
  typo, reversed your last copy, move or delete on disk. All six now go to the
  text cursor when one has the keyboard. Right-clicking the bar works too —
  opening its own Cut/Copy/Paste menu used to collapse the field back to
  breadcrumbs out from under the menu, taking the typed path with it. A
  relative path such as ".." or "src" is counted from the folder on screen
  rather than from wherever Vaktari was started, and a path to a file opens its
  folder and highlights it instead of failing as a missing directory. *Copy as
  path* puts every path you selected on the clipboard, one per line, rather
  than one of five with nothing said about the other four — and pasting one of
  those back into the bar works, where the quotes used to make it a relative
  path and produce a raw Windows error naming Vaktari's own folder. And Ctrl+L
  pressed twice keeps what you have typed: beginning to edit again used to
  reset the box to the folder you were in, so half a typed path disappeared,
  silently, for a keystroke whose only meaning is "put me in the address bar".

- **A listing that is not a folder stops pretending to be one.** The bin, Recent
  and This PC are views rather than places on disk, and everything that wanted
  a path was handed Vaktari's internal name for them: Ctrl+D pinned a sidebar
  place called "vaktari:trash" that could never be opened and had to be removed
  by hand, F4 opened a terminal in it, and Ctrl+L typed it into the address bar
  — the one box whose whole contract is that what it holds is a path you can
  read, edit and press Enter on. All three hold back now, and Ctrl+L opens the
  bar empty there. Properties did the same thing from the other side: Alt+Enter
  went round the check the menu entry makes, and Windows answers a question
  about a path that is empty with a size of zero, 1601 for every date and every
  attribute set, which reads as fact rather than as nothing. And pressing Enter
  on a binned row opened the path the item *used* to occupy — delete notes.txt,
  write a new notes.txt, and the bin row opened the new one with nothing at all
  to say so.

- **The keyboard stays where you put it.** Tab between split panes moved the
  highlight and left the keyboard behind, so the arrow keys went on moving the
  old pane's selection while Enter, Delete and Ctrl+C/X/V acted on the new one:
  arrow to a file, press Delete, and the wrong file went to the bin. Escape in
  the filter, Enter in the address bar and either answer to a confirmation each
  left focus on a control that had just been collapsed to nothing, so the arrow
  keys, Enter, Delete, Home, End and type-ahead were dead until you pressed F6
  or clicked. And a breadcrumb, a sidebar place, Back and Up are each a button,
  and each kept the focus after taking you somewhere, so Enter re-pressed the
  button that had just moved you. The listing takes the keyboard back, unless
  something else has deliberately claimed it.

- **A pane keeps its place.** Rebuilding the rows cleared the selection, so F5
  lost your place in a long folder and clicking a column heading quietly
  deselected everything you had picked; it is restored by path now, so a file
  that changed while you were looking at it keeps its highlight. Delete,
  Delete, Delete did not walk down the list either — once the rows had gone
  nothing was selected, so the next press had nothing to act on; the row after
  the ones you removed is selected instead, or the new last row when you
  deleted from the end. A new folder is selected as well as renamed, since the
  prompt used to open with nothing selected at all. And a load that failed left
  the last successful one's answer standing, so plugging the drive back in and
  retyping the same path did nothing at all until you had visited some other
  folder first. The item count and the empty message were worked out only
  when you navigated, too, so a finished download left "36 items" beside 37
  rows,
  and a folder that was empty when you arrived kept "this folder is empty"
  printed across the middle of it while real rows appeared underneath.

- **The filter is dropped when you leave the folder, takes wildcards, and says
  when nothing matches.** Type "report" to find something, open a folder from
  the results, and the new folder came up filtered by a word that had nothing
  to do with it — which reads as an empty folder. Refreshing where you are is
  not leaving, so that keeps what you typed. `*.png` hid everything, because
  the filter only ever asked whether a name contained the text and no name
  contains an asterisk, while the very same text works in the search box; a
  filter that looks like a pattern is now used as one. And a filter that
  matched nothing printed "this folder is empty" over a folder full of files,
  which reads as data loss — while clearing the filter, the one way out, was
  the one thing that message gave no reason to try. It names the filter now,
  because the box may be somewhere your eye is not.

- **Escape does what Escape does.** It closed nothing but the shortcut sheet:
  Settings, Properties, Batch rename, Share, Connection and the conflict prompt
  all had to be dismissed with the mouse — and the conflict prompt is the one
  that turns up in the middle of a copy, when both hands are already on the
  keyboard. Escape now presses each window's own Cancel, so abandoning a
  conflict prompt still cancels the copy behind it. In the listing it closes
  the preview, which is the one thing drawn *over* the rows and the one thing
  the key could not put away, and otherwise clears the filter text and any
  pending cut — the two things the F1 sheet has always promised it does. It no
  longer takes the filter bar away with it, which was a long walk back through
  a menu for a key people press to mean "never mind"; and a pending cut can now
  be abandoned from anywhere rather than only while the filter box happens to
  be open.

- **A space belongs to the name you are typing.** Space opens the preview panel,
  and it was claimed by the window ahead of every rule that knows a text box has
  the keyboard — so renaming a file to "My Report" flipped a preview open over
  the listing instead of putting a space in the name. Typing "new folder" to
  jump to it did the same on the fourth keystroke and threw the prefix away,
  which left every two-word name in a folder unreachable by typing. The gesture
  is unchanged and still listed under F1; it waits its turn now.

- **Group headings behave like the sort they belong to.** Reversing a sort
  reversed the files and left the headings where they were, so the listing read
  Today, Yesterday, This week downwards while the rows inside each band ran the
  other way. Grouping by Name or Modified drew every band twice — "Today" over
  the folders, then "Today" again over the files — because folders-first was
  applied ahead of the group. The bands were also worked out once when the
  listing was built, so deleting the first row of a band took its heading away
  and a download landing at the top of one got no heading at all. And a file
  saved at six in the evening was filed under "Later" west of Greenwich,
  because the band was read in UTC while the Modified column on the very same
  row said today. All four are right now.

- **Accented names sort with their own letter instead of after Z.** "Écoles",
  "Über" and "Ångström" sat below "Zebra" and were banded under '#' beside
  ".gitignore", because the comparison upper-cased each character and then
  compared code numbers — 'É' is 201 where 'Z' is 90, so a European folder
  threw its own names off the end of its alphabet. They band and sort under E,
  U and A now, with the accent deciding only between names that are otherwise
  identical, so "Ecoles" and "Écoles" land next to each other.

- **"Show item counts for folders" does something.** The setting saved and
  restored faithfully, both platforms could count a directory, and the Size
  column was wired all the while to something that printed an em dash for every
  folder whatever the setting said — so a feature that is on by default had
  never worked once. Folder rows count their contents as they scroll into view
  now, with the em dash standing in while a count is in flight or when the
  folder cannot be read, and the counting happens off the drawing thread so a
  slow share does not stutter the scroll.

- **The window is named after the folder you are looking at.** The title was
  worked out at startup and once more when Settings closed, and never
  otherwise: with the full-path option on it named your startup folder all
  session, and with it off the title bar read "Vaktari" and nothing else, ever.
  Four open windows meant four identical taskbar buttons and four identical
  alt-tab entries. It follows the folder now, tab switches included, and the
  bin and This PC are named the way they are labelled.

- **The sidebar's right-click menu opens at last.** It had never appeared for
  anything since it was written — not a pinned place, not a drive, not the bin.
  The menu is its own popup and inherits nothing from the row beneath it, so
  the guard deciding whether to show it asked for a row, got nothing, and
  cancelled every time; the row is handed over before the menu opens now, which
  is also what gives *Eject*, *Remove from places* and *Properties* something
  to act on. Every row carries *Open in new tab*, plus *Copy path* and
  *Properties* wherever it names a real folder. The bin's row carries *Empty
  the Recycle Bin*, named the way your platform names it — emptying it used to
  be reachable from exactly one place, the band of buttons that appears once
  you have navigated into the bin. And a place you pinned yourself can be given
  a name of your own: two folders both called "src" no longer sit there as two
  identical rows, told apart only by editing a file by hand.

- **Search answers the keyboard, lands on the file, and searches where you
  asked it to.** The results were a column of buttons, which carry no
  selection — so there was nothing for an arrow key to move and nothing for
  Enter to open, and a result could only be reached with the mouse; Enter now
  opens the top result straight from the box and Down puts the keyboard in the
  list. Choosing a hit took you to the right folder and then highlighted
  nothing whenever the file was read-only or a symbolic link, and a hidden hit
  had no row to land on at all, so opening one now turns hidden files on rather
  than leaving you in a folder that cannot show it; one that has since gone
  says so. "This folder only" is ticked by default and scoped itself to the
  pane's path, which for This PC, the bin or a recents listing is an internal
  name rather than a folder — so a search from This PC hunted for a folder that
  does not exist and then reported no results, a flat denial about the whole
  machine; such a listing now searches everywhere and the box says so. A
  one-character query runs rather than answering "keep typing…", since "b" for
  a folder of build outputs is a real question. And on Linux, where KDE's index
  answers, "this folder only" was applied as a plain text prefix, so a search
  scoped to `proj` also returned hits from `projects` and `proj-old` — the
  same search gave different answers depending on whether the index happened
  to be running.

- **The transfer bar keeps its buttons, and keeps its last word.** The progress
  line ends with the file being copied, which can run to 255 characters, and it
  was laid out with no width to fit into — so pause and cancel ended up past
  the right edge, and they are the only route to either command anywhere in
  Vaktari. The line trims now, with the whole of it in a tooltip. Every failure
  message was also written and hidden in the same instant, because the bar
  showed only while something was running: a copy that skipped a locked file
  reported a clean run, which is the whole reason the engine carries on past a
  locked file rather than stopping. The bar stays while there is something to
  say, and pause and cancel give way to a dismiss once the work is over.

- **A listing that has lost track reloads itself, and a busy folder no longer
  freezes the window.** The watcher behind each pane has a fixed buffer, and
  unpacking an archive or finishing a large download in the folder on screen
  overruns it — after which changes were dropped in silence and what you saw
  drifted out of date with no cure but F5. The buffer is larger, an overrun
  re-reads the folder, and a folder that is deleted or a share that drops says
  so instead of leaving you on rows for a place that is gone. Every single
  change also re-checked the whole listing for look-alike names and rewrote the
  count, on the thread that draws, so extracting an archive into the folder on
  screen stopped the application answering for seconds; both now happen once,
  a fifth of a second after things stop moving. On Linux a row the watcher
  noticed also carried less than the same row from a full listing — it lost
  read-only and symlink — so the selection would not settle on a file that had
  just been created, and a symlink that appeared while you watched was drawn as
  the thing it points at.

- **Measuring a mixed selection counts the files too, and Stop stops it.**
  Properties reads "12 MB in 3 files, plus 1 folder unmeasured" until you press
  *Measure*, and the answer that replaced it held only what the folder
  contained — a smaller number than the line before it, from the one action
  whose whole purpose is to make that number right. The same button both starts
  and stops the walk, and it greyed itself out the instant the walk began, so
  measuring a home folder ran to the end whatever you pressed.

- **Restoring one thing from the bin restores one thing.** A row there carries
  the path the file came from, and two entries can honestly share one — delete
  a file, restore it, delete it again, which is exactly when somebody reaches
  for restore. Both came back, the second landing beside the first under a
  made-up name. The row you picked is the one that returns, and it is the most
  recent of them.

- **Dragging onto a breadcrumb moves things up the tree, and a folder below the
  fold can be reached.** The crumbs sit above the listing, so a drag over
  "Documents" found the pane underneath and offered the folder you were already
  in: the cursor said yes, the drop happened, and the file went exactly where
  it already was. The listing also held still for the whole gesture, so
  dropping into a folder below the fold meant abandoning the drag, scrolling,
  and starting again; rest near the top or bottom edge and it moves now, the
  same as it already did while you drew a rubber band. And a row in the bin can
  no longer be picked up at all — its path is where the item used to be, so the
  drag armed, the cursor showed a payload, and letting go did nothing and said
  nothing; it is refused now, with a line pointing at Restore.

- **Closing the last tab closes the window.** With one tab open, Ctrl+W and the
  tab's ✕ were drawn, clickable, tooltipped and inert: a side is not allowed to
  be left with no tabs, so both routes hit that rule and returned without a
  word. They now do what Explorer and every browser do. A split still collapses
  instead, because there is another side to fall back to. And one folder called
  "Screenshots from the trip to Norway 2024" no longer grows a tab hundreds of
  pixels wide and pushes the others off the strip — titles are capped and
  trimmed, with the full path in the tooltip.

- **Text and marks you have to read are readable.** Small accent-coloured text —
  "offline", "look-alike", the tile badge, the settings status lines, the
  link-style buttons — came out at 3.4:1 against the chrome in the dark theme,
  which is the one that ships as the default; it now takes a shade derived from
  whatever the accent turns out to be, including a colour your desktop hands
  over. The column headings, the status bar, the inactive tab titles and the
  breadcrumb ancestors sat a hair under the standard, an earlier pass having
  raised that grey for the listing and never checked the chrome. On a selected
  row the type, size, date and path cells kept the dim grey chosen against the
  listing's own background while the row was filled with the accent colour —
  the one row you are certainly looking at. The version-control letters were
  coloured for a dark listing and used on both, so on a white one the M came
  out at 2.20:1, and the letter is the whole signal. Row banding was judged in
  raw colour values and sat behind a setting that is off by default, so the
  scheme almost everybody sees striped at 1.04:1, which is to say not at all.
  The age ramp had six names and five shades, so a file a year old and one a
  decade old were drawn identically. The small instructional hints were dimmed
  twice over. The theme cards in Settings and the overwrite dialog's panel had
  no background at all, having asked for a colour that nothing defined. And the
  sidebar places, This PC, the breadcrumbs and the four sortable column
  headings had no accessible name, so a screen reader announced each as a bare
  button.

- **The monospace font you chose reaches everything monospaced.** The setting
  was arriving at two labels in the details pane and nowhere else: the
  batch-rename pattern box, the filter and address bars, the conflict window's
  paths, the status line and the nine monospace fields in Properties all asked
  the system for a generic monospace family instead, so the application showed
  two different fixed-width fonts at once.

- **An empty submenu, a script that needed a restart, and a limit box reading
  zero.** *Open with* was drawn whenever anything was selected rather than
  when there was anything to offer, so every folder — and on Linux any file
  whose type would not resolve — drew a row with a chevron and an empty popup
  behind it. Scripts and templates were both read once when the pane was built,
  so the menu that invites you to go and add a script never noticed the one you
  added; they are re-read each time the menu opens. And the preview size boxes
  in Settings showed a literal "0" beside help text promising that blank or 0
  means no limit — which reads as "skip files larger than nothing", and also
  hid the placeholder written to say what was really going on. With no limit
  set, the box is empty.

- **On Linux, "open containing folder" from another program opens it.** The
  desktop entry the installer writes asked the system for URIs and nothing
  decoded one, so on GNOME, Xfce, Cinnamon and plain xdg-open the folder
  arrived as `file:///home/me/Documents`, was judged not to exist, and was
  dropped without a word — on the primary install route, which therefore could
  not open a folder at all. The entry now asks for paths, and a `file://` URI
  is understood anyway, because a portal can still send one.

- **On Linux, dragging files no longer freezes the window.** Deciding whether a
  drag copies or moves asks which volume each path is on, and answering it
  walked every mount on the machine — twice per file, for every twitch of the
  pointer — so a two-hundred-file drag meant hundreds of scans a second, and a
  single hung network mount stopped the window dead. The mounts are read once
  per drag from a text file now, touching no filesystem at all. A stale mount
  counts as its own volume, so a plain drag out of one copies and leaves the
  original where it is.

- **On Linux, deleting from a USB stick uses that drive's own bin, and a moved
  folder leaves nothing behind.** Everything went to the bin in your home
  folder, so every delete from another drive quietly *copied* the file across
  first — twenty gigabytes of video, slowly, onto a disk you were not deleting
  from — and the entry then died with the stick when you unplugged it. Deletes
  now land at the top of the volume the file lives on, the same place Dolphin
  and Nautilus put them, with your home bin as the fallback where the top of a
  volume is not yours to write; the listing and restore sweep every drive
  rather than only home, so items other desktops trashed onto a drive are
  finally visible, and emptying no longer skips them while reporting them as
  removed. Separately, a moved tree took its files and left every directory
  standing at the source, all the way down, so the folder you had just moved
  was still sitting there, empty.

- **On Linux, applying permissions to a folder and everything in it no longer
  follows links out of it.** The recursive apply walked into symbolic links and
  set the mode on whatever they pointed at, so a folder holding a link to your
  photo library, given a recursive 700, quietly rewrote the real library, and a
  link pointing back up the tree never finished at all. Links are reported,
  left alone and counted as skipped, so the summary is honest about the tree
  not being uniform. The same rule now guards the other walks that used to
  follow them: the folder size in Properties, the bin's size, and a search
  under a folder that links to your home directory, which used to walk your
  whole home directory.

- **On Linux, "Open with" stops offering applications it cannot run, and stops
  going missing.** vim, nano and htop all register themselves for plain text,
  so they appeared for any text file — and choosing one started it with no
  terminal to appear in: the launch reported success and nothing whatever
  showed up. Such an application is now wrapped in a terminal emulator, with
  the flags that particular terminal wants, and is not offered at all on a
  machine with no terminal installed. The submenu also disappeared
  intermittently, because working out a file's type shared a budget with the
  row icons and never waited its turn, so a right-click while a listing was
  still drawing icons got no answer — and no answer meant no options and no
  submenu drawn at all. The menu and the properties dialog have a small budget
  nobody else can spend, and an ordinary extension is answered from the
  desktop's own type database rather than by starting a process to be told
  that .txt is text.

- **On Linux, Documents and Downloads stay in the sidebar when your config
  lives somewhere else.** Two parts of Vaktari read the same list of user
  folders and disagreed about where it is: the sidebar looked only in
  `~/.config`, so a session with XDG_CONFIG_HOME set elsewhere lost those rows
  while the icons naming the very same folders carried on working. There is one
  reader now, the one that honours the setting, and a key written with a
  leading space is no longer missed.

- **On Windows, a program you start runs in its own folder, and a mapped drive
  counts as remote.** Opening an `.exe` or a `.bat` handed it Vaktari's working
  directory instead of its own, so a portable tool or a script that reads a
  file sitting beside it failed — and the failure looked like the program being
  broken rather than like how it was started. And Vaktari knew only about the
  shares it had discovered itself, which deliberately skips lettered
  connections, so a drive mounted as `Z:` was judged local and every visible row
  on it fired off a directory read over SMB just to draw its icon, which is the
  round-trip storm that check exists to prevent.

## [0.9.16] — 2026-08-29

### Fixed

- **The Proton Drive link now actually reaches your clipboard.** The CLI
  answers with the node's whole sharing state and keeps the URL nested
  inside it; 0.9.15 only looked at the top level, so the link was created
  and then reported as missing — "the CLI made the link but did not say
  where it is". The parser now finds the URL wherever the answer keeps it,
  fragment and all (that part is the decryption key). If you hit this,
  just share the item again: the link already exists, and re-sharing
  fetches the same URL onto the clipboard.

- **Stop sharing speaks the CLI's real verb.** The removal command is
  `remove-url`, which Proton's public docs never named — it was a marked
  guess until now, and every command Vaktari sends has since been pinned
  against the real binary in a live share round-trip.

## [0.9.15] — 2026-08-29

### Changed

- **Sharing via Proton Drive is one click from nothing.** The separate
  "Install…" entry is gone: *Share via Proton Drive* now shows for anything
  in your drive folder whether or not the CLI is on the machine, and the
  click does whatever is missing in order — downloads Proton's tool
  (~120 MB, the status bar narrates), opens your browser to sign in the
  first time, then makes the link and puts it on the clipboard. While the
  download runs, the row reads "Setting up Proton Drive sharing…".

### Fixed

- **The Proton Drive folder is now found wherever you actually put it.**
  0.9.14's auto-detection only knew the default location in your user
  profile, so a sync folder moved to another drive — which the app happily
  allows — left the whole feature invisible. Vaktari now reads the sync root
  from the Proton Drive app's own records first, which names the exact
  folder wherever it lives, and falls back to the old layout guess. The
  setting still wins over both when filled in.

## [0.9.14] — 2026-08-29

### Added

- **Proton Drive sharing installs itself.** When a file sits inside your
  Proton Drive folder and the CLI is not on the machine, the Share submenu
  offers *Install Proton Drive sharing* — one click downloads Proton's own
  tool into Vaktari's folder and the share entries appear on the next
  right-click, no restart, no hunting through a vendor site. The same
  promise the copyparty entry already made, kept for links.

- **The Proton Drive folder finds itself.** With the setting left empty,
  Vaktari looks where the Proton Drive app puts "My files" — directly under
  *Proton Drive* in your user folder, or one account folder down. The guess
  is only taken when it is unambiguous, and the setting always wins over it.

### Fixed

- **The Proton menu entries actually work again.** Moving them inside the
  Share submenu in 0.9.13 broke how they found their pane, and every click
  on them did nothing without a word said. They act again, and the new
  install entries use the corrected route.

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
