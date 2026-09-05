using System.Text.Json.Serialization;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Core.Settings;

/// <summary>What the window shows when it opens.</summary>
public enum StartupLocation
{
    /// <summary>Folders, tabs and window state from last time. The default, and
    /// the reason this project exists — forgetting them was the original
    /// complaint about the tool it replaces.</summary>
    RestoreSession,
    HomeFolder,
    SpecificFolder,

    /// <summary>
    /// The listing of every drive on the machine — "This PC" on Windows,
    /// "This computer" on Linux.
    ///
    /// **A member of its own, because SpecificFolder could never carry it.**
    /// That case is gated on <c>Directory.Exists</c>, and the drive listing is
    /// reached by the virtual path <c>vaktari:computer</c>, which no directory
    /// check could satisfy — so typing it into the startup folder box drew the
    /// "that folder is not there" warning under the box, and the next launch
    /// opened the home folder instead. Explorer's own startup choice is
    /// exactly these two, This PC or Home, so the one Vaktari could not offer
    /// was the first one a Windows user went looking for.
    ///
    /// Appended rather than slotted in beside HomeFolder, where it reads more
    /// naturally. The enum is persisted: <c>SettingsJsonContext</c> writes it by
    /// NAME today, which is what makes the declaration order free — and a member
    /// added in the middle renumbers SpecificFolder underneath every
    /// settings.json already written the moment that option comes off. Both
    /// halves are pinned in Vaktari.Core.Tests.StartupLocationPersistenceTests.
    ///
    /// **What appending cannot buy is the NAME, and the cost of that was
    /// measured: a settings.json saying "Computer", opened by a build without
    /// this member, loses every preference in the file and not just this one.**
    /// The deserializer throws on a name it does not have, JsonSettingsStore
    /// treats an unreadable file the same as an absent one, and the next Save
    /// writes the defaults over it — sorting, status bar, bin limits and all.
    /// Measured in
    /// StartupOnTheDriveListingTests.A_startup_name_this_build_does_not_have_costs_the_whole_file.
    /// </summary>
    Computer,
}

public enum DateStyle { Relative, Absolute }

/// <summary>What the size column means for a folder.</summary>
public enum FolderSizeMode { ItemCount, ContentSize, None }

public enum TrashLimitAction { Warn, DeleteOldest, DeleteLargest }

/// <summary>
/// Single or double click to open. <see cref="System"/> follows the desktop's
/// own setting, which is what Dolphin does — it does not offer its own control
/// at all, deferring to System Settings. Vaktari keeps the override because it
/// also has to run on Windows, where there is no equivalent to defer to.
/// </summary>
public enum ActivationClick { System, Single, Double }

/// <summary>
/// General behaviour. Dolphin splits this across four tabs — Behavior,
/// Previews, Confirmations, Status Bar — but those are a UI grouping rather
/// than a data one, so they are flat here and the dialog does the grouping.
///
/// **Every default below is the behaviour the application has today.** That is
/// deliberate and load-bearing: introducing this record must not change how
/// anything works, so that when it is threaded through the call sites the only
/// thing to verify is that nothing changed.
/// </summary>
public sealed record GeneralSettings
{
    // ---- sorting ----------------------------------------------------------

    /// <summary>file2 before file10. NaturalOrder does this unconditionally today.</summary>
    public bool NaturalSorting { get; init; } = true;

    /// <summary>False today: NaturalOrder.Compare upper-cases both sides.</summary>
    public bool CaseSensitiveSorting { get; init; }

    /// <summary>
    /// Let folders fall wherever the sort field puts them, instead of banding
    /// them above the files.
    ///
    /// **The comparer said "Directories first, always" and meant it.** The
    /// tie-break in <c>PaneViewModel.CompareWithin</c> returned on
    /// <c>a.IsDirectory != b.IsDirectory</c> before it looked at the field at
    /// all, and no setting was consulted anywhere — so sorting a folder by
    /// modified date could not answer "what changed here most recently" if the
    /// answer was a folder, and there was no key in settings.json to change it.
    /// Explorer, Dolphin and Nautilus all offer this switch.
    ///
    /// **NAMED FOR ITS ZERO VALUE, like <see cref="ViewSettings.KeepWidthAfterPanelClose"/>
    /// and for the same proven reason:** deserialization here does not run
    /// property initializers, so a key absent from a settings.json written
    /// before this existed arrives as <c>default(bool)</c>. false therefore has
    /// to BE the shipped behaviour — and folders-first is the shipped
    /// behaviour, so false keeps it and nobody's listing reorders itself on
    /// upgrade.
    /// </summary>
    public bool MixFoldersWithFiles { get; init; }

    // ---- behaviour --------------------------------------------------------

    /// <summary>
    /// False: every folder uses the same view. True: a folder remembers the
    /// view, sort and grouping it was last given. Off by default because it is
    /// the behaviour the application has always had.
    /// </summary>
    public bool RememberViewPerFolder { get; init; }

    public bool ShowTooltips { get; init; } = true;

    /// <summary>Already the behaviour — Tab moves between split halves.</summary>
    public bool TabSwitchesSplitPanes { get; init; } = true;

    /// <summary>
    /// Where a file's icon comes from.
    ///
    /// **The bundled set is not to everyone's taste, and that is fair.** It is
    /// drawn for this application and it is consistent, which is exactly why
    /// somebody who knows their own desktop's icons may prefer those — an
    /// executable showing its real icon, a folder showing the one they gave it.
    ///
    /// False is the shipped behaviour and stays the default: the set drawn for
    /// this application is the one it looks right in.
    /// </summary>
    public bool UseSystemIcons { get; init; }

    /// <summary>
    /// A downloaded icon theme's folder — the one holding index.theme, which is
    /// what you get when you extract Papirus or Tela.
    ///
    /// **Wins over both other sources when set**, because it is the most
    /// deliberate of the three: somebody found a theme, downloaded it and
    /// pointed at it. Empty is the normal state.
    ///
    /// A path rather than a name, because on Windows there is no registry of
    /// installed themes to look a name up in — the folder is the whole of what
    /// we know about it. A path naming a folder that has since gone is ignored
    /// rather than honoured into a listing with no icons at all.
    /// </summary>
    public string IconThemeFolder { get; init; } = "";

    /// <summary>
    /// Which terminal F4 opens, by id — "warp", "windows-terminal", "git-bash".
    ///
    /// Empty means "whichever is found first", which is what the application
    /// always did and remains right for the many machines with exactly one.
    ///
    /// **An id rather than a path or a name.** A path breaks when the terminal
    /// updates itself into a new versioned folder, and a display name is the
    /// sort of thing that gets tidied up in a later release, silently resetting
    /// everybody's choice. An id naming something not installed is ignored, so
    /// uninstalling a terminal cannot break F4.
    /// </summary>
    public string PreferredTerminal { get; init; } = "";

    /// <summary>
    /// Where the Proton Drive sync folder lives on this machine, or empty when
    /// the user has not said. A setting rather than a detection: the official
    /// client lets the folder go anywhere (this machine keeps it on D:), and
    /// guessing wrong would map files to remote paths that do not exist.
    /// </summary>
    public string ProtonDriveFolder { get; init; } = "";

    /// <summary>
    /// Dolphin closes the inactive pane. Vaktari keeps it in
    /// RememberedRightPane so reopening the split returns to where it was, and
    /// that difference is deliberate — closing a split should not be a quiet
    /// way to lose a location. False keeps Vaktari's behaviour.
    /// </summary>
    public bool ClosingSplitDiscardsOtherPane { get; init; }

    // ---- what gets remembered ---------------------------------------------

    /// <summary>
    /// Whether opening a file or a folder is recorded in the recent lists.
    ///
    /// **It was recorded unconditionally, and there was no way to stop it or
    /// to empty it.** Every folder opened and every file opened went in, no
    /// setting was consulted, and the only route out was a per-row "Forget"
    /// that needs the entry still on screen.
    ///
    /// **Defaulted to ON, which is the one judgement here.** Both references
    /// record by default and the recent lists are a feature people use; a
    /// privacy switch that is off out of the box is a feature nobody finds.
    /// Off stops new entries; it does not empty what is already there, because
    /// silently deleting somebody's list from a checkbox is not what the
    /// checkbox says. Emptying is its own button.
    /// </summary>
    public bool RememberRecent { get; init; } = true;

    /// <summary>
    /// Whether a search that is run is kept in the search history.
    ///
    /// **Nothing recorded what had been searched for at all**, so there was
    /// nothing to switch off — and the first thing a search history needs is
    /// the switch, because it is a list of the questions somebody has asked
    /// their own machine.
    ///
    /// **NAMED FOR ITS ZERO VALUE, and this is the important part.** The
    /// paragraph on <see cref="ViewSettings.KeepWidthAfterPanelClose"/> says
    /// deserialization here does not run property initializers, and that claim
    /// was RE-MEASURED for this property rather than taken on trust:
    /// <c>{"version":1,"general":{"showTooltips":true}}</c> deserialized
    /// through <see cref="SettingsJsonContext"/> came back with
    /// <see cref="RememberRecent"/> FALSE, though it is declared
    /// <c>= true</c>. So a <c>= true</c> default is decorative for every
    /// settings.json written before the key existed — which is every one that
    /// exists — and a positively named <c>RememberSearches = true</c> would
    /// have shipped the feature switched off for everybody upgrading, with a
    /// checkbox that says it is on.
    ///
    /// So the wanted behaviour IS the zero: false means the history is kept,
    /// which is what should happen when nobody has said otherwise. The dialog
    /// still shows it the positive way round, as "Remember what you search
    /// for" — the inversion belongs at the one control that reads it, not in
    /// the file.
    ///
    /// On by default for the reason <see cref="RememberRecent"/> gives: a
    /// privacy switch that ships off is a feature nobody finds. Turning it on
    /// stops NEW searches being recorded; it does not empty what is already
    /// there, because silently deleting somebody's list from a checkbox is not
    /// what the checkbox says. Emptying is its own button.
    /// </summary>
    public bool ForgetSearches { get; init; }

    // ---- previews ---------------------------------------------------------

    public bool ShowPreviews { get; init; } = true;

    /// <summary>Megabytes; 0 means no limit, which is the behaviour today.</summary>
    public int MaxLocalPreviewMegabytes { get; init; }

    /// <summary>
    /// Separate from the local limit because it is the one that matters here:
    /// a thumbnail on an SMB or SFTP mount pulls the whole file over the
    /// network. 0 means no limit, which is the behaviour today.
    /// </summary>
    public int MaxRemotePreviewMegabytes { get; init; }

    // ---- confirmations ----------------------------------------------------

    /// <summary>No confirmation today — trash is reversible.</summary>
    public bool ConfirmMoveToTrash { get; init; }

    /// <summary>Confirmed today, with real buttons rather than a bare key path.</summary>
    public bool ConfirmPermanentDelete { get; init; } = true;

    public bool ConfirmClosingMultipleTabs { get; init; }

    // OnOpeningExecutable lived here: declared, defaulted, serialised into
    // settings.json and read by nothing, with no control in the settings
    // dialog — so nobody could ever have set it, and its default was the
    // behaviour anyway. This is Nautilus's "Executable Text Files" preference,
    // and the reason it was inert is that the interesting values need a
    // prompt: Ask has to ask, and RunScript has to decide what "run" means for
    // a file the user may not have read. That is a feature with a dialog, not
    // a field — so the field goes rather than sit here looking finished.

    // ---- status bar -------------------------------------------------------

    public bool ShowStatusBar { get; init; } = true;

    public bool ShowFreeSpace { get; init; } = true;
}

public sealed record StartupSettings
{
    public StartupLocation ShowOnStartup { get; init; } = StartupLocation.RestoreSession;

    /// <summary>Only read when ShowOnStartup is SpecificFolder.</summary>
    public string? StartupFolder { get; init; }

    public bool BeginInSplitView { get; init; }

    public bool ShowFilterBar { get; init; }

    public bool LocationBarEditable { get; init; }

    public bool ShowFullPathInTitleBar { get; init; }
}

/// <summary>Settings that apply to one layout only.</summary>
public sealed record IconsViewSettings
{
    /// <summary>Minimum width reserved for an item's label.</summary>
    public int TextWidth { get; init; } = 120;

    public int MaximumLines { get; init; } = 2;

    /// <summary>
    /// Extra gap between tiles, in pixels at 100% scale. Zero is today's
    /// behaviour: the grid panel already reserves six pixels for the item
    /// template's margin, and this is added on top of that rather than
    /// replacing it — a "spacing" of zero that clipped every tile would be a
    /// setting that appears to be broken.
    /// </summary>
    public int Spacing { get; init; }
}

public sealed record CompactViewSettings
{
    public int MaximumTextWidth { get; init; } = 180;

    /// <summary>Extra gap around each compact cell, in pixels at 100% scale.</summary>
    public int Spacing { get; init; }
}

public sealed record DetailsViewSettings
{
    public FolderSizeMode FolderSize { get; init; } = FolderSizeMode.ItemCount;

    /// <summary>AgeConverters renders relative dates today.</summary>
    public DateStyle DateStyle { get; init; } = DateStyle.Relative;

    // Column visibility is NOT here. It lives on TabState: a reference listing
    // beside a working one wants different columns, and a choice made on one
    // side of a split must not move the other.
}

/// <summary>
/// Defaults a pane starts from. Panes still scale independently afterwards —
/// these are the starting values, not a cap, because per-pane scaling is an
/// accessibility feature and a global setting must not take it away.
/// </summary>
/// <summary>
/// Version-control decorations. Its own group rather than a line in
/// <see cref="GeneralSettings"/>, because the feature already has more than one
/// dimension and a second provider would land here rather than widening
/// something unrelated.
/// </summary>
public sealed record VcsSettings
{
    /// <summary>
    /// On by default. The decoration only appears inside a repository, so for
    /// anyone who never opens one it costs nothing — and a feature nobody can
    /// see until they find a checkbox may as well not exist.
    /// </summary>
    public bool ShowDecorations { get; init; } = true;
}

/// <summary>
/// What to do when the details panel is asked for and there is not room.
/// </summary>
public enum NarrowPanelBehaviour
{
    /// <summary>Grey the toggle out. The window keeps the size you gave it.</summary>
    DisableToggle,

    /// <summary>Widen the window to make room. The window manager still has the
    /// final say — it will clamp to the screen.</summary>
    GrowWindow,
}

/// <summary>
/// Which lightness the bundled scheme uses.
///
/// **Separate from <see cref="ViewSettings.FollowDesktopColours"/>, which is
/// about hues.** That flag decides whether the desktop's scheme and accent are
/// layered over the bundled one; this decides only whether the result is light
/// or dark, and the two compose — a desktop-coloured window still honours a
/// forced lightness.
/// </summary>
public enum ThemeMode
{
    /// <summary>Whatever the desktop is set to. The default, and the only value
    /// that keeps up when the desktop changes while Vaktari is running.</summary>
    FollowDesktop,

    Light,
    Dark,
}

public sealed record ViewSettings
{
    /// <summary>
    /// Default is <see cref="NarrowPanelBehaviour.DisableToggle"/>: a window
    /// that resizes itself because you pressed a toggle is surprising, and the
    /// less surprising option is the better default even though the other is
    /// arguably more helpful.
    /// </summary>
    public NarrowPanelBehaviour NarrowDetailsPanel { get; init; }

    /// <summary>
    /// Leave the window wide after the panel that needed the room is closed.
    /// Default (false) hands the width back.
    ///
    /// **NAMED FOR ITS ZERO VALUE ON PURPOSE, AND THIS IS THE IMPORTANT PART.**
    /// Deserialization here does NOT run property initializers: a key absent from
    /// `settings.json` arrives as `default(T)`, not as the declared default —
    /// PROVEN by a control that printed `restoreWidth=False` from the file while a
    /// freshly constructed record printed `True`. So a `= true` default is
    /// decorative for any file written before the property existed.
    ///
    /// The fix that does not depend on knowing why: phrase the setting so the
    /// wanted behaviour IS the zero. `false` means "give the width back", which is
    /// what should happen when nobody has said otherwise.
    ///
    /// Only meaningful alongside <see cref="NarrowPanelBehaviour.GrowWindow"/> —
    /// nothing was grown otherwise, so there is nothing to give back.
    /// </summary>
    public bool KeepWidthAfterPanelClose { get; init; }

    /// <summary>
    /// Whether the desktop's colours and font are layered over the application's
    /// own scheme.
    ///
    /// **Off by default, and that is the whole shape of the decision.** The
    /// bundled scheme is a considered look; a file manager that repaints itself
    /// to match Plasma the first time it starts is a surprise rather than a
    /// courtesy. But the reader for those colours already exists on both
    /// platforms and used to be computed and discarded, so the cost of offering
    /// the choice is one flag.
    /// </summary>
    public bool FollowDesktopColours { get; init; }

    /// <summary>
    /// Light, dark, or whatever the desktop says.
    ///
    /// **Defaults to following the desktop, and that default matters more than
    /// the override does.** Before there was a light scheme at all the window
    /// was dark unconditionally, which on a machine set to light meant Fluent
    /// wrote near-black text onto dark surfaces — 1.02:1, invisible. Following
    /// the desktop is what stops that; being able to overrule it is a
    /// preference on top.
    /// </summary>
    public ThemeMode ThemeMode { get; init; } = ThemeMode.FollowDesktop;

    /// <summary>
    /// A tick box ahead of every row, and one on the list heading that ticks
    /// or clears the lot.
    ///
    /// **Every route to a multi-selection went through a modifier or a drag.**
    /// Ctrl+click, shift+click and the rubber band were the whole of it, so
    /// somebody on a trackpad, working one-handed, or simply unaware that ctrl
    /// does anything could pick exactly one file at a time.
    ///
    /// **NAMED FOR ITS ZERO VALUE, like KeepWidthAfterPanelClose above and for
    /// the same proven reason:** deserialization here does not run property
    /// initializers, so a key absent from `settings.json` arrives as
    /// `default(T)` rather than as the declared default. `false` therefore has
    /// to BE the wanted behaviour — and it is. Explorer ships its boxes off
    /// behind a View tick and Dolphin ships them on; this follows Explorer,
    /// because the boxes take a slot out of the name column and a listing that
    /// grows one uninvited is a worse first impression than a feature nobody
    /// finds.
    ///
    /// Here rather than in <see cref="DetailsViewSettings"/>: all three
    /// layouts draw them, so a per-layout home would have been three copies of
    /// one answer.
    /// </summary>
    public bool ShowSelectionBoxes { get; init; }

    /// <summary>
    /// Leave the extension off the name a row draws — Explorer's "File name
    /// extensions", unticked.
    ///
    /// **There was no such switch, and the name column always drew the whole
    /// file name.** <c>FileKind.DisplayName</c> hid exactly two extensions —
    /// .lnk on Windows and .desktop on Linux, both because the platform's own
    /// shell hides them — and nothing else, ever, with no key in settings.json
    /// to say otherwise.
    ///
    /// Here rather than in <see cref="DetailsViewSettings"/> for the reason
    /// <see cref="ShowSelectionBoxes"/> gives above it: all three layouts bind
    /// the same converter, so a per-layout home would be three copies of one
    /// answer.
    ///
    /// **NAMED FOR ITS ZERO VALUE**, for the reason stated on
    /// <see cref="KeepWidthAfterPanelClose"/>: an absent key arrives as
    /// <c>default(bool)</c>, so false has to mean the shipped behaviour, and
    /// the shipped behaviour is that extensions are shown. The dialog asks the
    /// positive question — the checkbox reads "Show file name extensions" —
    /// and the view model inverts it, exactly as it does for the panel width.
    /// </summary>
    public bool HideFileExtensions { get; init; }

    // ---- what a pane starts as --------------------------------------------
    //
    // **There was no default layout, so every pane began in Details sorted by
    // name.** PaneViewModel's fields said `ViewMode.Details`, `SortField.Name`
    // and `GroupMode.None` as literals and nothing anywhere could say
    // otherwise: somebody who works in the large grid got Details from the tab
    // strip's "+", from a new window and from the second half of every split,
    // and the settings file had no key to change it. The session remembered the
    // tabs that were open — TabState carries View, Sort, SortDescending and
    // GroupBy — and the per-folder store answered for folders already given a
    // layout; between them they remembered where you had been and nothing about
    // where you were going.
    //
    // **All four are named for their zero value**, like ShowSelectionBoxes and
    // KeepWidthAfterPanelClose above. RE-MEASURED HERE rather than taken from
    // those notes: `DefaultView` was given a `= ViewMode.Grid` initializer and
    // a settings.json carrying only `views.themeMode` was deserialized through
    // SettingsJsonContext — it came back Details, so the initializer is not run
    // for an absent key and `default(T)` is what every upgrading install gets.
    //
    // The enum declarations make that free rather than a compromise: `ViewMode`
    // is `{ Details, Grid, Compact }`, `SortField` is `{ Name, Size, Modified,
    // Kind }` and `GroupMode` starts at `None`, so zero already IS the
    // behaviour every install has today.

    public ViewMode DefaultView { get; init; }

    public SortField DefaultSort { get; init; }

    public bool DefaultSortDescending { get; init; }

    /// <summary>Only the details view draws bands, but the pane keeps the
    /// choice in all three — so this is a pane default like the other three
    /// rather than a member of <see cref="DetailsViewSettings"/>.</summary>
    public GroupMode DefaultGroupBy { get; init; }

    /// <summary>Null means follow the desktop font from kdeglobals.</summary>
    public string? CustomFontFamily { get; init; }

    /// <summary>
    /// How large the interface's text is drawn, before any per-pane zoom: 1.25
    /// draws every label, row, tab and status line a quarter larger.
    ///
    /// **NAMED FOR ITS ZERO VALUE, like KeepWidthAfterPanelClose above and for
    /// the same proven reason** — deserialization does not run property
    /// initializers, so a key absent from a settings.json written before this
    /// existed arrives as <c>default(double)</c>. Zero therefore has to mean
    /// something wanted, and it means "follow whatever the desktop's own
    /// text-size setting says", which is where an upgrading install should
    /// start: the desktop has already been told how big this person needs text,
    /// and Vaktari was the only thing on screen ignoring it.
    ///
    /// Read through <c>InterfaceText.Scale</c>, which clamps it — a file edited
    /// by hand can say 40 as easily as 1.4.
    /// </summary>
    public double InterfaceTextScale { get; init; }

    public IconsViewSettings Icons { get; init; } = new();
    public CompactViewSettings Compact { get; init; } = new();
    public DetailsViewSettings Details { get; init; } = new();
}

public sealed record NavigationSettings
{
    public ActivationClick OpenItemsWith { get; init; } = ActivationClick.System;

    /// <summary>
    /// What Backspace does in a listing.
    ///
    /// **Two file managers taught two different habits, and Vaktari only
    /// answered one of them.** Explorer's Backspace goes Back through history;
    /// Dolphin's goes up to the parent folder. Vaktari hard-coded Back, so
    /// somebody arriving from Dolphin pressed it at the bottom of a deep tree
    /// and was thrown to wherever they had been ten minutes earlier — a
    /// silent, confident wrong answer rather than a key that does nothing.
    ///
    /// Back stays the default: it is what shipped, and it is what the larger
    /// of the two audiences expects. Alt+Left and Alt+Up keep doing their own
    /// jobs whichever way this is set, so nobody loses a route by flipping it.
    ///
    /// **Named for its zero value on purpose**, like
    /// <see cref="ViewSettings.KeepWidthAfterPanelClose"/> above: deserialization
    /// does not run property initializers, so a key absent from a settings.json
    /// written before this existed arrives as <c>default(bool)</c>. false is
    /// therefore the value every upgrading install gets, and false has to mean
    /// the shipped behaviour.
    /// </summary>
    public bool BackspaceGoesUp { get; init; }
}

/// <summary>
/// Which commands appear in the context menu. Dolphin's Services page also
/// lists .desktop service menus and version-control plugins; neither applies
/// here — the scripts menu replaces the former by design, and VCS decorations
/// are not built.
/// </summary>
public sealed record ContextMenuSettings
{
    public bool ShowCopyTo { get; init; } = true;
    public bool ShowMoveTo { get; init; } = true;
    public bool ShowAddToPlaces { get; init; } = true;
    public bool ShowSortBy { get; init; } = true;
    public bool ShowOpenInNewTab { get; init; } = true;

    /// <summary>Its own flag, not a share of the one above: this page exists to
    /// answer one question per entry.</summary>
    public bool ShowOpenInNewWindow { get; init; } = true;
    public bool ShowCopyLocation { get; init; } = true;
    public bool ShowDuplicate { get; init; } = true;
}

/// <summary>
/// Trash limits. Vaktari implements the XDG trash spec, so these govern the
/// same directories Dolphin's do — which means enabling them here affects what
/// the rest of the desktop sees, and that is the point.
/// </summary>
public sealed record TrashSettings
{
    public bool DeleteOldFiles { get; init; }
    public int DeleteAfterDays { get; init; } = 30;

    public bool LimitSize { get; init; }
    public int MaximumPercentOfDisk { get; init; } = 10;

    public TrashLimitAction WhenLimitReached { get; init; } = TrashLimitAction.Warn;
}

/// <summary>
/// Preferences — what the user always wants — as distinct from
/// <c>SessionState</c>, which is where they happened to be last time.
///
/// The two are deliberately separate files. They have different lifetimes and
/// they conflict head-on: "restore my last folders" and "always start in Home"
/// are both startup settings, and one of them has to win. Settings are read
/// first, because <see cref="StartupSettings.ShowOnStartup"/> decides whether
/// the session is consulted at all.
/// </summary>
public sealed record SettingsState
{
    /// <summary>
    /// v1 — initial. An unrecognised version falls back to defaults rather
    /// than migrating or throwing: a settings file must never prevent startup,
    /// for the same reason a session file must not.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public GeneralSettings General { get; init; } = new();
    public StartupSettings Startup { get; init; } = new();
    public ViewSettings Views { get; init; } = new();
    public VcsSettings Vcs { get; init; } = new();
    public NavigationSettings Navigation { get; init; } = new();
    public ContextMenuSettings ContextMenu { get; init; } = new();
    public TrashSettings Trash { get; init; } = new();
}

/// <summary>
/// Puts back what deserialization leaves out.
///
/// **MEASURED 5 September 2026, through the real source-generated context:
/// `Deserialize("{\"version\":1}", SettingsJsonContext.Default.SettingsState)`
/// returns a state whose every group is null.** The context does not run
/// property initializers, so a key absent from settings.json arrives as
/// `default(T)` rather than as the declared default — and `{"version":1}` is a
/// valid v1 document that simply names no section, so the version gate in
/// <c>JsonSettingsStore.Load</c> passes it and hands the nulls straight on.
///
/// For a SCALAR that is survivable, and the note on
/// <see cref="ViewSettings.ShowSelectionBoxes"/> says how: `false` and `0` have
/// to BE the wanted behaviour, and each such property is named for its zero
/// value. For a reference-typed GROUP `default(T)` is null, and nothing
/// downstream survives it — `settings.Views.HideFileExtensions` throws,
/// <c>SettingsViewModel.Collect</c>'s `_original.General with { … }` throws,
/// and the settings dialog's Closed handler reading
/// `Result.General.ProtonDriveFolder` throws, that last one reachable by
/// pointing Replace-from-a-copy at a hand-edited file.
///
/// Here rather than at each read site, because "each read site" is every future
/// caller of a property somebody adds next year. Called from the two doors a
/// state comes through: <c>JsonSettingsStore.TryLoad</c>, the one place a file
/// becomes a record, and <c>AppSettings.Apply</c>, the one place a record
/// becomes the live preferences. Load promises in its own summary that there is
/// always a valid set of preferences, and before this it did not keep that
/// promise on its own — it relied on a different type in a different assembly
/// remembering to repair what it returned.
///
/// **`ReferenceEquals(x, null)` rather than `x is null` or `x ?? new()`:** these
/// properties are non-nullable reference types, so the nullable analyser may
/// call the comparison redundant — and this project builds with warnings as
/// errors. A method call cannot be warned about.
/// </summary>
public static class SettingsRepair
{
    public static SettingsState Complete(SettingsState settings) => settings with
    {
        General = Complete(settings.General),
        Startup = ReferenceEquals(settings.Startup, null) ? new() : settings.Startup,
        Views = Complete(settings.Views),
        Vcs = ReferenceEquals(settings.Vcs, null) ? new() : settings.Vcs,
        Navigation = ReferenceEquals(settings.Navigation, null) ? new() : settings.Navigation,
        ContextMenu = ReferenceEquals(settings.ContextMenu, null) ? new() : settings.ContextMenu,
        Trash = ReferenceEquals(settings.Trash, null) ? new() : settings.Trash,
    };

    /// <summary>
    /// The same hazard one level down, on STRINGS rather than groups.
    ///
    /// **A missing key arrives as null, not as the declared default**, and 0.8.0
    /// shipped a NullReferenceException out of the MainWindow constructor
    /// because of exactly that: the application would not start.
    ///
    /// **`ProtonDriveFolder` was still missing from this list on 5 September
    /// 2026.** A `general` object written before that property existed reached
    /// `Collect()`'s `ProtonDriveFolder.Trim()`, so opening Settings on an
    /// upgrading install and pressing Save threw — the same shape of failure
    /// the two lines above were added to stop, one property later.
    ///
    /// Every non-nullable string in the model is listed.
    /// <see cref="StartupSettings.StartupFolder"/> and
    /// <see cref="ViewSettings.CustomFontFamily"/> are not, because they are
    /// declared `string?` and null is what they mean.
    /// </summary>
    private static GeneralSettings Complete(GeneralSettings general)
    {
        if (ReferenceEquals(general, null)) return new GeneralSettings();

        return general with
        {
            IconThemeFolder = general.IconThemeFolder ?? "",
            PreferredTerminal = general.PreferredTerminal ?? "",
            ProtonDriveFolder = general.ProtonDriveFolder ?? "",
        };
    }

    /// <summary>
    /// <see cref="ViewSettings"/> nests three groups of its own, exposed to
    /// exactly the same thing. Completing the outer one and stopping there would
    /// look handled while `Views.Icons.Spacing` still threw.
    ///
    /// **The three lines below are only reachable when the file names `views`
    /// and not the layouts inside it**, which cost a revert-check: with `views`
    /// absent the guard above hands back a freshly constructed record, and a
    /// constructor DOES run the initializers, so the layouts are already there
    /// and deleting any of the three lines changed nothing. A test that reaches
    /// them has to load `{"version":1,"views":{}}` rather than `{"version":1}`.
    /// </summary>
    private static ViewSettings Complete(ViewSettings views)
    {
        if (ReferenceEquals(views, null)) return new ViewSettings();

        return views with
        {
            Icons = ReferenceEquals(views.Icons, null) ? new() : views.Icons,
            Compact = ReferenceEquals(views.Compact, null) ? new() : views.Compact,
            Details = ReferenceEquals(views.Details, null) ? new() : views.Details,
        };
    }
}

/// <summary>
/// Persistence contract. Unlike the session there is no debounce: settings
/// change only when a person changes one, so a write per change is both rare
/// and what they expect. Atomicity and the fall-back-to-defaults rule are the
/// same.
/// </summary>
public interface ISettingsStore
{
    SettingsState Load();

    /// <summary>
    /// Synchronous on purpose. The session store is async because it writes on
    /// a timer while the user is working; this writes a few kilobytes when
    /// someone clicks OK in a dialog. Async here would add a fire-and-forget
    /// call site for no gain.
    /// </summary>
    void Save(SettingsState settings);
}

/// <summary>Source-generated — reflection-based JSON does not survive trimming.</summary>
[JsonSerializable(typeof(SettingsState))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
public partial class SettingsJsonContext : JsonSerializerContext;
