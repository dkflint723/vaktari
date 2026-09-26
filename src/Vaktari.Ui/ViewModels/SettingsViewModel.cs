using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.Settings;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// One entry in the font dropdown.
///
/// A typed record rather than a bare string **specifically so the dropdown can
/// have an item template**. A `DataTemplate` over `System.String` needs
/// `x:DataType` pointing at a framework type and is awkward under compiled
/// bindings; this is the ordinary pattern used everywhere else in the
/// application, and it also gives the sample somewhere to live.
///
/// <paramref name="Family"/> is built once here rather than converted in
/// markup — binding a string to a `FontFamily` property relies on a conversion
/// this codebase does not otherwise depend on.
/// </summary>
public sealed record FontOption(string Name, FontFamily Family, bool IsFollowDesktop);

/// <summary>
/// One row in the terminal chooser. <c>Id</c> empty is the "whichever is
/// found first" row, which is what the application always did and stays right
/// for the many machines with exactly one terminal.
/// </summary>
public sealed record TerminalChoice(string Id, string Name);

/// <summary>
/// The settings dialog's pages, in the order the strip lists them.
///
/// **The window always opened on the first page, and nothing outside it could
/// name another.** The tour's line about changing keys could only say "Settings
/// — Keyboard" and leave the reader to find it, and somebody working through
/// the Keyboard page had to click back to it after every visit. The order here
/// IS the strip's order — <see cref="SettingsViewModel.PageIndex"/> is this
/// value as a number — and SettingsPagesTests reads the markup to hold the two
/// together.
/// </summary>
public enum SettingsPage
{
    General,
    Appearance,
    FoldersAndLists,
    PrivacyAndSystem,
    Keyboard,
    ContextMenu,
    Bin,
}

/// <summary>
/// Edits a copy and commits it whole, rather than writing each control as it
/// changes. Cancel then genuinely cancels what has not been applied, and a
/// half-finished set of preferences never reaches disk.
///
/// Seven pages, each holding what its name says: General (how Vaktari opens,
/// what a click and a key do, splits, confirmations), Appearance, Folders and
/// lists, Privacy and system, Keyboard, Context menu and the bin. **The pages
/// are a layout, not a data shape** — every control still reads and writes the
/// stored key it always did, so regrouping them needed no migration.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    /// <summary>
    /// The state this dialog opened with, which Collect uses `with` over so
    /// pages that were never built keep their file values.
    ///
    /// Not readonly, because "restore defaults" has to replace this as well:
    /// resetting only what is on screen would leave every setting on a page
    /// nobody opened exactly as it was, which is not what the button says.
    /// </summary>
    private SettingsState _original = new();

    /// <summary>The Keyboard page: every command and its keys. Seeded with the
    /// rest of the dialog and collected with it, so Cancel throws a key away
    /// like any other change and Restore defaults puts the keys back too.</summary>
    public KeyboardPage Keyboard { get; } = new();

    private readonly Core.IDefaultFileManager? _defaults;
    private readonly Core.FileSystem.IFileIconProvider? _desktopIcons;
    private readonly Core.IFileManagerService? _fileManager;

    public SettingsViewModel(
        SettingsState current,
        Core.IDefaultFileManager? defaults = null,
        Core.FileSystem.IFileIconProvider? desktopIcons = null,
        Core.IFileManagerService? fileManager = null,
        string? settingsFile = null,
        Core.FileSystem.IFolderViewStore? folderViews = null,
        Core.FileSystem.IRecentStore? recents = null,
        Core.Search.ISearchHistory? searches = null,
        string? settingsFileNote = null,
        string? updateAvailable = null)
    {
        // The footer line, seeded: a file this build must not write is said
        // where the Save button is, not only on the status bar at startup.
        _settingsFileStatus = settingsFileNote ?? "";

        // And the version line, which is where "What is new" is the link.
        _updateAvailable = updateAvailable;

        _rememberedViews = folderViews?.Remembered ?? 0;
        _recentCount = recents?.Count ?? 0;
        _searchCount = searches?.Count ?? 0;

        _settingsFile = settingsFile ?? "";
        _defaults = defaults;
        _desktopIcons = desktopIcons;
        _fileManager = fileManager;
        _isDefaultFileManager = defaults?.IsDefault() ?? false;

        // The font LIST is a fact about the machine rather than a setting, so
        // it is built once here and only re-picked from below. Restoring
        // defaults does not uninstall anybody's fonts.
        AvailableFonts = BuildFontList(current.Views.CustomFontFamily);

        Seed(current);
    }

    /// <summary>
    /// Every control on the seven pages, from a state.
    ///
    /// **Lifted out of the constructor so it can be run a second time.** There
    /// was no way to put the settings back: the constructor read each field
    /// straight off the record it was handed, so the only route to the
    /// defaults was to close Vaktari, delete settings.json and start again.
    ///
    /// Assigns FIELDS, not properties, exactly as the constructor did — the
    /// ordering below is load-bearing in two places, and property setters would
    /// break both. The caller raises one property-changed for everything after
    /// this returns, which is what a re-seed needs and what construction does
    /// not.
    /// </summary>
    private void Seed(SettingsState current)
    {
        _original = current;

        var startup = current.Startup;
        var general = current.General;

        NaturalSorting = general.NaturalSorting;
        CaseSensitiveSorting = general.CaseSensitiveSorting;
        // Positive question here, negative one in the record, so the record's
        // zero value is the behaviour every install already has.
        FoldersFirst = !general.MixFoldersWithFiles;
        RememberViewPerFolder = general.RememberViewPerFolder;
        ShowTooltips = general.ShowTooltips;
        RememberRecent = general.RememberRecent;
        CheckForUpdates = general.CheckForUpdates;

        // Inverted HERE and nowhere else. GeneralSettings.ForgetSearches is
        // named for its zero value because deserialization does not run
        // property initializers — measured on this very record — and the
        // checkbox still has to read the positive way round.
        RememberSearches = !general.ForgetSearches;
        TabSwitchesSplitPanes = general.TabSwitchesSplitPanes;
        ClosingSplitDiscardsOtherPane = general.ClosingSplitDiscardsOtherPane;
        ShowStatusBar = general.ShowStatusBar;
        ShowFreeSpace = general.ShowFreeSpace;
        ShowPreviews = general.ShowPreviews;
        MaxLocalPreviewMegabytes = Limit(general.MaxLocalPreviewMegabytes);
        MaxRemotePreviewMegabytes = Limit(general.MaxRemotePreviewMegabytes);
        ConfirmMoveToTrash = general.ConfirmMoveToTrash;
        ConfirmPermanentDelete = general.ConfirmPermanentDelete;
        ConfirmClosingMultipleTabs = general.ConfirmClosingMultipleTabs;

        var views = current.Views;

        // Matched by NAME, not by reference: the configured value comes from a
        // file, and the list is built fresh. A configured font that is not
        // installed was already inserted by BuildFontList, so this cannot miss
        // and silently fall back to the sentinel — which would rewrite the
        // user's font the moment they pressed Save.
        SelectedFont = AvailableFonts.FirstOrDefault(o =>
            string.Equals(o.Name, views.CustomFontFamily, StringComparison.OrdinalIgnoreCase))
            ?? AvailableFonts[0];
        UseSystemIcons = current.General.UseSystemIcons;

        // **Behind the sync guard, which is what it is for.** Setting
        // IconThemeFolder rebuilds the catalogue around it, and this is about
        // to rebuild it once at the end anyway — without the guard a re-seed
        // enumerates the theme folder twice for one press of a button.
        _syncingIconThemes = true;

        try
        {
            IconThemeFolder = current.General.IconThemeFolder;
        }
        finally
        {
            _syncingIconThemes = false;
        }

        ProtonDriveFolder = current.General.ProtonDriveFolder;

        // **Checked on the way in, not only when it was chosen.** A theme
        // folder that has since been moved, renamed or deleted would otherwise
        // show as the chosen theme, with a Clear button and no complaint, while
        // the listing quietly used the drawn set — which is the same invisible
        // failure the browse-time check exists to prevent, reached by the other
        // route.
        //
        // **In two halves, because the whole question is expensive.** Reading a
        // theme enumerates it and everything it inherits — 2.8–3.1 seconds for
        // Papirus-Dark on the machine that reported it — and asking it here
        // meant the dialog did not appear until it was answered. What "moved or
        // deleted" actually needs is two existence checks, and those are free;
        // whether the folder still READS as a theme is asked behind the dialog
        // and reported if the answer turns out to be no.
        IconThemeProblem = "";
        ThemeVerification = Task.CompletedTask;

        if (IconThemeFolder.Length > 0)
        {
            if (!Directory.Exists(IconThemeFolder)
                || !File.Exists(Path.Combine(IconThemeFolder, "index.theme")))
            {
                IconThemeProblem = ThemeGone;
            }
            else
            {
                ThemeVerification = Verify(IconThemeFolder);
            }
        }

        // Built from the disk once the chosen folder is known, so the list
        // opens already showing what is in use. The field was assigned above
        // rather than the property, so nothing has rebuilt it yet.
        RefreshIconThemes();
        FollowDesktopColours = views.FollowDesktopColours;
        ShowFolderTree = views.ShowFolderTree;
        ThemeModeIndex = views.ThemeMode switch
        {
            Core.Settings.ThemeMode.Light => 1,
            Core.Settings.ThemeMode.Dark => 2,
            _ => 0,
        };
        InterfaceTextIndex = InterfaceText.RowFor(views.InterfaceTextScale);
        AbsoluteDates = views.Details.DateStyle == Core.Settings.DateStyle.Absolute;
        FolderSizeCounts = views.Details.FolderSize == Core.Settings.FolderSizeMode.ItemCount;
        FolderSizeContents = views.Details.FolderSize == Core.Settings.FolderSizeMode.ContentSize;
        FolderSizeNothing = views.Details.FolderSize == Core.Settings.FolderSizeMode.None;

        // Blank rather than "0": the placeholder says what zero means, and an
        // empty box invites a value where a literal 0 looks like a setting
        // someone already made.
        // `?? true` because the group is demonstrably null for a settings file
        // written before it existed, and the DEFAULT is on. Guarding here as
        // well as in the pane: the same dereference crashed the listing, I fixed
        // that one site, and left this one to crash the dialog instead.
        ShowVcsDecorations = current.Vcs?.ShowDecorations ?? true;
        ShowSelectionBoxes = views.ShowSelectionBoxes;
        // Inverted for the reason given beside FoldersFirst above.
        ShowFileExtensions = !views.HideFileExtensions;
        GrowWindowForPanel = views.NarrowDetailsPanel == NarrowPanelBehaviour.GrowWindow;
        // The dialog asks the positive question; the record stores the negative
        // one so its zero value is the wanted behaviour.
        RestoreWidthOnPanelClose = !views.KeepWidthAfterPanelClose;

        IconSpacing = views.Icons.Spacing > 0 ? views.Icons.Spacing.ToString() : "";
        CompactSpacing = views.Compact.Spacing > 0 ? views.Compact.Spacing.ToString() : "";

        var trash = current.Trash;

        DeleteOldTrash = trash.DeleteOldFiles;
        DeleteAfterDays = trash.DeleteAfterDays.ToString();
        LimitTrashSize = trash.LimitSize;
        MaxPercentOfDisk = trash.MaximumPercentOfDisk.ToString();
        LimitActionWarn = trash.WhenLimitReached == TrashLimitAction.Warn;
        LimitActionOldest = trash.WhenLimitReached == TrashLimitAction.DeleteOldest;
        LimitActionLargest = trash.WhenLimitReached == TrashLimitAction.DeleteLargest;

        OpenWithSystem = current.Navigation.OpenItemsWith == ActivationClick.System;
        OpenWithSingle = current.Navigation.OpenItemsWith == ActivationClick.Single;
        OpenWithDouble = current.Navigation.OpenItemsWith == ActivationClick.Double;

        BackspaceGoesUp = current.Navigation.BackspaceGoesUp;

        var menu = current.ContextMenu;

        MenuCopyTo = menu.ShowCopyTo;
        MenuMoveTo = menu.ShowMoveTo;
        MenuSortBy = menu.ShowSortBy;
        MenuDuplicate = menu.ShowDuplicate;
        MenuOpenInNewTab = menu.ShowOpenInNewTab;
        MenuOpenInNewWindow = menu.ShowOpenInNewWindow;
        MenuAddToPlaces = menu.ShowAddToPlaces;
        MenuCopyLocation = menu.ShowCopyLocation;

        RestoreLastSession = startup.ShowOnStartup == StartupLocation.RestoreSession;
        StartInHome = startup.ShowOnStartup == StartupLocation.HomeFolder;
        StartInComputer = startup.ShowOnStartup == StartupLocation.Computer;
        StartInSpecificFolder = startup.ShowOnStartup == StartupLocation.SpecificFolder;
        StartupFolder = startup.StartupFolder ?? "";
        BeginInSplitView = startup.BeginInSplitView;
        ShowFilterBar = startup.ShowFilterBar;
        LocationBarEditable = startup.LocationBarEditable;
        ShowFullPathInTitleBar = startup.ShowFullPathInTitleBar;

        // **Restore defaults left the terminal on whatever was chosen before.**
        // Only UseTerminals picked a row, and it runs once, when the dialog
        // opens — so a restore reset every control but this one, and Save
        // wrote the old terminal straight back. Picked from the list as it
        // stands: at construction that is the one "whichever is found first"
        // row, and UseTerminals picks again once the real list arrives.
        SelectPreferredTerminal();

        Keyboard.Load(current.Keyboard);
    }

    // ---- which page is showing ---------------------------------------------

    /// <summary>
    /// The page on screen, as the strip's index — bound to the TabControl's
    /// SelectedIndex, so setting it before the window is shown opens the
    /// dialog there, and reading it at close says where the person left off.
    /// See <see cref="SettingsPage"/> for why that needed saying.
    /// </summary>
    [ObservableProperty] private int _pageIndex;

    /// <summary>The same, by name.</summary>
    public SettingsPage Page
    {
        get => Enum.IsDefined((SettingsPage)PageIndex) ? (SettingsPage)PageIndex : SettingsPage.General;
        set => PageIndex = (int)value;
    }

    /// <summary>
    /// Whether the Keyboard page is the one showing, decided here and only
    /// here.
    ///
    /// **Asked to open on Keyboard, the dialog opened on General.** The
    /// Keyboard tab's IsSelected was bound both ways to Keyboard.IsOpen, and
    /// that binding, still reading false, unselected the page the index had
    /// just selected — whereupon the strip fell back to its first page and
    /// wrote 0 back here. Telling it from here instead only moved the fault:
    /// turning off the Keyboard page cleared IsOpen, the tab binding
    /// unselected mid-turn, and Ctrl+Tab from Keyboard landed on General. Two
    /// bindings answering one question will disagree at some moment, so
    /// there is one: the strip's index, which this turns into IsOpen — and
    /// IsOpen going false is what stops a row listening when the page is
    /// left.
    /// </summary>
    partial void OnPageIndexChanged(int value)
    {
        Keyboard.IsOpen = value == (int)SettingsPage.Keyboard;
        OnPropertyChanged(nameof(Page));
    }

    // ---- when Vaktari opens -------------------------------------------------

    // Four booleans, which are what the record's one enum is collected from
    // (see Collect) and what the tests and the old radios drove. The page now
    // shows them as ONE dropdown, through StartupIndex below.

    [ObservableProperty] private bool _restoreLastSession;
    [ObservableProperty] private bool _startInHome;
    [ObservableProperty] private bool _startInComputer;
    [ObservableProperty] private bool _startInSpecificFolder;

    /// <summary>
    /// The startup choice as one dropdown's row: restore, home, the drive
    /// listing, a specific folder — in that order.
    ///
    /// **Four radio buttons and an indented folder box took a third of a page
    /// to ask one question**, and the box sat there greyed out for the three
    /// answers that ignore it. One dropdown asks it in a line, and the folder
    /// box appears only for the answer that reads it.
    ///
    /// Derived from the four booleans rather than replacing them: they are what
    /// <see cref="Collect"/> reads, and setting a row sets exactly one of them,
    /// which is the guarantee the radios' shared GroupName used to give.
    /// </summary>
    public int StartupIndex
    {
        get => StartInSpecificFolder ? 3 : StartInComputer ? 2 : StartInHome ? 1 : 0;
        set
        {
            if (value == StartupIndex) return;

            RestoreLastSession = value is not (1 or 2 or 3);
            StartInHome = value == 1;
            StartInComputer = value == 2;
            StartInSpecificFolder = value == 3;
        }
    }

    partial void OnStartInHomeChanged(bool value) => OnPropertyChanged(nameof(StartupIndex));

    partial void OnStartInComputerChanged(bool value) => OnPropertyChanged(nameof(StartupIndex));

    [ObservableProperty] private string _startupFolder = "";
    [ObservableProperty] private bool _beginInSplitView;
    [ObservableProperty] private bool _showFilterBar;
    [ObservableProperty] private bool _locationBarEditable;
    [ObservableProperty] private bool _showFullPathInTitleBar;

    /// <summary>
    /// The drive-listing radio's label, in the platform's own word for it —
    /// "Open This PC" on Windows, "Open this computer" on Linux.
    ///
    /// **Here rather than in markup because the label is an interpolation, and
    /// x:Static hands a value over whole.** Measured, in this window: a
    /// TextBlock reading <c>Text="Open {x:Static core:Naming.ComputerName}"</c>
    /// rendered the literal characters "Open {x:Static core:Naming.ComputerName}"
    /// — inside a longer string the extension is not evaluated at all.
    ///
    /// NOT for the timing reason written on <see cref="BinName"/>, which was
    /// measured here and does not hold: with the process words forced to linux
    /// first, <c>Text="{x:Static core:Naming.ComputerName}"</c> in this window
    /// still read "This PC". A SettingsWindow is only ever constructed from a
    /// MainWindow, whose own constructor has already called Naming.Adopt by the
    /// time it can open one — so unlike MainWindow.axaml, this window's XAML is
    /// never parsed before the platform is chosen.
    ///
    /// <see cref="Core.Naming.ComputerName"/> and not ComputerTitle: the noun
    /// sits mid-sentence here, which is the difference between "Open this
    /// computer" and a stray capital.
    /// </summary>
    public string StartInComputerLabel => $"Open {Core.Naming.ComputerName}";

    // ---- General ----------------------------------------------------------

    [ObservableProperty] private bool _naturalSorting;
    [ObservableProperty] private bool _caseSensitiveSorting;

    /// <summary>
    /// The dialog asks the positive question — "Sort folders before files" —
    /// while <c>GeneralSettings.MixFoldersWithFiles</c> stores the negative one
    /// so that its zero value is the shipped behaviour. Same inversion, and the
    /// same reason, as <see cref="RestoreWidthOnPanelClose"/> below.
    /// </summary>
    [ObservableProperty] private bool _foldersFirst;
    [ObservableProperty] private bool _rememberViewPerFolder;
    [ObservableProperty] private bool _showTooltips;
    [ObservableProperty] private bool _rememberRecent;

    /// <summary>Off by default; see GeneralSettings.CheckForUpdates.</summary>
    [ObservableProperty] private bool _checkForUpdates;

    /// <summary>The positive half of <c>GeneralSettings.ForgetSearches</c>,
    /// which is named for its zero value so an upgrading settings.json keeps
    /// its history.</summary>
    [ObservableProperty] private bool _rememberSearches;
    [ObservableProperty] private bool _tabSwitchesSplitPanes;
    [ObservableProperty] private bool _closingSplitDiscardsOtherPane;
    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _showFreeSpace;

    /// <summary>
    /// The sort order as one dropdown's row: natural, alphabetical, or
    /// alphabetical with capitals apart.
    ///
    /// **"Case sensitive" sat greyed out under "Natural sorting" with nothing
    /// saying why.** Natural order compares case-insensitively by construction,
    /// so the case choice only means anything with it off — a relationship the
    /// page showed as a box that would not tick. Three rows say the same thing
    /// as three answers to one question, and there is nothing to grey.
    ///
    /// On the two stored keys, unchanged. The natural row leaves
    /// CaseSensitiveSorting as it was rather than clearing it: natural order
    /// never reads it, and a file that says true keeps saying true until
    /// somebody picks a row that does read it.
    /// </summary>
    public int SortOrderIndex
    {
        get => NaturalSorting ? 0 : CaseSensitiveSorting ? 2 : 1;
        set
        {
            if (value == SortOrderIndex) return;

            NaturalSorting = value is not (1 or 2);
            if (value is 1 or 2) CaseSensitiveSorting = value == 2;
        }
    }

    partial void OnNaturalSortingChanged(bool value) => OnPropertyChanged(nameof(SortOrderIndex));

    partial void OnCaseSensitiveSortingChanged(bool value) => OnPropertyChanged(nameof(SortOrderIndex));

    // CanSetFreeSpace was here, gating the free-space checkbox on the status
    // bar because that is where the number used to print. It prints on the
    // sidebar's drive rows now, so hiding the status bar would have greyed out
    // a setting governing something still on screen.

    [ObservableProperty] private bool _showPreviews;
    [ObservableProperty] private bool _confirmMoveToTrash;
    [ObservableProperty] private bool _confirmPermanentDelete;
    [ObservableProperty] private bool _confirmClosingMultipleTabs;

    // Text rather than int: a spinner for "0 means unlimited" reads as a
    // quantity when it is really a switch with a quantity attached, and an
    // empty box is a clearer "no limit" than a zero.
    [ObservableProperty] private string _maxLocalPreviewMegabytes = "";
    [ObservableProperty] private string _maxRemotePreviewMegabytes = "";

    public bool CanSetPreviewLimits => ShowPreviews;

    partial void OnShowPreviewsChanged(bool value)
        => OnPropertyChanged(nameof(CanSetPreviewLimits));

    /// <summary>Anything unparseable, negative or blank means no limit.</summary>
    private static int Megabytes(string text)
        => int.TryParse(text, out var value) && value > 0 ? value : 0;

    /// <summary>
    /// **A box showing "0" beside the words "no limit".** Zero is how no limit
    /// is stored, and it was written straight into the field -- which is a
    /// literal zero on screen, reading as "skip files larger than nothing", and
    /// it also hid the placeholder that says what is actually going on. The
    /// help text beneath already promises "blank or 0 means no limit"; the box
    /// now shows the blank half of that.
    /// </summary>
    private static string Limit(int megabytes)
        => megabytes > 0 ? megabytes.ToString() : "";

    // ---- Context menu -----------------------------------------------------
    //
    // Eight of Dolphin's nine. "Open in new window" was the missing one, and it
    // was missing for exactly one reason: App.axaml.cs created a single
    // MainWindow. It now opens the founder and Ctrl+N opens peers beside it, so
    // the entry exists and gets a toggle of its own like every other row —
    // sharing "Open in new tab"'s switch would make one checkbox answer two
    // questions on a page that exists to answer them one at a time. "View mode"
    // lives in the toolbar and the view flyout rather than the context menu, so
    // there is still nothing for a toggle to hide.

    [ObservableProperty] private bool _menuCopyTo;
    [ObservableProperty] private bool _menuMoveTo;
    [ObservableProperty] private bool _menuSortBy;
    [ObservableProperty] private bool _menuDuplicate;
    [ObservableProperty] private bool _menuOpenInNewTab;
    [ObservableProperty] private bool _menuOpenInNewWindow;
    [ObservableProperty] private bool _menuAddToPlaces;
    [ObservableProperty] private bool _menuCopyLocation;

    // ---- how listings are drawn (Appearance, Folders and lists) ----------
    //
    // Three of the six. Icons.TextWidth, Icons.MaximumLines and
    // Compact.MaximumTextWidth stay out: they are structural metrics that would
    // have to feed PaneScale.Compute, and that pipeline is double-typed while
    // MaxLines is an int. Details.FolderSize's "size of contents" option is
    // reachable now — it needed recursive summing that did not exist, and
    // SpaceUsage.Measure is it.

    /// <summary>
    /// The first entry, and the default. A sentinel string rather than a null
    /// item because a ComboBox showing an empty row reads as a bug.
    /// </summary>
    private const string FollowDesktop = "Follow the desktop font";

    public IReadOnlyList<FontOption> AvailableFonts { get; }

    /// <summary>
    /// The terminals this machine has, for the chooser — with "whichever is
    /// found first" at the top, since that is the behaviour anyone who has
    /// never opened this dialog already has.
    ///
    /// Fed in by the window rather than probed here: this view model is
    /// constructed on the UI thread when the dialog opens, and looking for a
    /// dozen executables at that moment is a dialog that takes a beat to appear.
    /// </summary>
    public IReadOnlyList<TerminalChoice> AvailableTerminals { get; private set; } =
        [new TerminalChoice("", "Whichever is found first")];

    [ObservableProperty] private TerminalChoice? _selectedTerminal;

    /// <summary>
    /// Whether files show the desktop's own icons instead of the bundled set.
    /// Off by default: the drawn set is the one this application looks right in,
    /// and somebody who prefers their desktop's is making a deliberate choice.
    /// </summary>
    [ObservableProperty] private bool _useSystemIcons;

    /// <summary>The chosen theme folder, or empty. Shown as its own name rather
    /// than the whole path, which is long and mostly uninteresting.</summary>
    [ObservableProperty] private string _iconThemeFolder = "";

    public string IconThemeLabel => IconThemeFolder.Length == 0
        ? "None chosen"
        : Path.GetFileName(IconThemeFolder.TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    public bool HasIconTheme => IconThemeFolder.Length > 0;

    partial void OnUseSystemIconsChanged(bool value)
    {
        // Set from somewhere other than the chooser — a re-seed, or a test —
        // so the chooser follows it. Choosing a row sets it under the guard.
        if (!_syncingIconThemes) SelectCurrentIconRow();
    }

    /// <summary>
    /// Where the Proton Drive sync folder is, for the link-sharing gestures. A
    /// typed path rather than a picker for v1: it is set once, and the person
    /// who moved their sync folder to D: knows exactly where it went.
    /// </summary>
    [ObservableProperty] private string _protonDriveFolder = "";

    /// <summary>
    /// Why a chosen folder was refused, shown under the row.
    ///
    /// **Said here rather than in a dialog.** The answer belongs beside the
    /// control that asked the question, and a second modal on top of a modal to
    /// report "that folder was not what I needed" is a lot of ceremony for a
    /// sentence.
    /// </summary>
    [ObservableProperty] private string _iconThemeProblem = "";

    private const string ThemeGone =
        "That folder is no longer an icon theme — it may have been moved or deleted. "
        + "Pick another from the list, or Vaktari's own icons.";

    private const string ThemeUnreadable =
        "That folder no longer reads as an icon theme. If it keeps its icons as links to "
        + "another theme, that other theme may have been removed. Pick another from the "
        + "list, or Vaktari's own icons.";

    /// <summary>
    /// Whether a folder reads as a theme. A seam, so a test can ask the
    /// question without a quarter of a gigabyte of icons on disk — and so the
    /// slow half can be held open long enough to prove the dialog did not wait
    /// for it.
    /// </summary>
    internal static Func<string, bool> ReadsAsTheme { get; set; } =
        folder => Core.FileSystem.FreedesktopIconTheme.FromFolder(folder) is not null;

    /// <summary>
    /// The check running behind the dialog, for tests to await. Completed when
    /// there was nothing to check.
    /// </summary>
    internal Task ThemeVerification { get; private set; } = Task.CompletedTask;

    private Task Verify(string folder)
        => Task.Run(() => ReadsAsTheme(folder))
            .ContinueWith(
                answered =>
                {
                    if (answered is { Status: TaskStatus.RanToCompletion, Result: false })
                        Avalonia.Threading.Dispatcher.UIThread.Post(
                            () => IconThemeProblem = ThemeUnreadable);
                },
                TaskScheduler.Default);

    public bool HasIconThemeProblem => IconThemeProblem.Length > 0;

    partial void OnIconThemeProblemChanged(string value)
        => OnPropertyChanged(nameof(HasIconThemeProblem));

    partial void OnIconThemeFolderChanged(string value)
    {
        OnPropertyChanged(nameof(IconThemeLabel));
        OnPropertyChanged(nameof(HasIconTheme));

        // Set from somewhere other than the list — browsing, or a theme that
        // has just been installed — so the list is rebuilt around it.
        if (!_syncingIconThemes) RefreshIconThemes();
    }

    // ---- the list of themes to pick from -----------------------------------

    /// <summary>One row in the list: what it is called, and what to hand the
    /// reader. <paramref name="DesktopIcons"/> marks the one row that asks for
    /// the desktop's per-file icons rather than a theme.</summary>
    public sealed record IconThemeChoice(string Label, string Folder, bool DesktopIcons = false);

    public ObservableCollection<IconThemeChoice> IconThemeChoices { get; } = [];

    [ObservableProperty] private IconThemeChoice? _selectedIconTheme;

    /// <summary>
    /// Guards the two directions against each other: choosing from the list
    /// sets the folder, and setting the folder rebuilds the list and selects in
    /// it. Without this the two would take turns forever.
    /// </summary>
    private bool _syncingIconThemes;

    /// <summary>
    /// What the row with no theme draws, which is not the same on both
    /// platforms.
    ///
    /// **It said "Vaktari's own icons" on Linux, where it is not.** With no
    /// theme chosen the listing draws with the platform's icon provider, and
    /// on Linux that IS the desktop's icon theme — the one Plasma names — with
    /// the drawn set only as the fallback; on Windows there is no such
    /// provider and the drawn set is what shows.
    /// </summary>
    private static string NoThemeLabel => Core.Naming.Platform == "windows"
        ? "Vaktari's own icons"
        : "Your desktop's icon theme";

    /// <summary>
    /// Rebuilds the list from what is actually on disk.
    ///
    /// **Read rather than remembered.** One download produces several themes
    /// and cannot say in advance how many; a folder deleted by hand would leave
    /// a remembered list offering something that is not there. Anything chosen
    /// by browsing is added on the end, so a theme kept somewhere else is still
    /// a row in the list rather than a reason for the list to disagree with the
    /// setting.
    ///
    /// **And the desktop's own icons are a row of it, not a box beside it.**
    /// "Use my desktop's icons" was a check box above this list, and a theme
    /// chosen here silently outranked it — so the box was greyed out with a
    /// paragraph under it explaining which of the two was drawing. Three
    /// sources, one of which wins, is one question: this list asks it. The row
    /// is offered only where the platform has per-file icons
    /// (<see cref="CanUseDesktopIcons"/>), exactly where the box was.
    /// </summary>
    public void RefreshIconThemes()
    {
        _syncingIconThemes = true;

        try
        {
            IconThemeChoices.Clear();
            IconThemeChoices.Add(new IconThemeChoice(NoThemeLabel, ""));

            if (CanUseDesktopIcons)
                IconThemeChoices.Add(new IconThemeChoice("Your desktop's icons", "", DesktopIcons: true));

            foreach (var installed in Core.FileSystem.IconThemeCatalogue.Installed())
                IconThemeChoices.Add(new IconThemeChoice(installed.Name, installed.Folder));

            if (IconThemeFolder.Length > 0 && !IconThemeChoices.Any(c => c.Folder.Length > 0 && Chosen(c)))
                IconThemeChoices.Add(new IconThemeChoice(IconThemeLabel + "  (chosen folder)", IconThemeFolder));
        }
        finally
        {
            _syncingIconThemes = false;
        }

        SelectCurrentIconRow();

        bool Chosen(IconThemeChoice choice) => Core.FileSystem.PathRules.Same(choice.Folder, IconThemeFolder);
    }

    /// <summary>
    /// The row the two stored keys describe, by the precedence
    /// IconLoader.UseSystemIcons applies: a chosen theme wins; then the
    /// desktop's icons, where they exist and are asked for; then the row with
    /// no theme. So what the chooser shows is what the listing draws.
    /// </summary>
    private void SelectCurrentIconRow()
    {
        var row = IconThemeFolder.Length > 0
            ? IconThemeChoices.FirstOrDefault(c => c.Folder.Length > 0
                                                   && Core.FileSystem.PathRules.Same(c.Folder, IconThemeFolder))
            // With no desktop row — no per-file icons here — asking for it
            // finds nothing and falls to the first row below, which is the
            // rule as IconLoader.UseSystemIcons applies it.
            : IconThemeChoices.FirstOrDefault(c => c.DesktopIcons == UseSystemIcons);

        _syncingIconThemes = true;

        try { SelectedIconTheme = row ?? IconThemeChoices.FirstOrDefault(); }
        finally { _syncingIconThemes = false; }
    }

    /// <summary>
    /// A row chosen, onto the two stored keys.
    ///
    /// The row with no theme and the desktop's row both clear the folder and
    /// say which of the two they are — but only where the desktop's row exists:
    /// on Linux UseSystemIcons can do nothing, so it is left as the file had
    /// it. A theme row leaves it alone too, because the theme outranks it
    /// either way and a choice nobody touched is not rewritten.
    /// </summary>
    partial void OnSelectedIconThemeChanged(IconThemeChoice? value)
    {
        if (_syncingIconThemes || value is null) return;

        _syncingIconThemes = true;

        try
        {
            IconThemeFolder = value.Folder;
            if (value.Folder.Length == 0 && CanUseDesktopIcons) UseSystemIcons = value.DesktopIcons;
            IconThemeProblem = "";
            IconThemeStatus = "";
        }
        finally
        {
            _syncingIconThemes = false;
        }
    }

    /// <summary>Raised so the window can open a folder picker; a view model has
    /// no business owning a dialog.</summary>
    public event EventHandler? IconThemeBrowseRequested;

    /// <summary>And a file picker, for an archive somebody downloaded.</summary>
    public event EventHandler? IconThemeArchiveRequested;

    /// <summary>And a browser, or a folder in whatever shows folders.</summary>
    public event EventHandler<string>? OpenUrlRequested;

    [RelayCommand]
    private void BrowseForIconTheme() => IconThemeBrowseRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(CanFetchIconTheme))]
    private void PickIconThemeArchive() => IconThemeArchiveRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Opens the folder the fetched themes are kept in.
    ///
    /// Created first: a folder that has never been written to does not exist,
    /// and "open" doing nothing at all reads as a broken button rather than as
    /// an empty shelf.
    /// </summary>
    [RelayCommand]
    private void OpenIconFolder()
    {
        var root = Core.FileSystem.IconThemeCatalogue.InstallRoot;

        try { Directory.CreateDirectory(root); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }

        OpenUrlRequested?.Invoke(this, root);
    }

    /// <summary>
    /// Where compatible themes live.
    ///
    /// **The KDE Store, because the format is the thing that matters.** Vaktari
    /// reads freedesktop icon themes, so what works is anything published as
    /// one — which is that catalogue, plus the projects that host their own.
    /// A generic search would send people to Windows .ico packs, none of which
    /// this can read.
    /// </summary>
    public const string IconThemesUrl = "https://store.kde.org/browse?category=132&order=latest";

    [RelayCommand]
    private void GetMoreIcons() => OpenUrlRequested?.Invoke(this, IconThemesUrl);

    // ---- the file all of this ends up in -----------------------------------

    /// <summary>
    /// settings.json, or empty in a view model built without one.
    ///
    /// **No page in this dialog could say where the choices go.** The footer
    /// shows a path on hover and it is the path of the BINARY; the file itself
    /// could only be found by knowing where a platform keeps config, or by
    /// searching the disk. Which also meant there was no way to keep a copy of
    /// a set-up, or to put one back.
    /// </summary>
    private readonly string _settingsFile;

    public string SettingsFile => _settingsFile;

    /// <summary>
    /// What the three commands below are allowed to do. False in a view model
    /// built without a file — the unit tests build dozens of those, and a
    /// "show me the file" that opens somebody else's is worse than a disabled
    /// one.
    /// </summary>
    public bool HasSettingsFile => _settingsFile.Length > 0;

    /// <summary>Said next to the button rather than in a dialog: none of the
    /// three is worth interrupting for, and a refusal has to be readable.</summary>
    [ObservableProperty]
    private string _settingsFileStatus = "";

    /// <summary>A save-file picker, for somewhere to put a copy.</summary>
    public event EventHandler? SettingsExportRequested;

    /// <summary>
    /// The diagnostics text, for the window to put on the clipboard. Raised
    /// rather than written here because the clipboard belongs to a TopLevel.
    /// </summary>
    public event EventHandler<string>? DiagnosticsRequested;

    /// <summary>
    /// **What a bug report needs, in one paste.** Version and where it runs
    /// from, the platform, how the interface is set up, and the tail of the
    /// log — with every path in it already redacted by the log, so the text
    /// can go into a public issue without a second look. The last line names
    /// the crash marker if the previous run left one.
    /// </summary>
    [RelayCommand]
    private void CopyDiagnostics()
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"vaktari {Program.Version}");
        sb.AppendLine(Vaktari.Core.Diagnostics.Log.Redact($"running from {Program.RunningFrom}"));
        sb.AppendLine($"{Environment.OSVersion} · {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture} · .NET {Environment.Version}");
        sb.AppendLine($"interface text {InterfaceText.ScaleFor(InterfaceTextIndex):P0} · " +
                      $"theme {(FollowDesktopColours ? "desktop" : "bundled")} · " +
                      $"icons {(HasIconTheme ? "theme " + IconThemeLabel : UseSystemIcons ? "desktop" : "bundled")}");
        sb.AppendLine(Vaktari.Core.Diagnostics.Log.Redact($"settings {SettingsFile}"));
        sb.AppendLine();

        var tail = Vaktari.Core.Diagnostics.Log.Tail(200);
        sb.AppendLine(tail.Length > 0 ? "--- last log lines ---" : "--- no log lines ---");
        if (tail.Length > 0) sb.AppendLine(tail);

        DiagnosticsRequested?.Invoke(this, sb.ToString());
    }

    /// <summary>And an open-file picker, for a copy to put back.</summary>
    public event EventHandler? SettingsImportRequested;

    /// <summary>
    /// Opens the folder the file is in, rather than the file.
    ///
    /// Opening settings.json itself would hand it to whatever the desktop has
    /// registered for .json, which on a machine with a development environment
    /// installed is an IDE taking twenty seconds to start. The folder is what
    /// the question "where is it?" actually asks.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasSettingsFile))]
    private void ShowSettingsFile()
    {
        if (Path.GetDirectoryName(_settingsFile) is not { Length: > 0 } folder) return;

        OpenUrlRequested?.Invoke(this, folder);
    }

    [RelayCommand(CanExecute = nameof(HasSettingsFile))]
    private void ExportSettings() => SettingsExportRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand(CanExecute = nameof(HasSettingsFile))]
    private void ImportSettings() => SettingsImportRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Writes what is on screen to a chosen path. Not the file on disk: see
    /// the comment on Collect.
    /// </summary>
    internal bool ExportTo(string path)
    {
        var written = Settings.JsonSettingsStore.Export(path, Collect());

        SettingsFileStatus = written
            ? $"Saved a copy as {Path.GetFileName(path)}."
            : "That file could not be written.";

        return written;
    }

    /// <summary>
    /// Puts a chosen file back, and leaves through the same door Save uses.
    ///
    /// **Closing is the point, not a shortcut.** Every control on these seven
    /// pages was seeded from the state this dialog opened with, so a file read
    /// in behind them would leave forty boxes showing the old values over the
    /// new ones — and pressing Save would then write the old values straight
    /// back over the import. Handing the imported state out as the result is
    /// the one shape where what the person sees next is what they imported.
    ///
    /// A file this version cannot read is refused rather than partially
    /// applied, which is why <see cref="Settings.JsonSettingsStore.Import"/>
    /// answers null instead of defaults.
    /// </summary>
    internal bool ImportFrom(string path)
    {
        if (Settings.JsonSettingsStore.Import(path) is not { } state)
        {
            SettingsFileStatus = "That is not a settings file this version can read.";
            return false;
        }

        Result = state;
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);

        return true;
    }

    // ---- fetching one ------------------------------------------------------

    /// <summary>The themes Vaktari can fetch itself.</summary>
    public IReadOnlyList<Core.FileSystem.IconThemeSource> AvailableThemes =>
        Core.FileSystem.IconThemeCatalogue.All;

    /// <summary>
    /// How the fetching is done, so a test can exercise this without a network.
    /// The real one downloads a hundred megabytes.
    /// </summary>
    public static Func<
        Core.FileSystem.IconThemeSource,
        IProgress<Core.FileSystem.FetchProgress>?,
        CancellationToken,
        Task<Core.FileSystem.IconThemeArchive.Installed>> Installer { get; set; } =
        Core.FileSystem.IconThemeInstaller.InstallAsync;

    [ObservableProperty] private bool _isFetchingIconTheme;
    [ObservableProperty] private double _iconThemeProgress;
    [ObservableProperty] private string _iconThemeStatus = "";

    /// <summary>
    /// Whether the fraction means anything yet.
    ///
    /// **False shows a moving indeterminate bar rather than an empty one.** A
    /// server need not say how large a file is — GitHub does not, for the
    /// theme in the catalogue — and a bar pinned at zero for a hundred and ten
    /// megabytes reads as a hung download rather than as a working one.
    /// </summary>
    [ObservableProperty] private bool _iconThemeProgressKnown;

    public bool HasIconThemeStatus => IconThemeStatus.Length > 0;

    partial void OnIconThemeStatusChanged(string value)
        => OnPropertyChanged(nameof(HasIconThemeStatus));

    partial void OnIsFetchingIconThemeChanged(bool value)
    {
        FetchIconThemeCommand.NotifyCanExecuteChanged();
        PickIconThemeArchiveCommand.NotifyCanExecuteChanged();
    }

    private bool CanFetchIconTheme() => !IsFetchingIconTheme;

    /// <summary>
    /// Downloads a theme and puts it to use, with no further asking.
    ///
    /// **The whole point is that nothing else is required of the user.** Doing
    /// this by hand means finding the project, downloading an archive,
    /// extracting it past a wall of "cannot create symbolic link" errors, and
    /// then knowing that the folder to point at is the one holding index.theme
    /// rather than the one the archive made. None of those steps is
    /// interesting and one of them cannot be completed at all on a machine
    /// without Developer Mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFetchIconTheme))]
    private Task FetchIconTheme(Core.FileSystem.IconThemeSource? source) =>
        source is null
            ? Task.CompletedTask
            : InstallAsync(source.Name, "Fetching", p => Installer(source, p, CancellationToken.None));

    /// <summary>
    /// Installs an archive somebody already has.
    ///
    /// **The same unpacking as a fetched one**, which is the point: a theme
    /// downloaded from anywhere hits the same symbolic-link wall, and going
    /// through here is what gets past it. Called by the window once a file has
    /// been chosen.
    /// </summary>
    public Task InstallIconThemeFromAsync(string file) =>
        InstallAsync(
            Path.GetFileName(file), "Unpacking",
            _ => FileInstaller(file, CancellationToken.None));

    /// <summary>
    /// How a file already on disk is unpacked. A seam, like
    /// <see cref="Installer"/>, so a test need not produce a real archive.
    /// </summary>
    public static Func<string, CancellationToken, Task<Core.FileSystem.IconThemeArchive.Installed>>
        FileInstaller { get; set; } = Core.FileSystem.IconThemeInstaller.InstallFromFileAsync;

    private async Task InstallAsync(
        string name,
        string verb,
        Func<IProgress<Core.FileSystem.FetchProgress>?,
             Task<Core.FileSystem.IconThemeArchive.Installed>> run)
    {
        if (IsFetchingIconTheme) return;

        IsFetchingIconTheme = true;
        IconThemeProgress = 0;

        // Indeterminate until something says otherwise, which covers both a
        // server that sends no length and unpacking a file already on disk.
        IconThemeProgressKnown = false;
        IconThemeProblem = "";
        IconThemeStatus = $"{verb} {name}…";

        try
        {
            var progress = new Progress<Core.FileSystem.FetchProgress>(p =>
            {
                IconThemeProgressKnown = p.Fraction is not null;
                IconThemeProgress = p.Fraction ?? 0;

                IconThemeStatus = p.Fraction is { } fraction
                    ? $"{verb} {name}… {fraction:P0}"
                    : $"{verb} {name}… {p.Megabytes:F0} MB";
            });

            var installed = await run(progress);

            // The archive holds several themes — Papirus brings its light and
            // dark variants — so the one whose name matches is the one selected,
            // and the rest join the list.
            var chosen = installed.Themes.FirstOrDefault(t =>
                    string.Equals(Path.GetFileName(t), name, StringComparison.OrdinalIgnoreCase))
                ?? installed.Themes.FirstOrDefault();

            if (chosen is null)
            {
                IconThemeStatus = "";
                IconThemeProblem =
                    $"{name} unpacked, but there was no icon theme inside it — nothing with an "
                    + "index.theme. Choosing a folder by hand still works.";
                return;
            }

            IconThemeFolder = chosen;

            var others = installed.Themes.Count - 1;

            IconThemeStatus = others > 0
                ? $"{Path.GetFileName(chosen)} is now in use. {others} more "
                  + (others == 1 ? "variant is" : "variants are") + " in the list."
                : $"{Path.GetFileName(chosen)} is now in use.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            IconThemeStatus = "";
            IconThemeProblem = $"{name} could not be downloaded. {e.Message}";
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            IconThemeStatus = "";
            IconThemeProblem = $"{name} could not be unpacked. {e.Message}";
        }
        finally
        {
            IsFetchingIconTheme = false;
            IconThemeProgress = 0;
            IconThemeProgressKnown = false;
        }
    }

    /// <summary>
    /// Handed the detected terminals once they are known.
    ///
    /// **Matched by id, not by reference**: the stored preference comes from a
    /// file and this list is built fresh, so comparing objects would silently
    /// fall back to the first row and rewrite the user's choice the moment they
    /// pressed Save. A preference naming something no longer installed keeps
    /// the same treatment it gets everywhere else — ignored, not honoured into
    /// a failure.
    /// </summary>
    public void UseTerminals(IEnumerable<Core.FileSystem.TerminalOption> terminals)
    {
        AvailableTerminals =
        [
            new TerminalChoice("", "Whichever is found first"),
            .. terminals.Select(t => new TerminalChoice(t.Id, t.Name)),
        ];

        OnPropertyChanged(nameof(AvailableTerminals));

        SelectPreferredTerminal();
    }

    /// <summary>The row the seeded state names, or "whichever is found first"
    /// when it names nothing on the list. Shared by <see cref="Seed"/> and
    /// <see cref="UseTerminals"/>, so a restore and a fresh list pick the same
    /// way.</summary>
    private void SelectPreferredTerminal()
        => SelectedTerminal =
            AvailableTerminals.FirstOrDefault(t => t.Id == _original.General.PreferredTerminal)
            ?? AvailableTerminals[0];

    /// <summary>
    /// The running build, shown in the dialog's footer.
    ///
    /// **Here rather than in an About window** because that is the whole
    /// feature: one line, in a dialog that already exists, next to everything
    /// else somebody opens Settings to check. A window whose only job is to
    /// state a version number is a window to design, position, dismiss and
    /// translate.
    ///
    /// Both halves come from Program, which is also what `--version` prints, so
    /// the two cannot disagree — asking the assembly twice by two routes is
    /// exactly how a window and a command line end up naming different builds.
    /// </summary>
    public string VersionLine => _updateAvailable is { } newer
        ? $"Vaktari {Program.Version} — {newer} is available"
        : $"Vaktari {Program.Version}";

    /// <summary>A newer release the once-a-day check found this run, or null.
    /// Handed in by the window, which is where the check lives.</summary>
    private readonly string? _updateAvailable;

    // ---- and what that version IS -------------------------------------------

    /// <summary>
    /// Where this project lives.
    ///
    /// **The version line was the whole of the About box, and it was a dead
    /// end.** It gave a number and, on hover, the path of the binary; there was
    /// nothing saying what changed in that version, nothing naming the project,
    /// and no way to reach either without already knowing where to look.
    /// </summary>
    public const string ProjectUrl = "https://github.com/dkflint723/vaktari";

    /// <summary>
    /// The releases page rather than CHANGELOG.md in the default branch: a
    /// person asking "what changed" from inside version N wants the notes for
    /// the versions AROUND N, and the file in main describes whatever is
    /// unreleased on top of them.
    /// </summary>
    public const string ReleasesUrl = ProjectUrl + "/releases";

    [RelayCommand]
    private void OpenReleases() => OpenUrlRequested?.Invoke(this, ReleasesUrl);

    [RelayCommand]
    private void OpenProject() => OpenUrlRequested?.Invoke(this, ProjectUrl);

    /// <summary>
    /// Where this build is installed — the same answer the version tooltip
    /// gave, said out loud rather than only on hover, because "which of the two
    /// copies am I running" is the question the number alone cannot settle.
    /// </summary>
    public string InstalledAt => VersionPath;

    // ---- default file manager ---------------------------------------------

    /// <summary>Whether to offer the control at all. Null platform, no control:
    /// a switch that cannot work is worse than an absent one.</summary>
    public bool CanBeDefault => _defaults is not null;

    /// <summary>
    /// Whether to offer the desktop's-own-icons choice at all.
    ///
    /// **This checkbox was on screen on Linux, where nothing could act on it.**
    /// The setting is honoured through IconLoader.Files alone, which is
    /// IPlatform.FileIcons: Windows composes an icon per file and has such a
    /// provider, freedesktop answers by icon NAME and has none. So on Linux the
    /// box could be ticked, saved, and found still ticked on reopening, while
    /// every row went on drawing exactly what it drew before — and what the
    /// label promises was already true there, because a Linux listing draws
    /// with the desktop's icon theme anyway.
    ///
    /// Null provider, no control — the same bargain <see cref="CanBeDefault"/>
    /// makes, for the same reason. A platform that grows a per-file provider
    /// gets the control back with nothing here changed.
    /// </summary>
    public bool CanUseDesktopIcons => _desktopIcons is not null;

    /// <summary>The honest limits for this platform, or blank where none.</summary>
    public string DefaultCaveat => _defaults?.Caveat ?? "";

    public bool HasDefaultCaveat => DefaultCaveat.Length > 0;

    [ObservableProperty] private bool _isDefaultFileManager;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDefaultStatus))]
    private string _defaultStatus = "";

    public bool HasDefaultStatus => DefaultStatus.Length > 0;

    /// <summary>
    /// **Applied immediately, not on Apply or Save**, and the label says so.
    ///
    /// Everything else in this dialog edits a copy and commits it whole, which
    /// is what makes Cancel mean something. This does not: it writes to the
    /// system — the registry on Windows, the desktop's MIME database on Linux —
    /// and there is no honest way to stage that. A checkbox would promise the
    /// dialog's usual contract and break it, so this is a button.
    /// </summary>
    [RelayCommand]
    private async Task MakeDefault()
    {
        if (_defaults is null) return;

        var result = _defaults.MakeDefault();

        DefaultStatus = result.Message;
        IsDefaultFileManager = _defaults.IsDefault();

        await ReconcileServiceAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RestoreDefault()
    {
        if (_defaults is null) return;

        var result = _defaults.Restore();

        DefaultStatus = result.Message;
        IsDefaultFileManager = _defaults.IsDefault();

        await ReconcileServiceAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// **Now, not on the next launch.** Becoming the desktop's file manager
    /// also means answering "show this file in its folder", and a bus name can
    /// only be claimed by a process that is running. Without this the button did
    /// half its job and said nothing about the other half — and the user's next
    /// act, quite reasonably, is to go and try it.
    ///
    /// The two commands above return Task and keep their names without an Async
    /// suffix, matching FetchIconTheme in this same file: that is what makes the
    /// generator emit MakeDefaultCommand, which is the name the markup binds. A
    /// rename to MakeDefaultAsync binds to nothing and the button silently stops
    /// working.
    /// </summary>
    private async Task ReconcileServiceAsync()
    {
        if (_fileManager is null) return;

        var state = await _fileManager.ReconcileAsync().ConfigureAwait(true);

        if (Core.FileManagerServiceStates.Describe(state) is { Length: > 0 } sentence)
            DefaultStatus = DefaultStatus.Length > 0
                ? DefaultStatus + " " + sentence
                : sentence;
    }

    /// <summary>
    /// What this desktop calls the bin, for labels that name it.
    ///
    /// Exposed on the view model rather than reached from markup with x:Static
    /// because <c>InitializeComponent</c> runs before the platform is chosen —
    /// an x:Static reference is resolved as the XAML is parsed and would bake in
    /// the default. A binding is evaluated when the DataContext arrives, which
    /// is after.
    /// </summary>
    public string BinName => Core.Naming.BinName;

    /// <summary>The same inside a sentence: "the Recycle Bin", "the trash".</summary>
    public string TheBin => Core.Naming.TheBin;

    /// <summary>The same starting a label: "Recycle Bin", "Trash".</summary>
    public string BinTitle => Core.Naming.BinTitle;

    /// <summary>
    /// The bin page's name in the strip, with its access key: Alt+B for the
    /// Recycle Bin, Alt+T for the trash. Its own letter on each platform,
    /// because the word is different — and neither letter is taken by the
    /// other six pages (SettingsPagesTests counts them).
    /// </summary>
    public string BinPageHeader => BinPageHeaderFor(BinTitle);

    /// <summary>The marker goes on the last word's first letter: "Recycle
    /// _Bin", "_Trash".</summary>
    internal static string BinPageHeaderFor(string title)
    {
        var last = title.LastIndexOf(' ') + 1;

        return title[..last] + "_" + title[last..];
    }

    /// <summary>
    /// What the Proton Drive box suggests, as a path on THIS machine.
    ///
    /// **It said "D:\Proton-Drive" on Linux**, the startup box's old mistake
    /// in the other direction: a path shape one platform cannot have, in the
    /// one box whose whole job is to be given a path here. Built from the
    /// profile folder, where the Proton Drive app puts it unless told
    /// otherwise.
    /// </summary>
    public static string ProtonDriveHint
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? "e.g. " + Path.Combine(home, "Proton Drive")
            : "The folder the Proton Drive app syncs";

    /// <summary>
    /// The bin page's note, in a line: the part a person must not miss, which
    /// is that the bin is shared. The whole of <see cref="BinSweepExplanation"/>
    /// is the line's tooltip and its help text.
    /// </summary>
    public string BinSweepSummary =>
        $"{TheBin[0].ToString().ToUpperInvariant()}{TheBin[1..]} is shared, so this also removes what other applications put there.";

    public string ConfirmTrashLabel => $"Moving files to {TheBin}";

    public string LimitBinLabel => $"Limit {TheBin} to a share of the disk";

    /// <summary>
    /// What sweeping actually touches, which is not the same sentence on both
    /// platforms.
    ///
    /// **The old text named another file manager**, and was on screen on
    /// Windows, where that name means nothing. Neither branch names a specific
    /// application now: what matters is that the bin is shared, not which
    /// program it is shared with.
    ///
    /// The per-volume caveat is true on both, for the same reason: a file
    /// deleted from another drive goes to a bin on that drive.
    /// </summary>
    public string BinSweepExplanation =>
        Core.Naming.Platform != "windows"
            ? "The trash is shared with the rest of the desktop, so this also "
              + "removes what other applications put there. Items whose deletion "
              + "date cannot be read are always left alone. Files deleted from "
              + "another drive live in a trash on that drive and are not covered."
            : "The Recycle Bin is shared with File Explorer, so this also removes "
              + "what other applications put there. Items whose deletion date "
              + "cannot be read are always left alone. Files deleted from another "
              + "drive live in a Recycle Bin on that drive and are not covered.";



    /// <summary>
    /// The file the running process came from, as the footer's tooltip.
    ///
    /// The version on its own does not answer "which copy am I running", which
    /// is the question that actually gets asked — a stale <c>~/.local</c>
    /// install shadowed a packaged one for three days, and both reported
    /// plausible numbers.
    /// </summary>
    public string VersionPath => Program.RunningFrom;

    // Assigned by Seed before the constructor returns, from a list built the
    // line before it. The initialiser is for the compiler, which cannot see
    // through the generated setter to know that.
    [ObservableProperty] private FontOption _selectedFont = null!;

    /// <summary>
    /// Whether the desktop's own colours are layered over the bundled scheme.
    ///
    /// **The flag is older than this control.** It has been read on every theme
    /// apply since the scheme was inverted to run first, and worked — but
    /// nothing ever offered it, so the only way to turn it on was to hand-edit
    /// settings.json, while the README described it as though there were a
    /// switch. There is one now.
    ///
    /// Light and dark are NOT this setting. Which of the two the bundled scheme
    /// uses always follows the desktop, because a pitch-black window on a
    /// machine set to light is a bug rather than a preference. This chooses
    /// whether the desktop's HUES come too.
    /// </summary>
    [ObservableProperty] private bool _followDesktopColours;

    /// <summary>
    /// Light, dark or follow the desktop, as a ComboBox index.
    ///
    /// **An index rather than the enum**, because binding SelectedItem to an
    /// enum needs either an ObjectDataProvider-style items source or a converter
    /// per direction, and the list here is three fixed rows that will not grow.
    /// SelectedIndex is the honest way to say that. The order is fixed by
    /// <see cref="ThemeModeFromIndex"/>, which is the only place it is decoded.
    /// </summary>
    [ObservableProperty] private int _themeModeIndex;

    private Core.Settings.ThemeMode ThemeModeFromIndex() => ThemeModeIndex switch
    {
        1 => Core.Settings.ThemeMode.Light,
        2 => Core.Settings.ThemeMode.Dark,
        _ => Core.Settings.ThemeMode.FollowDesktop,
    };

    /// <summary>
    /// The one Colour chooser: Vaktari's colours following the desktop's light
    /// or dark, Vaktari's colours light, Vaktari's colours dark, or the
    /// desktop's own colours — rows 0 to 3.
    ///
    /// **Two controls answered one question, and one combination of them was
    /// broken.** "Follow desktop colours" layers the desktop's window and view
    /// backgrounds, text, selection and accent — and its font, when none is
    /// chosen — over the bundled scheme (ThemeApplier.Apply). The backgrounds
    /// come in the desktop's OWN lightness, while the Colour row above it went
    /// on telling Fluent light or dark: so "Light" with desktop colours on a
    /// dark desktop drew dark surfaces under Fluent's dark text, the 1.02:1
    /// failure that file was repaired for. As one list, the desktop's colours
    /// are one row and come with the desktop's lightness.
    ///
    /// On the two stored keys, unchanged. Choosing row 3 stores FollowDesktop
    /// with the flag; a file that already pairs the flag with a forced
    /// lightness shows as row 3 and is saved as it was until another row is
    /// picked — a dialog nobody touched does not rewrite a choice.
    /// </summary>
    public int ColourIndex
    {
        get => FollowDesktopColours ? 3 : ThemeModeIndex;
        set
        {
            if (value == ColourIndex) return;

            if (value == 3)
            {
                ThemeModeIndex = 0;
                FollowDesktopColours = true;
            }
            else
            {
                FollowDesktopColours = false;
                ThemeModeIndex = value is 1 or 2 ? value : 0;
            }
        }
    }

    partial void OnThemeModeIndexChanged(int value) => OnPropertyChanged(nameof(ColourIndex));

    partial void OnFollowDesktopColoursChanged(bool value) => OnPropertyChanged(nameof(ColourIndex));

    /// <summary>
    /// What happens when the details panel is asked for and does not fit: grey
    /// its button out, widen the window and shrink it back afterwards, or widen
    /// it and leave it wide — rows 0 to 2.
    ///
    /// **Two check boxes, the second greyed out under the first, with two
    /// paragraphs between them.** The second only means anything when the
    /// first is ticked, so they were one question with three answers spread
    /// over a quarter of a page. The two stored keys are unchanged: the first
    /// row leaves KeepWidthAfterPanelClose as it was, because nothing is widened
    /// for it to act on.
    /// </summary>
    public int DetailsPanelIndex
    {
        get => !GrowWindowForPanel ? 0 : RestoreWidthOnPanelClose ? 1 : 2;
        set
        {
            if (value == DetailsPanelIndex) return;

            GrowWindowForPanel = value is 1 or 2;
            if (value is 1 or 2) RestoreWidthOnPanelClose = value == 1;
        }
    }

    partial void OnGrowWindowForPanelChanged(bool value) => OnPropertyChanged(nameof(DetailsPanelIndex));

    partial void OnRestoreWidthOnPanelCloseChanged(bool value) => OnPropertyChanged(nameof(DetailsPanelIndex));

    /// <summary>
    /// How large the interface's text is drawn, as a ComboBox index.
    ///
    /// **The size of the interface was the one thing in this dialog you could
    /// not change**, and outside it the only control was a per-pane zoom that
    /// left the sidebar, the tabs, the toolbar and the status bar exactly where
    /// they were. An index for the same reason ThemeModeIndex is one; the rows
    /// are decoded in exactly one place, <see cref="InterfaceText.Steps"/>,
    /// which the markup's labels are checked against.
    /// </summary>
    [ObservableProperty] private int _interfaceTextIndex;

    /// <summary>Marks modified, added, untracked and conflicted files in a
    /// repository. Only ever visible inside one.</summary>
    [ObservableProperty] private bool _showVcsDecorations;

    /// <summary>
    /// A tick box ahead of every row, and one on the list heading.
    ///
    /// Off by default and phrased positively here because the dialog asks the
    /// positive question; the record stores the same sense, since off is what
    /// its zero value has to mean.
    /// </summary>
    [ObservableProperty] private bool _showSelectionBoxes;

    /// <summary>
    /// Draw ".txt" on the end of the name, in all three layouts.
    ///
    /// Phrased positively because that is the question Explorer's View ribbon
    /// asks, and inverted on the way in and out because
    /// <c>ViewSettings.HideFileExtensions</c> is named for its zero value —
    /// the same arrangement as <see cref="RestoreWidthOnPanelClose"/> below.
    /// </summary>
    [ObservableProperty] private bool _showFileExtensions;

    /// <summary>
    /// True: widen the window to fit the details panel. False: grey the toggle
    /// out. A bool rather than the enum because the dialog binds checkboxes, and
    /// two states do not need a picker.
    /// </summary>
    [ObservableProperty] private bool _growWindowForPanel;

    /// <summary>Hand the width back when the panel closes. Only does anything
    /// when <see cref="GrowWindowForPanel"/> is on.</summary>
    [ObservableProperty] private bool _restoreWidthOnPanelClose;

    /// <summary>Extra gap between grid tiles, in pixels. Blank means none.</summary>
    [ObservableProperty] private string _iconSpacing = "";

    /// <summary>Extra gap around compact cells, in pixels. Blank means none.</summary>
    [ObservableProperty] private string _compactSpacing = "";

    /// <summary>
    /// Installed families, sorted, with the follow-the-desktop sentinel first.
    ///
    /// <paramref name="configured"/> is added even when it is not installed:
    /// silently dropping a font someone chose — because they are on a different
    /// machine, or uninstalled it — would rewrite their settings the moment they
    /// opened this dialog and pressed Save.
    /// </summary>
    private static IReadOnlyList<FontOption> BuildFontList(string? configured)
    {
        var names = new List<string> { FollowDesktop };

        var installed = FontManager.Current.SystemFonts
            .Select(f => f.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        names.AddRange(installed);

        if (configured is { Length: > 0 }
            && !names.Contains(configured, StringComparer.OrdinalIgnoreCase))
            names.Insert(1, configured);

        // Traced because "my font is not in the list" has two very different
        // causes — the font is not installed, or Avalonia's font manager does
        // not enumerate what fontconfig knows about — and the count alone
        // separates them. Compare with: fc-list : family | sort -u | wc -l
        // Prefixed "fontlist", not "font": ThemeApplier already logs
        // "[vaktari] font: configured=… applied=…" and one grep matched both,
        // which sent a diagnostic session off in the wrong direction. Two
        // different facts get two different prefixes.
        Console.Error.WriteLine(
            $"[vaktari] fontlist: {names.Count - 1} families enumerated");

        if (Environment.GetEnvironmentVariable("VAKTARI_FONT_DEBUG") == "1")
            foreach (var name in names.Skip(1))
                Console.Error.WriteLine($"[vaktari] fontlist: {name}");

        return names
            .Select(n => new FontOption(n, new FontFamily(n), n == FollowDesktop))
            .ToList();
    }
    [ObservableProperty] private bool _absoluteDates;
    /// <summary>
    /// A folder tree in the sidebar. **Reachable from here or it is not a
    /// setting**, which is the lesson the folder-size mode beside it taught:
    /// that one sat in the model for the life of the feature, writable only by
    /// editing settings.json, because no control could draw it.
    /// </summary>
    [ObservableProperty] private bool _showFolderTree;

    /// <summary>
    /// The Size column's answer for a folder, as three radios rather than the
    /// tick box this was: the box could say "counts" or "nothing" and had no
    /// way to say "how big it is", so that mode sat in the settings file
    /// unreachable from the dialog that wrote the file.
    /// </summary>
    [ObservableProperty] private bool _folderSizeCounts;

    [ObservableProperty] private bool _folderSizeContents;

    [ObservableProperty] private bool _folderSizeNothing;

    /// <summary>
    /// The three folder-size answers as one dropdown's row — how many things,
    /// how big, nothing — over the three booleans <see cref="Collect"/> reads.
    /// A dropdown for the same reason as <see cref="StartupIndex"/>: three
    /// radios and a two-line note under the middle one were a quarter of the
    /// page for one question.
    /// </summary>
    public int FolderSizeIndex
    {
        // Decoded in Collect's order, so a state with two set reads as the
        // row it would be saved as.
        get => FolderSizeContents ? 1 : FolderSizeCounts ? 0 : 2;
        set
        {
            if (value == FolderSizeIndex) return;

            FolderSizeCounts = value is not (1 or 2);
            FolderSizeContents = value == 1;
            FolderSizeNothing = value == 2;
        }
    }

    partial void OnFolderSizeCountsChanged(bool value) => OnPropertyChanged(nameof(FolderSizeIndex));

    partial void OnFolderSizeContentsChanged(bool value) => OnPropertyChanged(nameof(FolderSizeIndex));

    partial void OnFolderSizeNothingChanged(bool value) => OnPropertyChanged(nameof(FolderSizeIndex));

    // ---- Navigation -------------------------------------------------------
    //
    // **This said "one setting" and there are two.** Dolphin has no control of
    // its own for the click one at all — it points at System Settings — but
    // Vaktari keeps an override because it also has to run on Windows, where
    // there is nothing to defer to. The second is Backspace, which is a
    // preference for the same reason the first is: two desktops taught two
    // different habits and neither of them is wrong.
    //
    // "Open folders during drag" (spring-loaded folders) is the other item on
    // Dolphin's page and is not built, so it is not offered.

    [ObservableProperty] private bool _openWithSystem;
    [ObservableProperty] private bool _openWithSingle;
    [ObservableProperty] private bool _openWithDouble;

    /// <summary>
    /// **Backspace answered Explorer's habit and nobody else's**, and the two
    /// habits disagree: Explorer's goes Back, Dolphin's goes up one folder.
    /// The checkbox asks the Dolphin question so that OFF is the shipped
    /// behaviour — the record stores the same polarity, for the reason spelled
    /// out on the property itself.
    /// </summary>
    [ObservableProperty] private bool _backspaceGoesUp;

    // ---- Trash ------------------------------------------------------------

    [ObservableProperty] private bool _deleteOldTrash;
    [ObservableProperty] private string _deleteAfterDays = "";
    [ObservableProperty] private bool _limitTrashSize;
    [ObservableProperty] private string _maxPercentOfDisk = "";
    [ObservableProperty] private bool _limitActionWarn;
    [ObservableProperty] private bool _limitActionOldest;
    [ObservableProperty] private bool _limitActionLargest;

    public bool CanSetTrashAge => DeleteOldTrash;
    public bool CanSetTrashSize => LimitTrashSize;

    partial void OnDeleteOldTrashChanged(bool value)
        => OnPropertyChanged(nameof(CanSetTrashAge));

    partial void OnLimitTrashSizeChanged(bool value)
        => OnPropertyChanged(nameof(CanSetTrashSize));

    /// <summary>
    /// Clamped, and a bad value disables rather than defaults. Zero days would
    /// mean "delete everything immediately", which is not a plausible thing to
    /// have meant by typing badly.
    /// </summary>
    private static int Days(string text)
        => int.TryParse(text, out var value) && value > 0 ? value : 0;

    /// <summary>
    /// Spacing is clamped rather than rejected: unlike a trash age, zero is a
    /// perfectly sensible answer here — it is the default — so a bad value
    /// falls back to none instead of disabling anything.
    /// </summary>
    private static int Spacing(string text)
        => int.TryParse(text, out var value) && value > 0 ? Math.Min(value, 48) : 0;

    private static int Percent(string text)
        => int.TryParse(text, out var value) && value is > 0 and <= 100 ? value : 0;

    /// <summary>Set when the dialog was dismissed with Save.</summary>
    public bool Saved { get; private set; }

    public SettingsState Result { get; private set; } = new();

    /// <summary>
    /// The folder box is only meaningful for one of the four choices, so it
    /// disables with the others rather than accepting input that will be
    /// ignored.
    /// </summary>
    public bool CanEditStartupFolder => StartInSpecificFolder;

    partial void OnStartInSpecificFolderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditStartupFolder));
        OnPropertyChanged(nameof(StartupIndex));

        // The warning under the box is about a folder that is only consulted
        // for this one choice, so turning the choice off takes it away.
        StartupFolderChecked();
    }

    [RelayCommand]
    private void Save()
    {
        Result = Collect();
        Saved = true;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Raised by Apply with what to commit. The window that opened the dialog
    /// answers it through the same code a Save reaches on close, so there is
    /// one way a choice lands rather than two to keep in step.
    /// </summary>
    public event EventHandler<SettingsState>? ApplyRequested;

    /// <summary>How many times Apply has committed since the dialog opened.</summary>
    public int AppliedCount { get; private set; }

    /// <summary>
    /// Commits what is on screen now, without closing.
    ///
    /// **A change could only be seen by closing the dialog**, so trying a
    /// colour, a text size or an icon theme meant Save, look, reopen, find the
    /// page again, change it back. Apply is Save without the close: the same
    /// <see cref="Collect"/>, handed to the same code a Save reaches on close
    /// — MainWindow.Commit, which applies to every window in the family and
    /// writes the file.
    ///
    /// **Cancel after an Apply keeps what was applied**, and drops only what
    /// changed since. That is the contract of every property sheet with an
    /// Apply button, Windows' and KDE's both — undoing an Apply on Cancel
    /// would make the button a preview rather than a commit, and would have to
    /// un-forget a history it has already emptied. The footer says so the
    /// moment it happens, which is when somebody might wonder.
    ///
    /// The three armed Forget buttons are carried out here too, and then
    /// disarmed and zeroed, so the rows that offered them disappear rather
    /// than offering to empty a list that is already empty.
    /// </summary>
    [RelayCommand]
    private void Apply()
    {
        Result = Collect();
        AppliedCount++;

        ApplyRequested?.Invoke(this, Result);

        if (ForgetViewsOnSave) { ForgetViewsOnSave = false; RememberedViews = 0; }
        if (ForgetRecentOnSave) { ForgetRecentOnSave = false; RecentCount = 0; }
        if (ForgetSearchHistoryOnSave) { ForgetSearchHistoryOnSave = false; SearchCount = 0; }

        SettingsFileStatus = "Applied. Cancel now closes without undoing that — only later changes are dropped.";
    }

    /// <summary>
    /// Everything on the seven pages, as the record that gets written.
    ///
    /// Lifted out of Save so that exporting writes what is ON SCREEN rather
    /// than what is on disk. Exporting the file would have been simpler and
    /// wrong: the dialog is where the choices are made, so the interesting
    /// moment for "save a copy of this" is after changing something and before
    /// pressing Save — and a file copy at that moment silently exports the
    /// state the person is in the middle of replacing.
    /// </summary>
    private SettingsState Collect()
    {
        // Above StartInHome, below the folder — and the position is not load
        // bearing: the order decides only what a state with two of them set
        // would collapse to, and the page's one dropdown sets exactly one
        // (StartupIndex) so the dialog does not hand one over. Restore stays
        // LAST, which is the one position that matters: it is the fallback, so
        // a dialog nobody touched saves the default rather than a choice nobody
        // made.
        var location = StartInSpecificFolder ? StartupLocation.SpecificFolder
            : StartInComputer ? StartupLocation.Computer
            : StartInHome ? StartupLocation.HomeFolder
            : StartupLocation.RestoreSession;


        // `with` on the whole state, so pages that are not built yet keep
        // whatever is already in the file rather than being reset to defaults
        // by a dialog that never showed them.
        return _original with
        {
            General = _original.General with
            {
                NaturalSorting = NaturalSorting,
                CaseSensitiveSorting = CaseSensitiveSorting,
                MixFoldersWithFiles = !FoldersFirst,
                RememberViewPerFolder = RememberViewPerFolder,
                ShowTooltips = ShowTooltips,
                RememberRecent = RememberRecent,
                CheckForUpdates = CheckForUpdates,
                ForgetSearches = !RememberSearches,
                TabSwitchesSplitPanes = TabSwitchesSplitPanes,
                ClosingSplitDiscardsOtherPane = ClosingSplitDiscardsOtherPane,
                ShowStatusBar = ShowStatusBar,
                ShowFreeSpace = ShowFreeSpace,
                ShowPreviews = ShowPreviews,
                MaxLocalPreviewMegabytes = Megabytes(MaxLocalPreviewMegabytes),
                MaxRemotePreviewMegabytes = Megabytes(MaxRemotePreviewMegabytes),
                ConfirmMoveToTrash = ConfirmMoveToTrash,
                ConfirmPermanentDelete = ConfirmPermanentDelete,
                ConfirmClosingMultipleTabs = ConfirmClosingMultipleTabs,
                PreferredTerminal = SelectedTerminal?.Id ?? "",
                UseSystemIcons = UseSystemIcons,
                IconThemeFolder = IconThemeFolder,
                ProtonDriveFolder = ProtonDriveFolder.Trim(),
            },

            // Also guarded: `with` on a null record throws, so saving would have
            // crashed too even once the dialog opened.
            Vcs = (_original.Vcs ?? new VcsSettings())
                  with { ShowDecorations = ShowVcsDecorations },

            Views = _original.Views with
            {
                NarrowDetailsPanel = GrowWindowForPanel
                    ? NarrowPanelBehaviour.GrowWindow
                    : NarrowPanelBehaviour.DisableToggle,

                KeepWidthAfterPanelClose = !RestoreWidthOnPanelClose,

                CustomFontFamily = SelectedFont is null || SelectedFont.IsFollowDesktop
                    ? null
                    : SelectedFont.Name,

                FollowDesktopColours = FollowDesktopColours,
                ShowFolderTree = ShowFolderTree,
                ShowSelectionBoxes = ShowSelectionBoxes,
                HideFileExtensions = !ShowFileExtensions,
                ThemeMode = ThemeModeFromIndex(),
                InterfaceTextScale = InterfaceText.ScaleFor(InterfaceTextIndex),

                Icons = _original.Views.Icons with { Spacing = Spacing(IconSpacing) },
                Compact = _original.Views.Compact with { Spacing = Spacing(CompactSpacing) },

                Details = _original.Views.Details with
                {
                    DateStyle = AbsoluteDates
                        ? Core.Settings.DateStyle.Absolute
                        : Core.Settings.DateStyle.Relative,

                    // **All three are reachable now.** Only two were, so the
                    // third had to be preserved rather than overwritten by a
                    // control that never showed it — a tick box cannot offer a
                    // mode it has no way to draw. The size of a folder's
                    // contents needed recursive summing that did not exist;
                    // SpaceUsage.Measure is it.
                    FolderSize = FolderSizeContents
                        ? Core.Settings.FolderSizeMode.ContentSize
                        : FolderSizeCounts
                            ? Core.Settings.FolderSizeMode.ItemCount
                            : Core.Settings.FolderSizeMode.None,
                },
            },

            Trash = _original.Trash with
            {
                // A field that will not parse turns the feature OFF rather than
                // falling back to a default. Guessing a number here means
                // deleting files against something the user did not type.
                DeleteOldFiles = DeleteOldTrash && Days(DeleteAfterDays) > 0,
                DeleteAfterDays = Days(DeleteAfterDays) is > 0 and var d
                    ? d
                    : _original.Trash.DeleteAfterDays,

                LimitSize = LimitTrashSize && Percent(MaxPercentOfDisk) > 0,
                MaximumPercentOfDisk = Percent(MaxPercentOfDisk) is > 0 and var p
                    ? p
                    : _original.Trash.MaximumPercentOfDisk,

                WhenLimitReached = LimitActionOldest ? TrashLimitAction.DeleteOldest
                    : LimitActionLargest ? TrashLimitAction.DeleteLargest
                    : TrashLimitAction.Warn,
            },

            Navigation = _original.Navigation with
            {
                OpenItemsWith = OpenWithSingle ? ActivationClick.Single
                    : OpenWithDouble ? ActivationClick.Double
                    : ActivationClick.System,

                BackspaceGoesUp = BackspaceGoesUp,
            },

            // The rows that differ from their shipped keys, and whatever a
            // newer Vaktari left — see KeyboardPage.Collect.
            Keyboard = _original.Keyboard with { Bindings = Keyboard.Collect() },

            ContextMenu = _original.ContextMenu with
            {
                ShowCopyTo = MenuCopyTo,
                ShowMoveTo = MenuMoveTo,
                ShowSortBy = MenuSortBy,
                ShowDuplicate = MenuDuplicate,
                ShowOpenInNewTab = MenuOpenInNewTab,
                ShowOpenInNewWindow = MenuOpenInNewWindow,
                ShowAddToPlaces = MenuAddToPlaces,
                ShowCopyLocation = MenuCopyLocation,
            },

            Startup = _original.Startup with
            {
                ShowOnStartup = location,
                StartupFolder = string.IsNullOrWhiteSpace(StartupFolder) ? null : StartupFolder.Trim(),
                BeginInSplitView = BeginInSplitView,
                ShowFilterBar = ShowFilterBar,
                LocationBarEditable = LocationBarEditable,
                ShowFullPathInTitleBar = ShowFullPathInTitleBar,
            },
        };
    }

    // ---- the startup folder box ---------------------------------------------

    /// <summary>
    /// What an empty box suggests typing.
    ///
    /// **It said "/home/…" on Windows.** A hardcoded Linux path, in the one
    /// control whose whole job is to be given a path on THIS machine. Built
    /// from the profile directory instead, so it is both platform-correct and
    /// a real folder rather than a shape.
    /// </summary>
    public static string StartupFolderHint
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } home
            ? home
            : "";

    /// <summary>A folder picker, so the path does not have to be typed.</summary>
    public event EventHandler? StartupFolderBrowseRequested;

    [RelayCommand]
    private void BrowseForStartupFolder()
        => StartupFolderBrowseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Said under the box when the folder typed there is not there.
    ///
    /// **A folder that does not exist was accepted in silence and then ignored
    /// in silence.** The dialog saved whatever was typed, and the next launch
    /// found no such directory and opened home instead — so a typo, or a path
    /// on a drive that had been repartitioned, looked exactly like the setting
    /// not working.
    ///
    /// A WARNING rather than a refusal, and that is the whole judgement here:
    /// the folder may be on a stick that is out, or a share that is down, and
    /// refusing to save it would make the setting unusable for exactly the
    /// people who need it most. So it is saved, and said.
    /// </summary>
    public string StartupFolderProblem
        => !StartInSpecificFolder || StartupFolder.Trim().Length == 0
            ? ""
            : Directory.Exists(StartupFolder.Trim())
                ? ""
                : "That folder is not there at the moment. It will be saved, and "
                  + "Vaktari will open your home folder until it comes back.";

    public bool HasStartupFolderProblem => StartupFolderProblem.Length > 0;

    partial void OnStartupFolderChanged(string value) => StartupFolderChecked();

    private void StartupFolderChecked()
    {
        OnPropertyChanged(nameof(StartupFolderProblem));
        OnPropertyChanged(nameof(HasStartupFolderProblem));
    }

    // ---- the recent lists nothing could stop ---------------------------------

    /// <summary>
    /// How many recent entries there were when this dialog opened.
    ///
    /// **They were recorded unconditionally and could not be emptied.** Every
    /// folder and every file opened went in, no setting was consulted, and the
    /// only way out was a per-row "Forget" needing the entry still on screen.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecent), nameof(RecentCountLabel))]
    [NotifyCanExecuteChangedFor(nameof(ForgetRecentCommand))]
    private int _recentCount;

    public bool HasRecent => RecentCount > 0;

    public string RecentCountLabel => RecentCount == 1
        ? "One entry is remembered"
        : $"{RecentCount:N0} entries are remembered";

    /// <summary>
    /// Armed like the folder views beside it: Save clears them, Cancel does
    /// not. And SEPARATE from the switch, deliberately — turning recording off
    /// does not delete what is already there, because silently emptying
    /// somebody's list from a checkbox is not what the checkbox says.
    /// </summary>
    public bool ForgetRecentOnSave { get; private set; }

    [RelayCommand(CanExecute = nameof(HasRecent))]
    private void ForgetRecent()
    {
        ForgetRecentOnSave = true;

        SettingsFileStatus = RecentCount == 1
            ? "One remembered entry will be forgotten when you press Apply or Save."
            : $"{RecentCount:N0} remembered entries will be forgotten when you press Apply or Save.";
    }

    // ---- the searches nothing recorded --------------------------------------

    /// <summary>
    /// How many searches were held when this dialog opened.
    ///
    /// **Nothing recorded what had been searched for**, so this list is new —
    /// and it arrives with the switch and the button, rather than a release
    /// later, because the recent lists next door proved what a history with
    /// neither is: unstoppable, unemptiable, and only removable by finding the
    /// file.
    ///
    /// Read once, at open, rather than live — the same way the recent count
    /// beside it is read. It is a size to decide by rather than a gauge, and
    /// the clearing is not sized by it: Save empties whatever the store holds
    /// at that moment, so a search run in another window between opening this
    /// dialog and pressing Save is forgotten too, whatever this number said.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchHistory), nameof(SearchCountLabel))]
    [NotifyCanExecuteChangedFor(nameof(ForgetSearchHistoryCommand))]
    private int _searchCount;

    public bool HasSearchHistory => SearchCount > 0;

    public string SearchCountLabel => SearchCount == 1
        ? "One search is remembered"
        : $"{SearchCount:N0} searches are remembered";

    /// <summary>
    /// Armed like the two beside it: Save clears them, Cancel does not. And
    /// SEPARATE from the switch — turning recording off does not delete what
    /// is already there, because that is not what the checkbox says.
    /// </summary>
    public bool ForgetSearchHistoryOnSave { get; private set; }

    [RelayCommand(CanExecute = nameof(HasSearchHistory))]
    private void ForgetSearchHistory()
    {
        ForgetSearchHistoryOnSave = true;

        SettingsFileStatus = SearchCount == 1
            ? "One remembered search will be forgotten when you press Apply or Save."
            : $"{SearchCount:N0} remembered searches will be forgotten when you press Apply or Save.";
    }

    // ---- the folder views nothing could see ---------------------------------

    /// <summary>
    /// How many folders were being remembered when this dialog opened.
    ///
    /// **Turning the setting off left every folder already recorded exactly as
    /// it was.** The store is written to by merely LOOKING at a folder, so it
    /// fills up on its own; IFolderViewStore.Forget(path) existed and nothing
    /// had ever called it, and the file was invisible from the application. So
    /// a listing that had once been given a layout kept it with the feature
    /// switched off, and the only way to say otherwise was to find the file and
    /// delete it.
    ///
    /// Read once, at open, rather than live: this dialog is modal and nothing
    /// behind it is browsing. Zeroed by Apply once it has forgotten them.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRememberedViews), nameof(RememberedViewsLabel))]
    [NotifyCanExecuteChangedFor(nameof(ForgetRememberedViewsCommand))]
    private int _rememberedViews;

    public bool HasRememberedViews => RememberedViews > 0;

    public string RememberedViewsLabel => RememberedViews == 1
        ? "One folder is remembered"
        : $"{RememberedViews:N0} folders are remembered";

    /// <summary>
    /// Whether Save should clear them. **Armed rather than done**, so this
    /// behaves like every other control here: Cancel throws it away, and the
    /// one handler that already applies a save does this too rather than a
    /// second route reaching the store directly.
    /// </summary>
    public bool ForgetViewsOnSave { get; private set; }

    [RelayCommand(CanExecute = nameof(HasRememberedViews))]
    private void ForgetRememberedViews()
    {
        ForgetViewsOnSave = true;

        SettingsFileStatus = RememberedViews == 1
            ? "One remembered folder view will be forgotten when you press Apply or Save."
            : $"{RememberedViews:N0} remembered folder views will be forgotten when you press Apply or Save.";
    }

    /// <summary>
    /// Puts every setting back to what a first run would have given.
    ///
    /// **There was no way back.** Seven pages, every setting on them
    /// remembering what it was last set to, and the only route to the
    /// defaults was to close Vaktari, find settings.json — which nothing in
    /// the application could name until recently — delete it, and start
    /// again.
    ///
    /// **No confirmation, and that is not carelessness.** This dialog edits a
    /// copy and commits it whole on Apply or Save, so Cancel discards this
    /// exactly as it discards any other change not yet applied: the defaults
    /// are on screen to be looked at, and nothing has reached disk. A
    /// confirmation would be asking permission for something the next button
    /// already undoes.
    ///
    /// The state seeded is a bare SettingsState, which also resets the pages
    /// this dialog has not built — Collect carries _original forward with
    /// `with`, so replacing it is what makes "every setting" true rather than
    /// "every setting you can see".
    /// </summary>
    [RelayCommand]
    private void RestoreDefaults()
    {
        Seed(new SettingsState());

        SettingsFileStatus = "Defaults restored — nothing is saved until you press Apply or Save.";
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CloseRequested;
}
