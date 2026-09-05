using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vaktari.Core.Settings;

namespace Vaktari.Ui.ViewModels;

/// <summary>
/// Edits a copy and commits it whole, rather than writing each control as it
/// changes. Cancel then genuinely cancels, and a half-finished set of
/// preferences never reaches disk.
///
/// Only the Startup page exists so far. The remaining five are separate pieces
/// of work, each landing with the plumbing that makes its toggles do something
/// — a control that does nothing is worse than an absent one, and this project
/// requires the UI to be usable by someone with no prior knowledge of it.
/// </summary>
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

    private readonly Core.IDefaultFileManager? _defaults;
    private readonly Core.FileSystem.IFileIconProvider? _desktopIcons;
    private readonly Core.IFileManagerService? _fileManager;

    public SettingsViewModel(
        SettingsState current,
        Core.IDefaultFileManager? defaults = null,
        Core.FileSystem.IFileIconProvider? desktopIcons = null,
        Core.IFileManagerService? fileManager = null,
        string? settingsFile = null,
        Core.FileSystem.IFolderViewStore? folderViews = null)
    {
        _rememberedViews = folderViews?.Remembered ?? 0;

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
    /// Every control on the six pages, from a state.
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
        RememberViewPerFolder = general.RememberViewPerFolder;
        ShowTooltips = general.ShowTooltips;
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
        ThemeModeIndex = views.ThemeMode switch
        {
            Core.Settings.ThemeMode.Light => 1,
            Core.Settings.ThemeMode.Dark => 2,
            _ => 0,
        };
        AbsoluteDates = views.Details.DateStyle == Core.Settings.DateStyle.Absolute;
        ShowFolderItemCounts = views.Details.FolderSize != Core.Settings.FolderSizeMode.None;

        // Blank rather than "0": the placeholder says what zero means, and an
        // empty box invites a value where a literal 0 looks like a setting
        // someone already made.
        // `?? true` because the group is demonstrably null for a settings file
        // written before it existed, and the DEFAULT is on. Guarding here as
        // well as in the pane: the same dereference crashed the listing, I fixed
        // that one site, and left this one to crash the dialog instead.
        ShowVcsDecorations = current.Vcs?.ShowDecorations ?? true;
        ShowSelectionBoxes = views.ShowSelectionBoxes;
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
        StartInSpecificFolder = startup.ShowOnStartup == StartupLocation.SpecificFolder;
        StartupFolder = startup.StartupFolder ?? "";
        BeginInSplitView = startup.BeginInSplitView;
        ShowFilterBar = startup.ShowFilterBar;
        LocationBarEditable = startup.LocationBarEditable;
        ShowFullPathInTitleBar = startup.ShowFullPathInTitleBar;
    }

    // Three booleans rather than one enum property because Avalonia's
    // RadioButton binds IsChecked, and a converter per option would be more
    // moving parts than the thing it converts. Only the setters coordinate.

    [ObservableProperty] private bool _restoreLastSession;
    [ObservableProperty] private bool _startInHome;
    [ObservableProperty] private bool _startInSpecificFolder;

    [ObservableProperty] private string _startupFolder = "";
    [ObservableProperty] private bool _beginInSplitView;
    [ObservableProperty] private bool _showFilterBar;
    [ObservableProperty] private bool _locationBarEditable;
    [ObservableProperty] private bool _showFullPathInTitleBar;

    // ---- General ----------------------------------------------------------

    [ObservableProperty] private bool _naturalSorting;
    [ObservableProperty] private bool _caseSensitiveSorting;
    [ObservableProperty] private bool _rememberViewPerFolder;
    [ObservableProperty] private bool _showTooltips;
    [ObservableProperty] private bool _tabSwitchesSplitPanes;
    [ObservableProperty] private bool _closingSplitDiscardsOtherPane;
    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _showFreeSpace;

    /// <summary>
    /// Natural order compares case-insensitively by construction, so the case
    /// choice only means anything with it off. Disabled rather than hidden, so
    /// the relationship between the two is visible instead of mysterious.
    /// </summary>
    public bool CanSetCaseSensitivity => !NaturalSorting;

    partial void OnNaturalSortingChanged(bool value)
        => OnPropertyChanged(nameof(CanSetCaseSensitivity));

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

    // ---- View modes -------------------------------------------------------
    //
    // Three of the six. Icons.TextWidth, Icons.MaximumLines and
    // Compact.MaximumTextWidth stay out: they are structural metrics that would
    // have to feed PaneScale.Compute, and that pipeline is double-typed while
    // MaxLines is an int. Details.FolderSize's "size of contents" option needs
    // recursive summing in the metadata provider, which does not exist.

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

    /// <summary>
    /// Why a chosen folder was refused, shown under the row.
    ///
    /// **Said here rather than in a dialog.** The answer belongs beside the
    /// control that asked the question, and a second modal on top of a modal to
    /// report "that folder was not what I needed" is a lot of ceremony for a
    /// sentence.
    /// </summary>
    /// <summary>
    /// Where the Proton Drive sync folder is, for the link-sharing gestures. A
    /// typed path rather than a picker for v1: it is set once, and the person
    /// who moved their sync folder to D: knows exactly where it went.
    /// </summary>
    [ObservableProperty] private string _protonDriveFolder = "";

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
    /// reader.</summary>
    public sealed record IconThemeChoice(string Label, string Folder);

    public ObservableCollection<IconThemeChoice> IconThemeChoices { get; } = [];

    [ObservableProperty] private IconThemeChoice? _selectedIconTheme;

    /// <summary>
    /// Guards the two directions against each other: choosing from the list
    /// sets the folder, and setting the folder rebuilds the list and selects in
    /// it. Without this the two would take turns forever.
    /// </summary>
    private bool _syncingIconThemes;

    /// <summary>
    /// Rebuilds the list from what is actually on disk.
    ///
    /// **Read rather than remembered.** One download produces several themes
    /// and cannot say in advance how many; a folder deleted by hand would leave
    /// a remembered list offering something that is not there. Anything chosen
    /// by browsing is added on the end, so a theme kept somewhere else is still
    /// a row in the list rather than a reason for the list to disagree with the
    /// setting.
    /// </summary>
    public void RefreshIconThemes()
    {
        _syncingIconThemes = true;

        try
        {
            IconThemeChoices.Clear();
            IconThemeChoices.Add(new IconThemeChoice("Vaktari's own icons", ""));

            foreach (var installed in Core.FileSystem.IconThemeCatalogue.Installed())
                IconThemeChoices.Add(new IconThemeChoice(installed.Name, installed.Folder));

            if (IconThemeFolder.Length > 0 && !IconThemeChoices.Any(Chosen))
                IconThemeChoices.Add(new IconThemeChoice(IconThemeLabel + "  (chosen folder)", IconThemeFolder));

            SelectedIconTheme = IconThemeChoices.FirstOrDefault(Chosen) ?? IconThemeChoices[0];
        }
        finally
        {
            _syncingIconThemes = false;
        }

        bool Chosen(IconThemeChoice choice) => Core.FileSystem.PathRules.Same(choice.Folder, IconThemeFolder);
    }

    partial void OnSelectedIconThemeChanged(IconThemeChoice? value)
    {
        if (_syncingIconThemes || value is null) return;

        _syncingIconThemes = true;

        try
        {
            IconThemeFolder = value.Folder;
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
    /// **Closing is the point, not a shortcut.** Every control on these six
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

        SelectedTerminal =
            AvailableTerminals.FirstOrDefault(t => t.Id == _original.General.PreferredTerminal)
            ?? AvailableTerminals[0];
    }

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
    public string VersionLine => $"Vaktari {Program.Version}";

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
    /// **Applied immediately, not on Save**, and the label says so.
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
    [ObservableProperty] private bool _showFolderItemCounts;

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
    /// The folder box is only meaningful for one of the three choices, so it
    /// disables with the others rather than accepting input that will be
    /// ignored.
    /// </summary>
    public bool CanEditStartupFolder => StartInSpecificFolder;

    partial void OnStartInSpecificFolderChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditStartupFolder));

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
    /// Everything on the six pages, as the record that gets written.
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
        var location = StartInSpecificFolder ? StartupLocation.SpecificFolder
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
                RememberViewPerFolder = RememberViewPerFolder,
                ShowTooltips = ShowTooltips,
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
                ShowSelectionBoxes = ShowSelectionBoxes,
                ThemeMode = ThemeModeFromIndex(),

                Icons = _original.Views.Icons with { Spacing = Spacing(IconSpacing) },
                Compact = _original.Views.Compact with { Spacing = Spacing(CompactSpacing) },

                Details = _original.Views.Details with
                {
                    DateStyle = AbsoluteDates
                        ? Core.Settings.DateStyle.Absolute
                        : Core.Settings.DateStyle.Relative,

                    // Only two of the three modes are reachable from here, so
                    // the third is preserved rather than overwritten by a
                    // control that never showed it.
                    FolderSize = ShowFolderItemCounts
                        ? (_original.Views.Details.FolderSize == Core.Settings.FolderSizeMode.None
                            ? Core.Settings.FolderSizeMode.ItemCount
                            : _original.Views.Details.FolderSize)
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

    /// <summary>
    /// Puts every setting back to what a first run would have given.
    ///
    /// **There was no way back.** Nine sections on one page and five more
    /// pages, every one of them remembering what it was last set to, and the
    /// only route to the defaults was to close Vaktari, find settings.json —
    /// which nothing in the application could name until recently — delete it,
    /// and start again.
    ///
    /// **No confirmation, and that is not carelessness.** This dialog edits a
    /// copy and commits it whole on Save, so Cancel discards this exactly as it
    /// discards any other change: the defaults are on screen to be looked at,
    /// and nothing has reached disk. A confirmation would be asking permission
    /// for something the next button already undoes.
    ///
    /// The state seeded is a bare SettingsState, which also resets the pages
    /// this dialog has not built — Collect carries _original forward with
    /// `with`, so replacing it is what makes "every setting" true rather than
    /// "every setting you can see".
    /// </summary>
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
    /// behind it is browsing.
    /// </summary>
    private readonly int _rememberedViews;

    public bool HasRememberedViews => _rememberedViews > 0;

    public string RememberedViewsLabel => _rememberedViews == 1
        ? "One folder is remembered"
        : $"{_rememberedViews:N0} folders are remembered";

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

        SettingsFileStatus = _rememberedViews == 1
            ? "One remembered folder view will be forgotten when you press Save."
            : $"{_rememberedViews:N0} remembered folder views will be forgotten when you press Save.";
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        Seed(new SettingsState());

        SettingsFileStatus = "Defaults restored — nothing is saved until you press Save.";
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? CloseRequested;
}
