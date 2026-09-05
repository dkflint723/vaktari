using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;

namespace Vaktari.Ui.ViewModels;

/// <summary>One clickable ancestor in the breadcrumb bar.</summary>
public sealed record PathSegment(string Name, string FullPath, ICommand Open, bool IsLast)
{
    /// <summary>
    /// The platform's own separator, so the bar reads `C:\ Users \ flint`
    /// rather than mixing one convention with the other. It was a literal "/"
    /// in the markup, which on Windows drew a POSIX separator between two
    /// backslash paths.
    /// </summary>
    public static string Separator => Path.DirectorySeparatorChar.ToString();

    /// <summary>
    /// **A root already ends in a separator, so it must not be given another.**
    /// `LeafName` returns a root as itself — `C:\` or `/` — and the bar then
    /// appended its own, producing `C:\ \ Users` on Windows and `/ / home` on
    /// Linux. The doubling was there before the crumbs moved to `Ancestors`; it
    /// only became obvious on Windows, where the two glyphs differ.
    /// </summary>
    public bool ShowSeparator => !IsLast && !PathRules.IsRoot(FullPath);

    /// <summary>
    /// The stand-in for the ancestors there is no room to show.
    ///
    /// **A real crumb rather than something the panel draws**, because
    /// Avalonia seals Panel.Render — a panel paints its Background and nothing
    /// else. It is created for every path deep enough to need one and parked
    /// off-screen by <see cref="BreadcrumbPanel"/> whenever the whole path
    /// fits, so its presence costs nothing when it is not wanted.
    /// </summary>
    public bool IsEllipsis { get; init; }

    public static PathSegment Ellipsis(ICommand open) =>
        new("…", "", open, IsLast: false) { IsEllipsis = true };
}

public sealed partial class PaneViewModel : ObservableObject, IDisposable
{
    private const int FlushIntervalMs = 100;

    private readonly IFileSystemProvider _fs;
    private readonly IFileOperations? _ops;
    private readonly IApplicationLauncher? _launcher;
    private readonly IClipboardService? _clipboard;
    private readonly IScriptRunner? _scripts;
    private readonly ITemplateProvider? _templates;
    private readonly List<FileEntry> _all = new();
    private CancellationTokenSource? _filterDebounce;
    private IDisposable? _watcher;

    /// <summary>
    /// Incremented on every load. Watcher events capture it before going async
    /// and re-check it before touching the collections: an event that passes
    /// the IsLoading check, then gets delayed by an await, would otherwise land
    /// in the middle of a later listing and insert an entry the enumeration is
    /// about to add again.
    /// </summary>
    private int _generation;
    private readonly Stack<string> _back = new();
    private readonly Stack<string> _forward = new();
    private CancellationTokenSource? _cts;
    private bool _suppressReload;

    public PaneViewModel(
        IFileSystemProvider fs,
        IFileOperations? ops = null,
        IApplicationLauncher? launcher = null,
        IClipboardService? clipboard = null,
        IScriptRunner? scripts = null,
        ITemplateProvider? templates = null)
    {
        WatchSelections();

        // **The count and the empty state follow the rows now.** Both are
        // computed from Entries.Count, and nothing raised them when the WATCHER
        // changed the listing — only a navigation did. So a finished download
        // left "36 items" beside 37 rows, and a folder emptied by something
        // else went on saying it had files. Worse in the other direction: a
        // folder that was empty when you arrived kept "this folder is empty"
        // printed across the middle of it while real rows appeared underneath.
        Entries.CollectionChanged += (_, _) => NotifyListingState();

        _templates = templates;
        _fs = fs;
        _ops = ops;
        _launcher = launcher;
        _clipboard = clipboard;
        _scripts = scripts;

        RefreshScripts();
        RefreshTemplates();
    }






    public BulkObservableCollection<FileEntry> Entries { get; } = new();

    /// <summary>
    /// Each layout sees the entries only while it is the one on screen.
    ///
    /// Grid and compact use a WrapPanel, which Avalonia has no virtualizing
    /// form of, so every item they are given is realized — and all three lists
    /// stay alive when hidden. Binding all of them to Entries meant opening a
    /// large folder realized a container per file in TWO invisible layouts,
    /// which is exactly the cost the streaming enumerator exists to avoid.
    ///
    /// The inactive ones get an empty array: no items, no containers, no
    /// change notifications.
    /// </summary>
    private static readonly FileEntry[] NoEntries = [];

    /// <summary>
    /// The details rows: the folder's own, unless a folder in it has been
    /// opened in place.
    ///
    /// The second collection and the rule that chooses between them live in
    /// PaneViewModel.Expansion.cs. With nothing expanded this is <c>Entries</c>
    /// itself, so an ordinary listing keeps exactly the shape it had.
    /// </summary>
    public IEnumerable<FileEntry> DetailsEntries
        => View != ViewMode.Details ? NoEntries
         : _projected ? _rows
         : Entries;

    public IEnumerable<FileEntry> GridEntries
        => View == ViewMode.Grid ? Entries : NoEntries;

    public IEnumerable<FileEntry> CompactEntries
        => View == ViewMode.Compact ? Entries : NoEntries;

    /// <summary>
    /// Above this, the un-virtualized layouts are refused rather than allowed
    /// to hang the app.
    ///
    /// WrapPanel realizes a container per item and Avalonia has no virtualizing
    /// wrap panel, so switching to grid on a large folder freezes the process
    /// outright. Refusing is ugly; truncating the listing would be worse — a
    /// file manager that silently omits files is actively dangerous, and you
    /// would have no way to know it had.
    ///
    /// Details view is virtualized and always available, so nothing becomes
    /// unreachable.
    /// </summary>
    /// <summary>
    /// Per-folder view overrides. Null until the shell supplies one.
    /// </summary>
    public static IFolderViewStore? FolderViews { get; set; }


    /// <summary>
    /// Recently opened files and folders. A separate store from
    /// Static for the same reason as the other providers: panes are created by
    /// the shell, not injected here.
    /// </summary>
    public static IRecentStore? Recents { get; set; }

    /// <summary>
    /// The recent lists, when the setting says to keep them.
    ///
    /// **Nothing consulted a setting: every open went in.** Read here rather
    /// than at each of the five call sites, so a sixth cannot be added that
    /// forgets to ask.
    /// </summary>
    private static IRecentStore? Recording
        => Settings.AppSettings.Current.General.RememberRecent ? Recents : null;

    /// <summary>
    /// The trash, for the `vaktari:trash` listing and its restore/empty
    /// actions. Same static convention as the others.
    /// </summary>
    public static ITrashMaintenance? Trash { get; set; }

    /// <summary>
    /// Version-control decorations. Static like the other providers; null when
    /// the feature is off or the tool is missing, and every caller must treat
    /// that as "draw nothing", never as "everything is clean".
    /// </summary>
    public static Vaktari.Core.Vcs.IVersionControl? Vcs { get; set; }

    /// <summary>
    /// The desktop's own context menu. Static like the other providers; null
    /// where the desktop has no such thing, which is every desktop but Windows
    /// today, and the menu then offers no entry rather than an empty one.
    /// </summary>
    public static Vaktari.Core.FileSystem.IShellMenuProvider? ShellMenu { get; set; }

    /// <summary>Whether to offer the entry at all.</summary>
    public bool HasShellMenu => ShellMenu is not null && !IsTrashListing && !IsRecentListing;

    /// <summary>
    /// Mounting disk images, or null where this machine cannot. Static like the
    /// other providers on this type.
    /// </summary>
    public static Vaktari.Core.Places.IDiskImages? DiskImages { get; set; }

    /// <summary>
    /// Where the machine's drives come from, for the This PC listing. Static
    /// like the other providers here; the sidebar holds the same one, which is
    /// the point — two enumerations of the drives would eventually disagree.
    /// </summary>
    public static Vaktari.Core.Places.IPlacesProvider? Places { get; set; }

    /// <summary>
    /// The search backend, for the `vaktari:search:` listing. Static like the
    /// other providers here.
    ///
    /// **Null is an empty result, never a crash** — the same rule the rest of
    /// this block follows. A desktop with no index still lets you type a
    /// question; it just cannot answer it.
    /// </summary>
    public static Vaktari.Core.Search.ISearchProvider? Search { get; set; }

    /// <summary>
    /// Reads this platform's kind of shortcut, so opening one can follow it.
    /// Static like the other providers here; null where the desktop has no such
    /// indirection to read.
    /// </summary>
    public static Vaktari.Core.FileSystem.IShortcutMaker? Shortcuts { get; set; }

    /// <summary>
    /// Whether the selected file is an image this machine could mount, and is
    /// not mounted already.
    ///
    /// **The bin and Recent are excluded.** Both hold rows naming where a file
    /// USED to be, so mounting there would attach whatever occupies that path
    /// now.
    /// </summary>
    public bool CanMountSelection =>
        DiskImages is { IsAvailable: true } images
        && !IsTrashListing
        && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && images.CanMount(entry.FullPath)
        && images.MountOf(entry.FullPath) is null;

    /// <summary>The other half: an image this application has mounted, which
    /// can therefore be put away again.</summary>
    public bool CanUnmountSelection =>
        DiskImages is { IsAvailable: true } images
        && !IsTrashListing
        && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && images.MountOf(entry.FullPath) is not null;

    private Vaktari.Core.FileSystem.IShellMenu? _shellMenu;

    /// <summary>What the live menu was built for, so an open one is reused
    /// rather than rebuilt and a stale one is still replaced.</summary>
    private IReadOnlyList<string>? _shellPaths;

    private bool _shellBuilding;

    /// <summary>
    /// What the shell offered, ready to bind.
    ///
    /// **A mixed list, holding real Separator controls among the records.**
    /// Avalonia uses a Control in an ItemsSource as its own container, which is
    /// the only way a data-driven menu can draw a rule — and the shell's menu
    /// leans on rules heavily enough that dropping them turns twenty entries
    /// into an undifferentiated column.
    /// </summary>
    public ObservableCollection<object> ShellMenuItems { get; } = new() { Waiting() };

    /// <summary>
    /// The row that holds the submenu open before there is anything in it.
    ///
    /// **Avalonia will not open a submenu with no items, and draws no chevron
    /// for one** — so an empty ItemsSource makes "Windows menu" look like a
    /// plain command and never fire SubmenuOpened, which is the event that
    /// builds the thing. The placeholder is what makes lazy loading possible at
    /// all, not decoration.
    /// </summary>
    private static Vaktari.Core.FileSystem.ShellMenuEntry Waiting() =>
        new("Reading the shell…", -1, IsEnabled: false);

    /// <summary>Bumped per open, so a build that lands late can tell that it is
    /// no longer wanted.</summary>
    private int _shellGeneration;

    /// <summary>
    /// Builds the shell menu when its submenu opens, and not before.
    ///
    /// **Off the UI thread, and this is not an optimisation.** Building gives
    /// every shell extension on the machine a turn, any one of which can be
    /// slow or hang outright; waiting for that here would freeze the window for
    /// as long as it took, which is the exact failure this feature is shaped to
    /// avoid. The entries arrive when they arrive and the menu fills in.
    ///
    /// **And nothing waits on it anywhere**, which is what lets the build run
    /// for as long as the machine needs. The provider hands back a task rather
    /// than a menu, so no thread is blocked while a handler thinks and nothing
    /// has to decide when to stop believing it — the placeholder stays on
    /// screen and says the shell is being read. It used to be given four
    /// seconds and then answered as though the shell had offered nothing.
    ///
    /// Not quite every thread, and the difference is worth being honest about:
    /// an async method body runs on its caller up to the first incomplete
    /// await, so the apartment thread this hands off to is now CONSTRUCTED and
    /// started here, on the UI thread, where the deleted Task.Run used to put
    /// that on the pool. It is a Thread constructor and a Start; no handler
    /// code runs on this thread at any point.
    ///
    /// Lazily for a second reason: no ordinary right-click should pay for
    /// something that lives behind one more hover.
    /// </summary>
    public async Task OpenShellMenuAsync()
    {
        if (ShellMenu is not { } provider) return;

        // The selection, or the folder when the click was on empty space — the
        // same rule the rest of the menu follows.
        //
        // **Empty means a different QUESTION, not the same question about the
        // folder.** A click on nothing wants what the folder offers about
        // itself as a place; asking for the folder's own menu answers with what
        // its row in the parent listing offers, which acts on it from outside.
        // The shell keeps those as two separately bound menus and this used to
        // ask for the first one either way.
        var paths = SelectionPaths();
        var background = paths.Count == 0;

        if (background) paths = [CurrentPath];

        // **Built once for a given selection, and never rebuilt underneath
        // itself.** The caller guards its own event, but this is the property
        // that has to hold: rebuilding starts by clearing the collection the
        // menu is drawn from, so a second call while one is on screen makes the
        // open submenu disappear. Keyed on the paths rather than on a flag so
        // that a menu left behind by a close event that never arrived is still
        // replaced when the selection moves on.
        if (_shellPaths is { } built && built.SequenceEqual(paths, StringComparer.Ordinal)
            && (_shellMenu is not null || _shellBuilding))
            return;

        CloseShellMenu();

        _shellPaths = paths;
        _shellBuilding = true;

        var generation = _shellGeneration;

        var menu = await (background
            ? provider.BuildBackgroundAsync(paths[0])
            : provider.BuildAsync(paths)).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _shellBuilding = false;

            // Closed, or reopened, while the shell was thinking. Releasing it
            // here is what keeps a slow build from leaking the apartment thread
            // it was running on.
            if (generation != _shellGeneration)
            {
                menu?.Dispose();
                return;
            }

            _shellMenu = menu;

            var rows = Flatten(menu?.Entries ?? []).ToList();

            // Never empty: an empty ItemsSource closes the submenu out from
            // under the pointer, and "nothing" reads as a fault rather than as
            // an answer.
            //
            // **This row now means the shell answered, and answered nothing.**
            // It used to mean that as well as "the shell was still thinking
            // when a four-second timer went off", so a slow machine was told
            // there was nothing here. They are separate rows now: until these
            // rows go up, what is on screen is the placeholder CloseShellMenu
            // left, which says the shell is being read.
            if (rows.Count == 0)
                rows.Add(new Vaktari.Core.FileSystem.ShellMenuEntry(
                    "Nothing offered here", -1, IsEnabled: false));

            ShowShellRows(rows);
        });
    }

    /// <summary>
    /// Puts these rows on screen without the collection ever being empty on the
    /// way there.
    ///
    /// **A clear followed by adds is not a replacement — it is an emptying and
    /// then a replacement, and the gap between the two is a state that readers
    /// land in.** Both callers used to do exactly that, three lines under a
    /// comment promising "never empty", and both were measured doing it: with
    /// the clear-then-fill version,
    /// ShellMenuBindingTests.The_rows_are_never_empty_while_they_are_replaced
    /// records a notification raised with Count 0 on every close and on every
    /// rebuild. That gap is what the intermittent
    /// "Assert.Contains … Collection: []" in
    /// Nothing_offered_is_said_only_after_the_shell_has_answered was reading;
    /// the same gap is what Avalonia's own reader — the ItemsControl the
    /// submenu is drawn from, which handles the Reset synchronously — sees
    /// when it closes the submenu out from under the pointer.
    ///
    /// New rows in first, old rows out afterwards, so the count never touches
    /// zero. A menu's worth of rows is twenty or thirty at the outside, so the
    /// extra notifications cost nothing worth weighing against that.
    ///
    /// **Never called with nothing to show**, and there is no branch here for
    /// it: the close passes the placeholder, and the rebuild passes the
    /// "Nothing offered here" row when the shell gave it none. An empty list
    /// here would empty the collection, which is the thing being prevented.
    /// </summary>
    private void ShowShellRows(IReadOnlyList<object> rows)
    {
        foreach (var row in rows) ShellMenuItems.Add(row);

        while (ShellMenuItems.Count > rows.Count) ShellMenuItems.RemoveAt(0);
    }

    private static IEnumerable<object> Flatten(
        IReadOnlyList<Vaktari.Core.FileSystem.ShellMenuEntry> entries)
    {
        foreach (var entry in entries)
        {
            if (entry.IsSeparator) yield return new Avalonia.Controls.Separator();
            else yield return entry;
        }
    }

    /// <summary>
    /// Releases it, which ends the thread holding the handlers.
    ///
    /// **The ids are offsets into one live menu.** Releasing while the user is
    /// still looking would leave every row pointing at nothing; never releasing
    /// leaks an apartment thread per right-click. So this is tied to the
    /// context menu closing, and to opening the next one.
    ///
    /// Back to the placeholder rather than to empty, so the entry keeps its
    /// chevron and can be opened again.
    /// </summary>
    public void CloseShellMenu()
    {
        _shellGeneration++;

        ShowShellRows([Waiting()]);

        _shellMenu?.Dispose();
        _shellMenu = null;
        _shellPaths = null;
        _shellBuilding = false;
    }

    // ---- administrator ----------------------------------------------------

    /// <summary>
    /// Whether the right-click that opened this menu was held with Shift.
    ///
    /// **Behind a modifier because it is not an everyday action.** Elevating is
    /// how a person gets past a permission deliberately set against them, so it
    /// belongs where it is reachable and not where it is stumbled into —
    /// Explorer puts it behind the same gesture.
    ///
    /// Set by the window, which is the only thing that sees the press: a
    /// context menu opening carries no record of which keys were down.
    /// </summary>
    [ObservableProperty] private bool _adminRequested;

    partial void OnAdminRequestedChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowAdminEntries));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    /// <summary>Whether to show the section at all.</summary>
    public bool ShowAdminEntries =>
        AdminRequested && _launcher?.CanElevate == true && !IsTrashListing && !IsRecentListing;

    /// <summary>
    /// Whether "run as administrator" would mean anything for what is selected.
    ///
    /// **Which files those are is the launcher's question now, and a list of
    /// Windows file extensions used to answer it here.** That list is right on
    /// Windows — the runas verb on a .txt does nothing at all, no error, no
    /// elevation, no editor — and it is no answer whatever on a desktop where
    /// an executable usually has no extension. Sitting in the view model it
    /// answered for every platform, so when Linux gained pkexec this entry
    /// would still have been invisible there for want of a file it recognised.
    ///
    /// **No longer behind Shift.** Explorer shows "Run as administrator" for
    /// every executable on a plain right-click; only its EXTENDED verbs hide
    /// behind Shift. Copying the gate onto this entry meant an ordinary
    /// right-click on an .exe showed no elevation at all, and the person went
    /// hunting through submenus for something that looked buried — because it
    /// was. The admin TERMINAL keeps the Shift gate: that one is an extended
    /// verb by Explorer's own convention too.
    /// </summary>
    public bool CanRunSelectionAsAdministrator =>
        _launcher is { CanElevate: true } launcher
        && !IsTrashListing && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && launcher.CanElevateFile(entry.FullPath);

    /// <summary>
    /// Hands the selection to the system to start elevated. The consent dialog
    /// is the system's — Windows' own, or the one polkit puts up — and Vaktari
    /// itself stays unelevated whatever is chosen.
    /// </summary>
    [RelayCommand]
    public void RunAsAdministrator()
    {
        if (!CanRunSelectionAsAdministrator || _launcher is not { } launcher) return;

        // Every one that is actually runnable. Selecting three installers and
        // choosing this ran one of them — and elevation is the worst place for
        // "it did something, but not what you asked".
        //
        // **The full path, not the name.** The rule here used to be an
        // extension match, which either spelling satisfies; the launcher's rule
        // can be the file's own mode bits, and a bare name asks the question
        // about a path relative to wherever this process happens to be running.
        var runnable = EntriesToActOn()
            .Where(e => !e.IsDirectory && launcher.CanElevateFile(e.FullPath))
            .ToList();

        if (runnable.Count == 0 || TooMany(runnable.Count)) return;

        foreach (var entry in runnable) launcher.OpenElevated(entry.FullPath);
    }

    /// <summary>
    /// Mounts the selected disk image and shows what is inside it.
    ///
    /// **Navigating there is the point of the verb.** Explorer opens the drive
    /// it just mounted, and a Mount that left the person looking at the .iso
    /// they started from would make them go and find it. The arrival watcher
    /// puts the drive in the sidebar a moment later either way; this is the
    /// direct answer to a direct request.
    /// </summary>
    [RelayCommand]
    public async Task MountImageAsync()
    {
        if (!CanMountSelection || DiskImages is not { } images
            || SelectedEntry is not { } entry) return;

        var name = entry.Name;

        Status = $"mounting {name}…";

        try
        {
            var mounted = await images.MountAsync(entry.FullPath, CancellationToken.None)
                .ConfigureAwait(true);

            await NavigateAsync(mounted.MountPath).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // The tool's own sentence, which says whether the file is not an
            // image or the machine cannot mount one.
            Status = Vaktari.Core.FileSystem.Failures.Describe(ex, $"mount {name}");
        }

        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    /// <summary>Detaches an image this application mounted. The file itself is
    /// untouched.</summary>
    [RelayCommand]
    public async Task UnmountImageAsync()
    {
        if (!CanUnmountSelection || DiskImages is not { } images
            || SelectedEntry is not { } entry) return;

        var name = entry.Name;
        var mounted = images.MountOf(entry.FullPath);

        Status = $"unmounting {name}…";

        try
        {
            // Out of the way first, for the same reason ejecting a drive moves
            // the panes: a pane inside the mounted image holds it open.
            if (mounted is not null && IsInside(CurrentPath, mounted.MountPath))
                await NavigateAsync(
                    Path.GetDirectoryName(entry.FullPath)
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
                    .ConfigureAwait(true);

            await images.UnmountAsync(entry.FullPath, CancellationToken.None).ConfigureAwait(true);

            Status = $"unmounted {name}";
        }
        catch (Exception ex)
        {
            Status = Vaktari.Core.FileSystem.Failures.Describe(ex, $"unmount {name}");
        }

        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
    }

    private static bool IsInside(string? path, string root)
    {
        if (string.IsNullOrEmpty(path)) return false;

        if (Vaktari.Core.FileSystem.PathRules.Same(path, root)) return true;

        var prefix = Vaktari.Core.FileSystem.PathRules.Normalise(root);
        var full = Vaktari.Core.FileSystem.PathRules.Normalise(path);

        return full.StartsWith(prefix, Vaktari.Core.FileSystem.PathRules.Comparison)
               && (prefix.EndsWith(Path.DirectorySeparatorChar)
                   || (full.Length > prefix.Length
                       && full[prefix.Length] == Path.DirectorySeparatorChar));
    }

    /// <summary>An elevated terminal in this folder, in the preferred terminal.</summary>
    [RelayCommand]
    public void OpenAdminTerminalHere()
    {
        if (!ShowAdminEntries) return;

        _launcher?.OpenElevatedTerminal(CurrentPath, Terminals.FirstOrDefault());
    }

    /// <summary>Runs one of the shell's entries.</summary>
    [RelayCommand]
    public void InvokeShellEntry(Vaktari.Core.FileSystem.ShellMenuEntry? entry)
    {
        // A parent row exists to open its children; invoking it would ask the
        // handler to run a command it never issued.
        if (entry is null || entry.HasChildren || entry.IsSeparator) return;

        _shellMenu?.Invoke(entry.Id);
    }

    /// <summary>
    /// Status per entry of the CURRENT folder, or empty. Rebuilt per load and
    /// never merged across folders — a stale entry here would decorate the
    /// wrong file, which is worse than decorating nothing.
    /// </summary>
    public IReadOnlyDictionary<string, Vaktari.Core.Vcs.VcsState> VcsStates { get; private set; }
        = new Dictionary<string, Vaktari.Core.Vcs.VcsState>();

    /// <summary>
    /// True when this folder is inside a repository, which is what reserves the
    /// marker column.
    ///
    /// Gated rather than always-on for the same reason as the parent-path
    /// column: most folders are not repositories, and a permanently reserved
    /// strip of dead space before every filename would be a poor trade for
    /// avoiding one binding.
    /// </summary>
    [ObservableProperty] private bool _isRepository;

    /// <summary>
    /// Applied on arrival, before the listing is asked for, so the folder is
    /// enumerated and sorted once under its own rules rather than sorted twice.
    /// Silent when the preference is off or the folder has no opinion.
    /// </summary>
    private void ApplyFolderView(string path)
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews?.Read(path) is not { } view) return;

        View = view.View;
        Sort = view.Sort;
        SortDescending = view.SortDescending;
        GroupBy = view.GroupBy;

        // Zero means the folder expressed no opinion about scale, so the pane
        // keeps whatever it had — scale is an accessibility setting and a
        // folder must not be able to shrink someone's text.
        if (view.FontScale > 0) FontScale = view.FontScale;
        if (view.IconScale > 0) IconScale = view.IconScale;

        // A folder's opinion is about the pane, not about one layout.
        if (view.FontScale > 0 || view.IconScale > 0) SeedScales(FontScale, IconScale);
    }

    /// <summary>
    /// Records the current view against the current folder. Called when the
    /// user changes one of these, never on arrival — otherwise merely visiting
    /// a folder would give it an opinion it never had.
    /// </summary>
    public void RememberFolderView()
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews is null || string.IsNullOrEmpty(CurrentPath)) return;
        if (_restoringView) return;

        FolderViews.Write(CurrentPath, new FolderViewState
        {
            View = View,
            Sort = Sort,
            SortDescending = SortDescending,
            GroupBy = GroupBy,
            FontScale = FontScale,
            IconScale = IconScale,
        });
    }

    private bool _restoringView;




    /// <summary>
    /// Grid is virtualized now, so it has no limit. Measured 100,000 items in
    /// 46 ms with 48 containers realized, against 6,841 ms for 20,000 before —
    /// the cost no longer grows with the folder at all.
    /// </summary>
    public bool CanUseGrid => true;

    /// <summary>
    /// **The limit is gone — compact virtualizes too, 31 July 2026.**
    /// `VirtualizingWrapPanel` gained an `Orientation`, so the same panel the
    /// grid uses now fills columns instead of rows: the row arithmetic became
    /// lane arithmetic, and only the axes differ.
    ///
    /// The old comment here said the panel "cannot simply be swapped in", and
    /// that was true — it needed the orientation first. It has it.
    /// **This was the last layout that refused a folder for being too large.**
    /// </summary>
    public bool CanUseCompact => true;

    /// <summary>True when neither tile layout is refused. Both are unconditional
    /// now; kept because the drop-back check and the menu still ask.</summary>
    public bool CanUseTileLayouts => CanUseCompact;

    // `EffectiveTileLimit` and `VAKTARI_TILE_LIMIT` were here. The limit was a
    // stopgap for un-virtualized tile layouts and both of them virtualize now, so
    // an override that enforces nothing is worse than no override: it invites
    // someone to raise a ceiling that is not there. `VAKTARI_TILE_DEBUG=1` is
    // still the way to watch realization.

    private void NotifyLayoutEntries()
    {
        OnPropertyChanged(nameof(DetailsEntries));
        OnPropertyChanged(nameof(GridEntries));
        OnPropertyChanged(nameof(CompactEntries));
    }

    /// <summary>
    /// One selection collection PER LAYOUT, and the reason is not cosmetic.
    ///
    /// Details, grid and compact are three separate ListBoxes that all stay
    /// alive when hidden. Pointing their SelectedItems at a single shared
    /// collection made each one write its own idea of the selection into it, so
    /// clicking one row produced a union of whatever the other two still held —
    /// three different files selected from one click. Deduplicating does not
    /// help, because the entries genuinely differ.
    ///
    /// Separate collections mean a hidden list can only ever disturb its own.
    /// </summary>
    public ObservableCollection<FileEntry> DetailsSelection { get; } = new();
    public ObservableCollection<FileEntry> GridSelection { get; } = new();
    public ObservableCollection<FileEntry> CompactSelection { get; } = new();

    /// <summary>
    /// Subscribes to all three, not just the active one — a hidden list can
    /// still be told to sync, and the active one changes as the layout does.
    /// Without this nothing recomputed the selection count, which is why the
    /// status bar reported only the item total.
    /// </summary>
    private void WatchSelections()
    {
        DetailsSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        GridSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
        CompactSelection.CollectionChanged += (_, _) => NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(CanCreateShortcut));
        OnPropertyChanged(nameof(CanPurgeFromBin));
        OnPropertyChanged(nameof(CanRenameInBulk));
        OnPropertyChanged(nameof(HasDirectorySelected));
        OnPropertyChanged(nameof(HasAnyDirectorySelected));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
        OnPropertyChanged(nameof(CanCompressSelection));
        OnPropertyChanged(nameof(CanExtractSelection));

        // The heading's box is a function of the selection and nothing else, so
        // it belongs on the one notification every route to a selection change
        // already goes through.
        OnPropertyChanged(nameof(AllChosen));
    }

    /// <summary>The collection belonging to the layout currently on screen.</summary>
    public ObservableCollection<FileEntry> SelectedEntries => View switch
    {
        ViewMode.Grid => GridSelection,
        ViewMode.Compact => CompactSelection,
        _ => DetailsSelection,
    };

    /// <summary>What everything else should read. Never the raw collections.</summary>
    public IReadOnlyList<FileEntry> Selection => SelectedEntries.ToList();

    /// <summary>
    /// True when the right-click landed on something. One menu serves both a
    /// row and the empty space below it, so the entries that act on a selection
    /// hide there rather than sit enabled and do nothing — which is what they
    /// did: every one of them guards on the selection and returns quietly.
    ///
    /// Gating on the selection is safe because a right-click on a row selects
    /// that row first, so the entries are still there when you want them.
    /// </summary>
    public bool HasSelection => SelectedEntry is not null || SelectedEntries.Count > 0;

    /// <summary>Cut, Rename and Move to bin: needs a selection, and the bin
    /// listing is a view rather than a folder.</summary>
    public bool CanActOnSelection => HasSelection && !IsTrashListing;

    /// <summary>
    /// Whether to offer "Create shortcut".
    ///
    /// **Three conditions, and each hides a row that could only disappoint.**
    /// A platform with no idea of a shortcut leaves <see cref="Shortcuts"/>
    /// null — the same gate the right-drag menu already applies before it
    /// offers "Create shortcuts here". <see cref="IsRealFolder"/> because the
    /// shortcut is written INTO the listing being looked at, and in the bin,
    /// Recent, This PC and a search that destination is the literal string
    /// "vaktari:trash" and its kin. And something has to be selected, because
    /// the shortcut has to point at something.
    ///
    /// **This is narrower than <see cref="HasShellMenu"/>, which excludes only
    /// the bin and Recent** — so on Windows a search listing and This PC still
    /// offer the shell's own "Create shortcut", which writes beside the item
    /// and needs no folder on screen. That is the reason the verb was left in
    /// the hosted menu rather than filtered out of it as a native twin: doing
    /// so would have left those two listings with no "Create shortcut" in
    /// either menu.
    /// </summary>
    public bool CanCreateShortcut => Shortcuts is not null && IsRealFolder && HasSelection;

    /// <summary>
    /// **"Rename in bulk…" was offered for a single file.** It is the entry
    /// whose name says how many it is for, sitting directly under plain
    /// Rename, which is the one that handles that case -- so the menu asked a
    /// question that has an obvious answer and put the wrong route first for
    /// anyone who read it as "the thorough one". F2 already sends more than one
    /// row here on its own.
    /// </summary>
    public bool CanRenameInBulk => CanActOnSelection && SelectedEntries.Count > 1;

    /// <summary>
    /// Whether a FOLDER is selected, which is the only case where adding "the
    /// selection" to places differs from adding the current folder.
    ///
    /// **Two entries did the same thing.** Splitting "Add to places" into a
    /// selection one and a current-folder one read well until you noticed that
    /// the selection command falls back to the current folder for anything that
    /// is not a directory — so with nothing selected, or a file selected, both
    /// rows pinned the same path under two different labels, and the one naming
    /// a selection was not acting on it.
    /// </summary>
    public bool HasDirectorySelected => SelectedEntry is { IsDirectory: true };

    /// <summary>
    /// Whether a folder is anywhere in the selection, not just under the focus.
    ///
    /// **The row hid whenever the FOCUSED row was not one.** Click a file, then
    /// ctrl-click two folders, and "Open in new tab" — which now opens both —
    /// was not there to be clicked, because the focus was still on the file.
    /// HasDirectorySelected asks about one row and is right for the entries
    /// that act on one; this asks the question the verb actually answers.
    /// </summary>
    public bool HasAnyDirectorySelected
        => HasDirectorySelected || SelectedEntries.Any(e => e.IsDirectory);

    /// <summary>
    /// Whether the rows draw a tick box, and whether the column heading draws
    /// the one that ticks them all.
    ///
    /// **The listing had no pointer-only route to a multi-selection.** Every
    /// one of them went through a modifier — ctrl+click, shift+click — or
    /// through a rubber band, which is a drag.
    ///
    /// Read from the live settings rather than stored on the pane, so a save
    /// only has to raise the notification; see <see cref="RefreshSelectionBoxes"/>.
    /// Off by default, which is Explorer's answer rather than Dolphin's, and
    /// off means the boxes cost nothing at all: the markup collapses the slot
    /// rather than drawing it empty.
    /// </summary>
    public bool ShowSelectionBoxes => Settings.AppSettings.Current.Views.ShowSelectionBoxes;

    /// <summary>
    /// What the heading's box shows: all of the rows ticked, some of them, or
    /// none. Null is "some", which is the dash a three-state box draws.
    ///
    /// **An empty listing is NONE, not all.** "All of nothing" is true by
    /// arithmetic and reads, in a folder with no files in it, as a listing that
    /// has selected itself.
    /// </summary>
    public bool? AllChosen
    {
        get
        {
            if (SelectedEntries.Count == 0) return false;

            // **The FOLDER's rows, still, with a folder opened in place.**
            // Every select-everything gesture takes exactly these — see
            // MainWindow.SelectWholeFolder for why — so this is the count the
            // box's own click produces, and a selection larger than it can only
            // have been built by ctrl+clicking across the boundary on purpose.
            return SelectedEntries.Count >= Entries.Count ? true : null;
        }
    }

    /// <summary>
    /// Re-asks both selection-box questions after settings have been saved.
    ///
    /// Neither is stored, so neither raises anything on its own — a pane
    /// already on screen would keep the boxes it had until it was rebuilt.
    /// That is the trap the font setting fell into for weeks: saved, recorded,
    /// and invisible until the next launch.
    /// </summary>
    public void RefreshSelectionBoxes()
    {
        OnPropertyChanged(nameof(ShowSelectionBoxes));
        OnPropertyChanged(nameof(AllChosen));
    }

    /// <summary>
    /// Carries the selection to the layout being switched to, so changing view
    /// does not silently drop what you had chosen.
    /// </summary>
    private void CarrySelection(ViewMode from, ViewMode to)
    {
        if (from == to) return;

        var source = from switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var target = to switch
        {
            ViewMode.Grid => GridSelection,
            ViewMode.Compact => CompactSelection,
            _ => DetailsSelection,
        };

        var carried = source.ToList();

        target.Clear();
        foreach (var entry in carried) target.Add(entry);

        OnPropertyChanged(nameof(SelectedEntries));
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>Applications offered by the "open with" submenu.</summary>
    public ObservableCollection<LaunchOption> OpenWithOptions { get; } = new();

    /// <summary>
    /// Whether "Open with" has anything to offer.
    ///
    /// **Gated on a count, like every neighbour.** It was gated on HasSelection
    /// alone, so every folder on both platforms — and on Linux any file whose
    /// MIME will not resolve — drew a row with a chevron and an empty popup
    /// behind it. HasScripts, HasTemplates and HasSeveralTerminals all guard on
    /// a count for exactly this reason.
    /// </summary>
    public bool HasOpenWithOptions => OpenWithOptions.Count > 0;

    /// <summary>Raised when an operation starts, so the shell can show progress.</summary>
    public event EventHandler<IOperationHandle>? OperationStarted;

    /// <summary>Raised when a rename is requested, so the view can prompt.</summary>
    public event EventHandler<FileEntry>? RenameRequested;

    /// <summary>
    /// Raised when somebody asks for an application that is not in the list and
    /// the platform has no chooser dialog of its own, so the view has to draw
    /// one.
    ///
    /// An event rather than a window opened from here, like every other dialog
    /// this view model asks for: a view model that constructs a Window cannot
    /// be tested without one, and the pane is built headless in several dozen
    /// tests that have no owner to be modal to.
    /// </summary>
    public event EventHandler<ChooseApplicationViewModel>? ChooseApplicationRequested;

    [ObservableProperty] private string _currentPath = "";
    [ObservableProperty] private string _pathText = "";
    [ObservableProperty] private string _title = "…";
    [ObservableProperty] private string _status = "";

    /// <summary>Free space on the filesystem holding this folder — Dolphin
    /// keeps it in the status bar and it is genuinely useful there.</summary>
    [ObservableProperty] private string _freeSpace = "";

    /// <summary>
    /// Off the UI thread: DriveInfo stats the filesystem, and on an unreachable
    /// NFS or SMB mount that blocks for the mount timeout — which would freeze
    /// the window on every navigation into it.
    /// </summary>
    private async Task RefreshFreeSpaceAsync(string path)
    {
        string text;

        try
        {
            text = await Task.Run(() =>
            {
                var drive = new DriveInfo(path);
                return $"{ByteSize.Format(drive.AvailableFreeSpace)} free";
            }).ConfigureAwait(false);
        }
        catch
        {
            text = "";
        }

        // Discard if we have navigated on since.
        if (CurrentPath != path) return;

        await Dispatcher.UIThread.InvokeAsync(() => FreeSpace = text);
    }

    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isLoaded;
    [ObservableProperty] private bool _showHidden;
    [ObservableProperty] private FileEntry? _selectedEntry;
    [ObservableProperty] private string _filterText = "";
    [ObservableProperty] private bool _isFilterVisible;
    // **These three were the literals `SortField.Name`, `false` and
    // `ViewMode.Details`, with no key in settings.json able to say otherwise.**
    // So every fresh pane opened in Details sorted by name: the tab strip's
    // "+", a new window, the second half of a split, and the very first tab of
    // a fresh install.
    //
    // Read from the live preferences at CONSTRUCTION rather than in the
    // setters, so the two things that overwrite them a moment later —
    // RestoreFrom for a session tab and AdoptViewOf for a tab opened `like`
    // another — still win, and the default never reaches a listing in those
    // cases. Both run before the pane has navigated anywhere.
    [ObservableProperty] private SortField _sort = Settings.AppSettings.Current.Views.DefaultSort;
    [ObservableProperty] private bool _sortDescending = Settings.AppSettings.Current.Views.DefaultSortDescending;
    [ObservableProperty] private ViewMode _view = Settings.AppSettings.Current.Views.DefaultView;

    /// <summary>Highlights the pane a drop would land in.</summary>
    [ObservableProperty] private bool _isDropTarget;

    /// <summary>
    /// The folder row a drop would land IN, as opposed to the pane it is over.
    ///
    /// **The outline round the pane was never the question.** Which pane the
    /// pointer is in is obvious; what you cannot see is whether releasing puts
    /// the files into the folder under the pointer or into the folder being
    /// listed — two different places, one of them a mistake you then have to
    /// find and undo. Empty means the drop lands in the current folder.
    /// </summary>
    [ObservableProperty] private string _dropTargetPath = "";

    // ---- dynamic columns -----------------------------------------------

    /// <summary>
    /// Set by the view as the pane resizes. Columns drop out in priority order
    /// as space runs out rather than being squeezed or clipped — which is what
    /// makes a narrow split pane still readable.
    /// </summary>
    [ObservableProperty] private double _viewportWidth = 1000;

    /// <summary>
    /// Set from the window's UI scale. Column content grows with the type
    /// scale, so the widths at which columns stop fitting have to grow with it
    /// too — fixed thresholds meant that at 2x every column still claimed to
    /// fit while overflowing the pane.
    /// </summary>
    /// <summary>
    /// The text scale, pushed in by the shell. Column thresholds are about how
    /// much room *text* needs, so they follow the font axis — not the icon one.
    /// This was left orphaned by the font/icon split and nothing assigned it,
    /// so the thresholds silently stopped following the text size.
    /// </summary>
    [ObservableProperty] private double _textScale = 1.0;

    /// <summary>
    /// Type and icon scale for THIS pane. Per tab and per split side, because a
    /// reference listing beside a working one wants different sizes — which is
    /// the whole reason for having two panes.
    /// </summary>
    [ObservableProperty] private double _fontScale = 1.0;
    [ObservableProperty] private double _iconScale = 1.0;

    /// <summary>
    /// Scale per LAYOUT, not per pane.
    ///
    /// The three modes want genuinely different proportions — a grid tile and a
    /// details row are not the same object at different zooms — so one shared
    /// pair meant enlarging the grid also enlarged the details rows, and the
    /// size readout showed a number that did not describe what was on screen.
    ///
    /// `FontScale` and `IconScale` remain the ACTIVE pair, because everything
    /// downstream reads them: the metric pipeline, the column thresholds, the
    /// typed-size flyout. This dictionary is only what the inactive modes are
    /// holding while they wait.
    /// </summary>
    private readonly Dictionary<ViewMode, (double Font, double Icon)> _scales = new()
    {
        [ViewMode.Details] = (1.0, 1.0),
        [ViewMode.Grid]    = (1.0, 1.0),
        [ViewMode.Compact] = (1.0, 1.0),
    };

    /// <summary>
    /// True only while a mode switch is loading the incoming mode's pair.
    /// Without it the assignment would immediately record itself back into the
    /// slot it just came from, and every mode would converge on one value again
    /// — the bug this exists to fix, reintroduced by its own fix.
    /// </summary>
    private bool _swappingScales;

    /// <summary>
    /// Gives every mode the same starting pair. Used when a session or a folder
    /// supplies one scale: it expressed an opinion about the pane, not about a
    /// particular layout, so no mode should be left at a stale 1.0.
    /// </summary>
    private void SeedScales(double font, double icon)
    {
        foreach (var mode in _scales.Keys.ToList()) _scales[mode] = (font, icon);
    }

    /// <summary>
    /// The bases the scales multiply. Exposed as real sizes rather than as a
    /// multiplier, because "14" is something a person can reason about and
    /// "1.15" is not.
    /// </summary>
    private const double BaseFontSize = 14;
    /// <summary>
    /// **The icon size box quoted 26, and nothing on screen was 26 pixels.**
    /// This was a private copy of what ThumbSize used to be; the
    /// design-reference pass took the details row icon to 18 and left the copy
    /// behind, so the box read 26 beside an 18px icon — and in Grid and
    /// Compact, where the icons are 72 and 36, it read 26 there as well. A
    /// number no layout had drawn since.
    ///
    /// Per LAYOUT, because the scale it multiplies is per layout: the flyout
    /// edits the ACTIVE mode's IconScale, so the active mode's icon is the only
    /// size the number beside it can honestly be.
    /// </summary>
    private double BaseIconSize => PaneScale.BaseIcon(View);

    private const double MinScale = 0.7;
    private const double MaxScale = 2.5;

    /// <summary>
    /// What "share" would act on: the selected folder, or this one. Shown in
    /// the menu so the target is visible before clicking rather than inferred
    /// from the result afterwards.
    /// </summary>
    public string ShareTargetLabel
    {
        get
        {
            var name = SelectedEntry is { IsDirectory: true } selected
                ? selected.Name
                : PathRules.LeafName(CurrentPath);

            return string.IsNullOrEmpty(name) ? "this folder" : name;
        }
    }

    public double FontPoints
    {
        get => Math.Round(FontScale * BaseFontSize);
        set => FontScale = Math.Clamp(value / BaseFontSize, MinScale, MaxScale);
    }

    public double IconPixels
    {
        get => Math.Round(IconScale * BaseIconSize);
        set => IconScale = Math.Clamp(value / BaseIconSize, MinScale, MaxScale);
    }

    partial void OnFontScaleChanged(double value)
    {
        if (!_swappingScales) _scales[View] = (value, IconScale);

        OnPropertyChanged(nameof(FontPoints));
        // Column thresholds are measured in text width, so they follow the font
        // axis of the pane they belong to.
        TextScale = value;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnIconScaleChanged(double value)
    {
        if (!_swappingScales) _scales[View] = (FontScale, value);

        OnPropertyChanged(nameof(IconPixels));

        // The nesting step a folder opened in place indents by is derived from
        // the row icon, so it moves with this. Republished from the stored
        // depths rather than re-spliced: a rebuild of the row collection is a
        // Reset over every row on screen, and a Ctrl+scroll sends one of these
        // per wheel tick.
        if (_depths.Count > 0) PublishIndents();

        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised so the shell can persist the change.</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>
    /// Forces every pane-level metric to be recomputed and rewritten.
    ///
    /// Needed because the metrics now mix two sources — this pane's scale and
    /// the global spacing settings — and only the first of those raises a
    /// property change. Without this a spacing change would reach only the panes
    /// that happened to rescale afterwards.
    /// </summary>
    public void RefreshScale() => OnPropertyChanged(nameof(IconScale));

    /// <summary>
    /// Starts this pane looking like another one.
    ///
    /// **A new tab used to start from nothing** — hidden files off, Details,
    /// sorted by name, ungrouped, at 100% — so opening one while working with
    /// hidden files showing meant setting it all up again. Both references
    /// carry the current view across.
    ///
    /// Under the reload guard, because these are set before the pane has
    /// navigated anywhere and each setter would otherwise ask for a listing
    /// that has no path yet.
    /// </summary>
    public void AdoptViewOf(PaneViewModel other)
    {
        _suppressReload = true;

        try
        {
            ShowHidden = other.ShowHidden;
            View = other.View;
            Sort = other.Sort;
            SortDescending = other.SortDescending;
            GroupBy = other.GroupBy;
            FontScale = other.FontScale;
            IconScale = other.IconScale;

            HideSizeColumn = other.HideSizeColumn;
            HideModifiedColumn = other.HideModifiedColumn;
            ShowTypeColumn = other.ShowTypeColumn;
            ShowCreatedColumn = other.ShowCreatedColumn;
        }
        finally
        {
            _suppressReload = false;
        }
    }

    // ---- which columns this pane shows ------------------------------------
    //
    // **Per pane, the way sort and grouping are.** A reference listing beside
    // a working one wants different columns, and a choice made on one side of
    // a split must not move the other. Persisted with the tab, restored with
    // it, and phrased so that false is what the pane showed before there was a
    // chooser — an absent session key arrives as default(T).

    [ObservableProperty] private bool _hideSizeColumn;
    [ObservableProperty] private bool _hideModifiedColumn;
    [ObservableProperty] private bool _showTypeColumn;
    [ObservableProperty] private bool _showCreatedColumn;

    partial void OnHideSizeColumnChanged(bool value) => NotifyColumns();
    partial void OnHideModifiedColumnChanged(bool value) => NotifyColumns();
    partial void OnShowTypeColumnChanged(bool value) => NotifyColumns();
    partial void OnShowCreatedColumnChanged(bool value) => NotifyColumns();

    // The ticks in the chooser. OneWay from these, with the click going through
    // the commands below — the same shape as every other tick in the menus.
    public bool IsSizeColumnShown => !HideSizeColumn;
    public bool IsModifiedColumnShown => !HideModifiedColumn;
    public bool IsTypeColumnShown => ShowTypeColumn;
    public bool IsCreatedColumnShown => ShowCreatedColumn;

    [RelayCommand] private void ToggleSizeColumn() => HideSizeColumn = !HideSizeColumn;
    [RelayCommand] private void ToggleModifiedColumn() => HideModifiedColumn = !HideModifiedColumn;
    [RelayCommand] private void ToggleTypeColumn() => ShowTypeColumn = !ShowTypeColumn;
    [RelayCommand] private void ToggleCreatedColumn() => ShowCreatedColumn = !ShowCreatedColumn;

    // **Two questions, both of which have to say yes.** The width rule was here
    // first and stays: a column that no longer fits is dropped whatever the
    // chooser says, because a chosen column crushing the name is worse than an
    // absent one. The choice is ANDed on top rather than replacing it.
    public bool ShowSize => !HideSizeColumn && ViewportWidth >= 340 * TextScale;

    public bool ShowModified => !HideModifiedColumn && ViewportWidth >= 520 * TextScale;

    /// <summary>
    /// The type column, off until it is asked for.
    ///
    /// **Its own width threshold, and a deliberately generous one.** It sits
    /// between the name and the size, so every pixel it takes comes out of the
    /// name — the only column that stretches. Below this width the name is
    /// already trimming and there is nothing left to give.
    /// </summary>
    public bool ShowType => ShowTypeColumn && ViewportWidth >= 620 * TextScale;

    /// <summary>
    /// When the file was made, off until it is asked for — the same shape as
    /// the type column, and off for the same reason: most listings are read by
    /// modified date and a second date column beside it is noise until somebody
    /// wants it.
    ///
    /// **The highest of the four thresholds the chooser can be held to (340,
    /// 520, 620, this), because it is the last column in the row.** It is the
    /// type column's 620 plus its own 150 (ColCreated at scale 1), which is the
    /// width a pane needs before this one can appear without taking back what
    /// that arithmetic already granted the columns to its left.
    /// </summary>
    public bool ShowCreated => ShowCreatedColumn && ViewportWidth >= 770 * TextScale;
    public bool ShowPermissions => ViewportWidth >= 680 * TextScale;
    public bool ShowMetadata =>
        ViewportWidth >= 840 * TextScale && !IsRecentListing && !IsTrashListing;

    /// <summary>
    /// True only in the two recent listings, where the rows come from a store
    /// rather than a directory.
    /// </summary>
    /// <summary>
    /// The path as a person should see it.
    ///
    /// **A virtual listing's path is an internal scheme**, and showing it is a
    /// leak: hovering the location bar in This PC read "vaktari:computer",
    /// which is a name for the code's benefit and nobody else's.
    /// </summary>
    public string DisplayPath => VirtualPaths.IsVirtual(CurrentPath)
        ? VirtualPaths.Label(CurrentPath)
        : CurrentPath;

    public bool IsRecentListing => VirtualPaths.IsRecent(CurrentPath);

    /// <summary>True in the trash listing, which gates restore and empty.</summary>
    public bool IsTrashListing => CurrentPath == VirtualPaths.Trash;

    /// <summary>
    /// True while this pane is showing a search, which is what puts the band
    /// above the listing.
    /// </summary>
    public bool IsSearchListing => VirtualPaths.IsSearch(CurrentPath);

    /// <summary>
    /// Whether "where does this row actually live" is a question this listing
    /// can answer.
    ///
    /// **Recent asked it and offered no answer.** A search and both Recent
    /// listings gather rows from the whole machine, and all three show the
    /// parent-path column for that one reason — a bare `config.toml` says
    /// nothing about which of four it is — but only a search carried the row
    /// that takes you there. Recent's only addition to the menu was Forget,
    /// which made "stop showing me this" easier to reach than "show me this".
    ///
    /// The bin is deliberately not included: a bin row's <c>FullPath</c> is the
    /// ORIGINAL path the file occupied before it was deleted, so going there
    /// lands on a folder that does not contain the row and may well contain
    /// something else wearing its name.
    /// </summary>
    public bool CanGoToLocation => IsSearchListing || IsRecentListing;

    /// <summary>What was asked, drawn in the band and in the empty state.</summary>
    public string SearchQueryText => VirtualPaths.QueryOf(CurrentPath);

    /// <summary>
    /// Whether "this folder only" has a folder to mean.
    ///
    /// **"This folder only" over This PC searched for a folder called
    /// "vaktari:computer".** A search started somewhere that is not a folder
    /// has no origin to scope to, so the box is disabled rather than ticked and
    /// quietly ignored.
    /// </summary>
    public bool CanScopeSearch =>
        VirtualPaths.OriginOf(CurrentPath) is { } origin && !VirtualPaths.IsVirtual(origin);

    /// <summary>
    /// The scope, as a place rather than a flag.
    ///
    /// **Ticking it navigates**, which is the whole difference from the popup's
    /// version: the scope is part of where you are, so changing it is going
    /// somewhere else and Back returns to the answer you had. The getter reads
    /// the path, so nothing has to keep the two in step.
    /// </summary>
    public bool SearchScopedHere
    {
        get => VirtualPaths.IsScoped(CurrentPath);
        set
        {
            // The navigation below raises this property again as the path
            // lands; without the guard that write starts a second navigation.
            if (!IsSearchListing || value == SearchScopedHere) return;

            // The case flag is carried, not defaulted: narrowing a search you
            // had asked to match capitals must not quietly widen it back.
            _ = NavigateAsync(VirtualPaths.Search(
                SearchQueryText, VirtualPaths.OriginOf(CurrentPath), value, SearchMatchesCase));
        }
    }

    /// <summary>
    /// Whether this backend can be asked to mind the capitals, which is what
    /// decides whether the box is drawn at all.
    ///
    /// **A box that cannot be honoured is the bug this came from wearing a
    /// tick.** SearchQuery.CaseSensitive had two readers and no writer; a
    /// control offered over a backend that ignores it — an index answering its
    /// own way — would be the same silence one layer up. Drawn rather than
    /// disabled, because unlike the scope box there is nothing search-specific
    /// to explain: the answer is a property of the backend, not of where you
    /// are.
    ///
    /// **No change notification, and that is an ordering rather than a hope.**
    /// <see cref="Search"/> is assigned by <c>WindowServices.Create</c>, which
    /// the first MainWindow's constructor calls BEFORE it builds the
    /// ShellViewModel whose panes this band binds to and before it assigns
    /// DataContext — three statements of one constructor, in that order, and a
    /// binding is not read until its DataContext attaches. Every later window
    /// is handed those same services, so by then it is already assigned.
    /// </summary>
    public bool CanMatchCase => Search?.SupportsCaseSensitivity ?? false;

    /// <summary>
    /// Whether the capitals in the question are part of it.
    ///
    /// Shaped exactly like <see cref="SearchScopedHere"/> — read off the path,
    /// written by navigating — so asking the same words two ways is being in
    /// two places and Back returns to the previous answer rather than re-running
    /// it.
    /// </summary>
    public bool SearchMatchesCase
    {
        get => VirtualPaths.MatchesCase(CurrentPath);
        set
        {
            // The navigation below raises this property again as the path
            // lands; without the guard that write starts a second navigation.
            if (!IsSearchListing || value == SearchMatchesCase) return;

            _ = NavigateAsync(VirtualPaths.Search(
                SearchQueryText, VirtualPaths.OriginOf(CurrentPath), SearchScopedHere, value));
        }
    }

    /// <summary>
    /// The box's own words, which carry the truth when it is disabled — a box
    /// still reading "This folder only" while being ignored claims a scope the
    /// search does not have.
    /// </summary>
    public string SearchScopeLabel
    {
        get
        {
            var origin = VirtualPaths.OriginOf(CurrentPath);

            // **"Everywhere" was not true on either platform.** Windows walked
            // the fixed drives, so the stick just plugged in was skipped;
            // Linux walked the home folder alone, so every other disk was. The
            // provider says what it actually covers now, because the provider
            // is what decides the roots — and it still says "everywhere" when
            // there is no provider, which is the one case where nothing is
            // being covered and no narrower claim would be truer.
            var reach = Search?.Everywhere ?? "everywhere";

            if (origin is null) return $"searching {reach}";

            return VirtualPaths.IsVirtual(origin)
                ? $"{VirtualPaths.Label(origin)} is not a folder — searching {reach}"
                : $"Only in {System.IO.Path.GetFileName(origin.TrimEnd(
                    System.IO.Path.DirectorySeparatorChar,
                    System.IO.Path.AltDirectorySeparatorChar))}";
        }
    }

    /// <summary>
    /// What is being typed into the search field, which is NOT what is being
    /// searched for.
    ///
    /// **The two used to be one string**, so every keystroke was a query: typing
    /// "claude" launched six walks, each cancelled by the next but only after it
    /// had begun reading directories. A draft that becomes a search on Enter
    /// needs no debounce, no minimum length and no cancellation — the three
    /// things the live version spent its complexity on.
    /// </summary>
    [ObservableProperty] private string _searchDraft = "";

    /// <summary>Whether the field has anything in it, which is what hides the
    /// "Ctrl+F" hint sitting at its right-hand end.</summary>
    public bool HasSearchDraft => SearchDraft.Length > 0;

    partial void OnSearchDraftChanged(string value) => OnPropertyChanged(nameof(HasSearchDraft));

    /// <summary>
    /// Whether the path bar shows the search FIELD or just its icon. The field
    /// is 230px that never yielded, and on the active side of a split it plus
    /// the filter button took the whole bar — leaving the crumbs reading "C:".
    /// </summary>
    [ObservableProperty] private bool _isSearchOpen;

    /// <summary>
    /// A one-shot trigger for the focus behaviour, not a state: set false then
    /// true to re-fire it, because after the first Ctrl+F it is true for ever
    /// and a second one would otherwise leave the caret where it was.
    /// </summary>
    [ObservableProperty] private bool _isSearchFocused;

    /// <summary>
    /// Ctrl+F: open the field and put the caret in it.
    ///
    /// Seeded from the search you are already looking at, so refining a
    /// question means editing it rather than retyping it — and abandoning the
    /// edit leaves the results you had, because they are a listing rather than
    /// a popup keyed on the box's contents.
    /// </summary>
    [RelayCommand]
    private void BeginSearch()
    {
        SearchDraft = IsSearchListing ? SearchQueryText : "";

        IsSearchOpen = true;

        IsSearchFocused = false;
        IsSearchFocused = true;
    }

    /// <summary>
    /// Enter: go to the results.
    ///
    /// **Enter used to do nothing at all**, so type-then-Enter — the reflex in
    /// both Explorer and Dolphin — dead-ended, and a result could only be
    /// reached with the mouse.
    ///
    /// The scope carries: retyping over a search you have already narrowed
    /// keeps it narrowed, and the origin stays the folder it started from
    /// rather than becoming the search path itself.
    /// </summary>
    [RelayCommand]
    private void RunSearch()
    {
        var text = SearchDraft.Trim();

        // Nothing typed is not a question. Whitespace is the same nothing: a
        // path built from it looks like a real search and asks the index to
        // match every file on the machine.
        if (text.Length == 0) return;

        var origin = IsSearchListing ? VirtualPaths.OriginOf(CurrentPath) : CurrentPath;

        // A fresh search means the folder you are standing in, the way
        // Explorer's box does. Asking for it from This PC is not refused here:
        // "a place that is not a folder cannot be the scope" is a fact about
        // the path, answered once by VirtualPaths.IsScoped so that this road
        // and a hand-edited session file get the same answer.
        var scoped = !IsSearchListing || SearchScopedHere;

        // The band above the results says what was asked, so the field has
        // nothing left to show and the crumbs can have their width back.
        IsSearchOpen = false;

        // Case carries the same way the scope does, and needs no IsSearchListing
        // clause of its own: MatchesCase answers false for a folder path, so a
        // search begun from a folder starts case-insensitive and one refined
        // from a search keeps what it was set to.
        _ = NavigateAsync(VirtualPaths.Search(text, origin, scoped, SearchMatchesCase));
    }

    /// <summary>
    /// Escape: put the field away.
    ///
    /// **It no longer takes the results with it**, which is the change that
    /// makes this safe. The popup was keyed on the box's contents, so clearing
    /// the box was the only way to close it and the only way out of a running
    /// walk — one gesture for three different intentions.
    /// </summary>
    [RelayCommand]
    private void DismissSearch()
    {
        SearchDraft = "";
        IsSearchFocused = false;
        IsSearchOpen = false;
    }

    /// <summary>
    /// Collapses the field back to its icon when you click away from it, but
    /// only when nothing is half-typed in it.
    /// </summary>
    [RelayCommand]
    private void CloseSearchIfEmpty()
    {
        if (SearchDraft.Length == 0) IsSearchOpen = false;
    }

    /// <summary>
    /// Go to where a result actually lives.
    ///
    /// **A search spans the whole filesystem, and a row is a filename.** The
    /// parent-path column answers "which of four config.toml is this"; this
    /// answers "take me there", which is the other half and the one Explorer
    /// calls Open file location. It is also all the popup could do with a
    /// result — choosing one WAS this — so a listing without it would have
    /// taken something away while adding everything else.
    /// </summary>
    [RelayCommand]
    private async Task GoToLocation()
    {
        if (SelectedEntry is not { } entry) return;

        // **In Recent Locations every row is a folder, and revealing a folder
        // ENTERS it** — which is what double-clicking the row already does, so
        // offered there unchanged this would have been a second name for Open
        // rather than an answer to "where is this". Shown in its parent with
        // the row lit instead, which is ShowAsync's whole distinction from
        // RevealAsync. A search keeps the other behaviour: a hit you asked to
        // be taken to is somewhere you want to BE.
        if (entry.IsDirectory && IsRecentListing)
        {
            // **A drive root has no parent directory, and GetParent says so on
            // both platforms — but for two different reasons, so neither is
            // worth leaning on.** The Windows provider returns
            // PathRules.Parent, which answers null for anything IsRoot accepts;
            // the Linux one returns Path.GetDirectoryName, which answers null
            // for `/`. Asking IsRoot here is what turns that null into an
            // ANSWER: the machine, which is where Up already goes from the top
            // of a drive. Recent Locations collects drive roots like any other
            // folder you visit, and without this line the entry would sit on
            // one doing nothing.
            var parent = PathRules.IsRoot(entry.FullPath)
                ? VirtualPaths.Computer
                : _fs.GetParent(entry.FullPath);

            if (!string.IsNullOrEmpty(parent)) await ShowAsync(parent, [entry.FullPath]);

            return;
        }

        await RevealAsync(entry);
    }

    /// <summary>Which index answered, so slow results are explained.</summary>
    public string SearchBackendLine =>
        Search is { IsAvailable: true } backend
            ? $"searching with {backend.BackendName}"
            : "searching by reading every folder — there is no index on this machine";

    /// <summary>
    /// Ends a running search where it stands, keeping the hits already found.
    ///
    /// **An unindexed walk is unbounded**, and the only other way out of one is
    /// to navigate away, which takes the results with it. Stop keeps them, and
    /// that is the whole difference between the two.
    ///
    /// The load's own cancellation path deliberately clears nothing, because it
    /// assumes a newer navigation is following and owns the state. Nothing
    /// follows this one, so it finishes the listing itself.
    /// </summary>
    [RelayCommand]
    private void StopSearch()
    {
        if (!IsSearchListing || !IsLoading) return;

        _cts?.Cancel();

        IsLoading = false;
        IsLoaded = true;

        Status = Entries.Count == 0
            ? "stopped"
            : $"stopped — {Entries.Count:N0} results so far";
    }

    /// <summary>
    /// How many matches this search may return before it gives up.
    ///
    /// Per pane rather than a constant, because <see cref="SearchMoreAsync"/>
    /// raises it: the cap exists to stop an unindexed walk running for ever,
    /// not to decide how many answers a person is allowed to have.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchLimitLine))]
    private int _searchLimit = SearchListing.Limit;

    /// <summary>
    /// **A search that ran out of budget looked exactly like one that ran out
    /// of tree.** Both end with the bar gone, the Stop gone and the listing
    /// settled; only one of them is an answer. This is the difference, and
    /// nothing carried it before — the walk broke out of its loop and returned.
    /// </summary>
    [ObservableProperty] private bool _searchHitLimit;

    /// <summary>What the band says when the walk was cut off.</summary>
    public string SearchLimitLine =>
        $"stopped after the first {SearchLimit:N0} matches — there are more";

    /// <summary>
    /// Honest about the cost. There is no cursor to resume from — the walk
    /// keeps no state between runs — so this is the same walk again with a
    /// bigger budget, not a continuation, and the hint says so rather than
    /// letting "Keep looking" imply otherwise.
    /// </summary>
    public string SearchMoreHint =>
        $"search again for another {SearchListing.Limit:N0} — the walk starts from the beginning";

    /// <summary>
    /// The way on from a truncated answer.
    ///
    /// **A message with no next step is a better-worded dead end.** The two
    /// things a person could already do about a cut-off search — narrow the
    /// words, tick "this folder only" — both throw away the answer in front of
    /// them. This keeps the question and raises the cap.
    ///
    /// Guarded as well as hidden: the button is bound to
    /// <see cref="SearchHitLimit"/>, but a command is reachable without its
    /// button, and re-reading every fixed drive to change nothing is an
    /// expensive way to do nothing.
    /// </summary>
    [RelayCommand]
    private async Task SearchMoreAsync()
    {
        if (!SearchHitLimit) return;

        SearchLimit += SearchListing.Limit;

        await RefreshAsync();
    }

    /// <summary>
    /// Whether this pane is looking at a real folder rather than one of the
    /// virtual listings.
    ///
    /// **Everything that needed a path on disk asked CurrentPath and was handed
    /// "vaktari:trash".** The listings that are views rather than folders — the
    /// bin, Recent, This PC — already gate the things that act on a SELECTION,
    /// but the things that act on the FOLDER ITSELF had no gate at all: Ctrl+D
    /// pinned a place whose path was the literal scheme, F4 opened a terminal
    /// in it, and Ctrl+L put it in the address bar to be read back as a path.
    /// Each of those is the same mistake, so they share one answer.
    /// </summary>
    public bool IsRealFolder => !VirtualPaths.IsVirtual(CurrentPath);

    /// <summary>
    /// The parent folder of each row, shown ONLY in a recent listing — and not
    /// optional there: those entries span the whole filesystem, so a bare
    /// filename says nothing about which of four `config.toml` files you are
    /// looking at.
    ///
    /// Shares column 2 with the metadata column rather than adding one of its own:
    /// the two are mutually exclusive by construction (ShowMetadata is false
    /// here), and inserting a column would renumber every element after it in
    /// two separate grids — the kind of edit that goes wrong quietly.
    /// </summary>
    public bool ShowParentPath =>
        (IsRecentListing || IsTrashListing || IsSearchListing)
        && ViewportWidth >= 420 * TextScale;

    partial void OnTextScaleChanged(double value) => NotifyColumns();

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(ShowModified));
        OnPropertyChanged(nameof(ShowType));
        OnPropertyChanged(nameof(ShowCreated));
        OnPropertyChanged(nameof(IsSizeColumnShown));
        OnPropertyChanged(nameof(IsModifiedColumnShown));
        OnPropertyChanged(nameof(IsTypeColumnShown));
        OnPropertyChanged(nameof(IsCreatedColumnShown));
        OnPropertyChanged(nameof(ShowPermissions));
        OnPropertyChanged(nameof(ShowMetadata));
        OnPropertyChanged(nameof(ShowParentPath));
    }

    partial void OnViewportWidthChanged(double value) => NotifyColumns();

    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewDetail = "";


    private CancellationTokenSource? _previewCts;







    /// <summary>An empty listing used to look identical to one still loading.</summary>
    /// <summary>
    /// The folder really has nothing in it — as distinct from a filter that
    /// matched nothing, which is a different sentence.
    /// </summary>
    public bool IsEmpty =>
        IsLoaded && !IsLoading && Entries.Count == 0 && !HasLoadError
        && string.IsNullOrWhiteSpace(FilterText);

    /// <summary>
    /// What an empty listing says.
    ///
    /// **"This folder is empty" was printed over the bin, over Recent and over
    /// This PC**, none of which is a folder. In the bin it is worse than
    /// clumsy: "this folder is empty" over an empty bin invites the reading
    /// that a folder somewhere has lost its contents, when what it means is
    /// that nothing has been deleted lately. Dolphin says "Trash is empty";
    /// Explorer never calls This PC a folder at all.
    /// </summary>
    public string EmptyText => CurrentPath switch
    {
        VirtualPaths.Trash    => $"{Vaktari.Core.Naming.TheBin} is empty",
        VirtualPaths.Computer => "no drives found",
        VirtualPaths.Files    => "no files opened lately",
        VirtualPaths.Locations => "no folders visited lately",

        // Above the catch-all, or a search that found nothing reports that a
        // folder is empty — about a folder nobody named.
        _ when IsSearchListing => $"nothing found for “{SearchQueryText}”",

        _ => "this folder is empty",
    };

    /// <summary>
    /// **"This folder is empty" over a folder full of files reads as data
    /// loss.** Typing a filter that matched nothing printed exactly that, and
    /// the way out — clear the filter — was the one thing the message gave no
    /// reason to try. Explorer says "No items match your search"; this says
    /// which filter, because the box may be somewhere the eye is not.
    /// </summary>
    public bool HasNoMatches =>
        IsLoaded && !IsLoading && Entries.Count == 0 && !HasLoadError
        && !string.IsNullOrWhiteSpace(FilterText);

    public string NoMatchesLine => $"nothing here matches \u201c{FilterText}\u201d";

    /// <summary>
    /// Why this folder could not be listed, shown in the listing itself.
    ///
    /// **A failed load used to draw nothing at all.** The catch set Status and
    /// stopped, and Status is a one-line message in the status bar that
    /// describes the ACTIVE pane — so a tab whose folder had been deleted
    /// showed column headings above an empty white space, indistinguishable
    /// from an empty folder and from one still loading, and in the inactive
    /// half of a split there was no message anywhere at all.
    ///
    /// Separate from IsEmpty rather than folded into it: "there is nothing
    /// here" and "this could not be read" are different facts, and telling
    /// somebody their folder is empty when it has been deleted is worse than
    /// saying nothing.
    /// </summary>
    [ObservableProperty] private string _loadError = "";

    public bool HasLoadError => LoadError.Length > 0;

    partial void OnLoadErrorChanged(string value)
    {
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Stable left-hand status: what is here and what is picked.</summary>
    /// <summary>
    /// Total size of the selection, so the status bar can report it the way
    /// Dolphin does. Directories contribute nothing — measuring them would mean
    /// walking the tree on every selection change.
    /// </summary>
    private string SelectionSize()
    {
        long total = 0;
        var files = 0;

        foreach (var entry in Selection)
        {
            if (entry.IsDirectory) continue;
            total += entry.Length;
            files++;
        }

        return files == 0 ? "" : ByteSize.Format(total);
    }

    /// <summary>
    /// What is here, and what is picked.
    ///
    /// **It said "items" and nothing else.** Both references split folders from
    /// files, and the split is the more useful half of the count: a folder of
    /// 200 items is a different place depending on whether it holds two
    /// subfolders or two hundred, and "items" cannot tell you which.
    ///
    /// The selection says how many of each too, but ONLY when both kinds are in
    /// it. "3 selected (3 files)" restates the number it just gave, and a
    /// status bar that repeats itself teaches people to stop reading it.
    /// </summary>
    public string Summary
    {
        get
        {
            var here = Count(Entries);

            if (Selection.Count == 0) return here;

            // One bracket for everything about the selection, not two in a
            // row: "2 selected (1 folder, 1 file) (10 B)" is the same facts
            // read twice as slowly.
            var about = new List<string>(2);

            if (Selection.Any(e => e.IsDirectory) && Selection.Any(e => !e.IsDirectory))
                about.Add(Count(Selection));

            if (SelectionSize() is { Length: > 0 } size) about.Add(size);

            var picked = about.Count > 0
                ? $"{Selection.Count:N0} selected ({string.Join(", ", about)})"
                : $"{Selection.Count:N0} selected";

            return $"{here} · {picked}";
        }
    }

    /// <summary>
    /// "5 folders, 12 files", leaving out whichever is none.
    ///
    /// A part that reads "0 folders" is noise in the one place on screen with
    /// no room for any — and the singular matters for the same reason the bin's
    /// own line does: "1 files" is the sort of thing that makes a person trust
    /// the rest of the number less.
    /// </summary>
    private static string Count(IEnumerable<FileEntry> entries)
    {
        var folders = 0;
        var files = 0;

        foreach (var entry in entries)
            if (entry.IsDirectory) folders++; else files++;

        var parts = new List<string>(2);

        if (folders > 0) parts.Add($"{folders:N0} folder{(folders == 1 ? "" : "s")}");
        if (files > 0) parts.Add($"{files:N0} file{(files == 1 ? "" : "s")}");

        return parts.Count > 0 ? string.Join(", ", parts) : "0 items";
    }

    private void NotifyListingState()
    {
        OnPropertyChanged(nameof(CanUseTileLayouts));
        OnPropertyChanged(nameof(CanUseGrid));
        OnPropertyChanged(nameof(CanUseCompact));

        // The drop-back to list view lived here: entering a folder past the
        // compact limit switched layout and said so. Both tile layouts virtualize
        // now, so there is nothing left to rescue anyone from.

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNoMatches));
        OnPropertyChanged(nameof(NoMatchesLine));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ShareTargetLabel));

        // **The heading's box depends on the listing as well as on the
        // selection.** It hung off NotifySelectionChanged alone, so a file
        // arriving in a folder where everything was ticked left the heading
        // still saying "all" after it had become "some" — and the watcher
        // adds rows without touching the selection.
        OnPropertyChanged(nameof(AllChosen));
    }

    public bool IsDetailsView => View == ViewMode.Details;
    public bool IsGridView => View == ViewMode.Grid;
    public bool IsCompactView => View == ViewMode.Compact;

    [RelayCommand]
    public void ShowAsDetails() => View = ViewMode.Details;

    [RelayCommand]
    public void ShowAsGrid() => TrySetTileLayout(ViewMode.Grid, "grid");

    /// <summary>
    /// Dolphin's third mode: names only, flowing down and wrapping into
    /// columns. The point is density — it fits several times as many entries on
    /// screen as either other layout, which is what you want when you are
    /// looking for a name rather than inspecting files.
    /// </summary>
    [RelayCommand]
    public void ShowAsCompact() => TrySetTileLayout(ViewMode.Compact, "compact");

    /// <summary>
    /// Refuses rather than hangs. The message names the real reason and the
    /// number, so it reads as a known limit and not a malfunction.
    /// </summary>
    private void TrySetTileLayout(ViewMode mode, string label)
    {
        // Kept as the single entry point for both tile layouts even though it no
        // longer refuses anything — `label` is now unused, and the method stays
        // only because a future layout might need to.
        _ = label;

        View = mode;
    }

    partial void OnViewChanged(ViewMode oldValue, ViewMode newValue)
    {
        // The outgoing mode keeps whatever it was left at, and the incoming one
        // restores its own. `_swappingScales` stops these two assignments being
        // recorded against the mode we are arriving in.
        _scales[oldValue] = (FontScale, IconScale);

        var (font, icon) = _scales[newValue];

        _swappingScales = true;
        try
        {
            // Assigned, not suppressed: the metric pipeline, the column
            // thresholds and the size readout all have to follow. Only the
            // bookkeeping above is skipped.
            FontScale = font;
            IconScale = icon;
        }
        finally
        {
            _swappingScales = false;
        }

        // **Two layouts sitting at the same scale switched silently.** The size
        // readout hangs off IconScale, and restoring an identical scale raises
        // nothing — so a Details-to-Grid switch at 100%, which is the default,
        // left "18" in the box beside 72px tiles. The base moved even though
        // the scale did not.
        OnPropertyChanged(nameof(IconPixels));

        // Timed because the un-virtualized layouts realize a container per
        // item, and how bad that is at a given count is the one number the
        // guard above should be set from.
        // Threshold 200, not 1,000: the measured cost is ~0.4 ms/item in grid
        // and ~0.8 in compact, so the interesting range — where realization is
        // still tolerable — sits BELOW a thousand. A 1,000-item floor measured
        // only the region that was already too slow.
        var realizeWatch = newValue != ViewMode.Details && Entries.Count > 200
            ? System.Diagnostics.Stopwatch.StartNew()
            : null;

        if (realizeWatch is not null)
            Dispatcher.UIThread.Post(() =>
            {
                realizeWatch.Stop();
                Console.Error.WriteLine(
                    $"[vaktari] tiles: {newValue} with {Entries.Count:N0} items "
                    + $"realized in {realizeWatch.ElapsedMilliseconds} ms");
            }, DispatcherPriority.Background);

        // **A folder opened in place is a details-only shape**, the way a
        // grouping is: grid and compact lay out fixed-size cells with room for
        // neither an indent nor a triangle. Ignored rather than cleared, so the
        // tree is still there when you come back — and rebuilt here, because
        // the sort and the watcher have gone on maintaining Entries while the
        // details listing was not the one on screen.
        //
        // Ahead of NotifyLayoutEntries for the same reason that call is ahead
        // of CarrySelection: the collection has to be right before the layout
        // is told to read it.
        if (_open.Count > 0) Reproject();

        // Populate the incoming layout FIRST. Its ListBox cannot hold a
        // selection for items it does not yet have, so carrying the selection
        // before the items exist would silently drop it.
        NotifyLayoutEntries();

        CarrySelection(oldValue, newValue);

        // **The band order survived the switch that hid the menu.** Ignoring the
        // grouping is only half of it: the rows were already sorted into bands
        // when the view changed and nothing re-sorted them, so the tiles came up
        // in date order with no headings — exactly the state hiding the menu was
        // meant to prevent — and the row that would have cleared it was not on
        // screen to click.
        //
        // After CarrySelection rather than before: the resort below keeps the
        // selection by reading the collection belonging to the layout on screen,
        // which is now the INCOMING one, and that holds nothing until
        // CarrySelection has filled it.
        //
        // Only when the switch crosses the details boundary, because that is the
        // only move that changes what EffectiveGroupBy answers — grid to compact
        // is ungrouped on both sides. Suppressed during a restore for the reason
        // NavigateAsync gives where it sets the flag: ApplyFolderView assigns
        // View, Sort and GroupBy against the PREVIOUS folder's rows, mid-load.
        if (!_suppressReload
            && GroupBy != GroupMode.None
            && (oldValue == ViewMode.Details) != (newValue == ViewMode.Details))
        {
            // The resort goes through the filter itself. This used to spell
            // that out here — `FilterText.Length > 0 ? ApplyFilter()` — because
            // ResortInPlace rebuilt Entries from the UNFILTERED master list,
            // and switching to tiles in a filtered, grouped folder showed the
            // whole folder again.
            ResortInPlace();
        }

        OnPropertyChanged(nameof(IsDetailsView));
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsCompactView));
        OnPropertyChanged(nameof(SelectedEntries));

        // The whole state in one line. "Status bar says 300 items but the pane
        // is empty" means Entries is populated and the bound layout is not —
        // which can only be View and the entries properties disagreeing, or a
        // ListBox still holding the empty array from when it was hidden.
        Console.Error.WriteLine(
            $"[vaktari] view: {oldValue}->{newValue} entries={Entries.Count:N0} "
            + $"details={DetailsEntries.Count():N0} grid={GridEntries.Count():N0} "
            + $"compact={CompactEntries.Count():N0} "
            + $"remember={Settings.AppSettings.Current.General.RememberViewPerFolder}");

        RememberFolderView();
    }

    [RelayCommand]
    public void ToggleView()
        => View = View == ViewMode.Details ? ViewMode.Grid : ViewMode.Details;

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;
    /// <summary>
    /// A virtual listing has no parent, and the Up button must be DISABLED
    /// there rather than merely inert.
    ///
    /// `GetParent` is `Path.GetDirectoryName`, which returns an EMPTY STRING —
    /// not null — for a path with no separator in it, so
    /// "vaktari:recent-files" reported a parent, enabled the button, and then
    /// did nothing when pressed because `NavigateAsync` rejects a blank path.
    /// An enabled control that does nothing is worse than a disabled one: it
    /// invites the user to conclude the application is broken.
    /// </summary>
    public bool CanGoUp => !string.IsNullOrEmpty(CurrentPath)
                           && !VirtualPaths.IsVirtual(CurrentPath)

                           // A drive root goes up to the machine. Without This
                           // PC there was nowhere above C:\, so Up was disabled
                           // at the top of every drive by construction.
                           && (PathRules.IsRoot(CurrentPath)
                               || !string.IsNullOrEmpty(_fs.GetParent(CurrentPath)));

        /// <summary>
    /// Whether a listing built with this hidden setting would hold that entry
    /// at all. Static and pure, so the decision can be read without a listing
    /// behind it.
    /// </summary>
    public static bool NeedsHiddenShown(FileEntry entry, bool showHidden)
        => !showHidden && entry.IsConcealed;

    /// <summary>The row on screen for a path, or null. Null rather than
    /// FirstOrDefault, because default(FileEntry) has a null FullPath and
    /// assigning THAT is a different kind of nothing.</summary>
    private FileEntry? RowFor(string? path)
    {
        foreach (var row in Entries)
            if (PathRules.Same(row.FullPath, path)) return row;

        return null;
    }

    /// <summary>
    /// Goes to where something lives and highlights it.
    ///
    /// **Selecting the search backend's own entry selected nothing.** FileEntry
    /// is a record struct with structural equality over all five members, the
    /// listings bind SelectedItem to SelectedEntry, and a ListBox resolves
    /// SelectedItem by equality against the rows it holds — so the hit had to
    /// match the row in size, timestamp and flags as well as path, and it
    /// routinely did not. The Linux search provider sets Directory and Hidden
    /// and nothing else, while the file system provider also sets Symlink and
    /// ReadOnly, so choosing any read-only file or symlink landed you in the
    /// right folder with nothing lit and the selection empty.
    ///
    /// **And a hidden hit had no row to select at all.** Both search backends
    /// return hidden and system files; the listing excludes them while
    /// ShowHidden is off. Turning it on is the only answer that shows what was
    /// asked for — landing on a folder that provably cannot contain the result
    /// is the worse surprise.
    /// </summary>
    public Task RevealAsync(FileEntry entry)
        => LandOnAsync(
            // A folder search result is a place you want to BE, so it is entered.
            entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath),
            [entry.FullPath],
            NeedsHiddenShown(entry, ShowHidden));

    /// <summary>
    /// Shows items where they live — including a FOLDER, which is selected in
    /// its parent rather than entered.
    ///
    /// **That one difference from <see cref="RevealAsync"/> is the whole reason
    /// this exists.** A search result you click is somewhere you want to go. An
    /// item another application asks the file manager to SHOW is something you
    /// want to look at, and entering it puts you inside the very folder you were
    /// being shown, with the folder itself off screen.
    ///
    /// **A list rather than a loop over one path**: every reveal navigates, so
    /// two files in one folder would load that folder twice, and the second
    /// selection would clear the first — "show these four downloads" would land
    /// with only the fourth lit.
    /// </summary>
    public async Task ShowAsync(string folder, IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;

        var unhide = false;

        foreach (var path in paths)
        {
            // The platform's own answer, so the hidden rule is the platform's
            // rather than this file guessing at leading dots — the system flag
            // counts too, and only the provider knows. Null when the item has
            // already gone; LandOnAsync then says so.
            if (await _fs.GetEntryAsync(path, CancellationToken.None).ConfigureAwait(true)
                is { } entry && NeedsHiddenShown(entry, ShowHidden))
            {
                unhide = true;
                break;
            }
        }

        await LandOnAsync(folder, paths, unhide).ConfigureAwait(true);
    }

    /// <summary>
    /// The half both reveals share: go there, then light the rows up.
    ///
    /// Shared rather than copied because the two hard-won parts are in here.
    /// Hidden files are turned on BEFORE the navigation, so the listing is built
    /// once already holding the rows instead of being rebuilt underneath the
    /// selection. And the rows selected are the ones the LISTING holds, not the
    /// ones the caller passed: FileEntry is a record struct with structural
    /// equality over all five members and the listings bind SelectedItem by
    /// equality, so a row that differs in size, timestamp or a single flag
    /// selects nothing at all.
    /// </summary>
    private async Task LandOnAsync(string? folder, IReadOnlyList<string> targets, bool unhide)
    {
        if (string.IsNullOrEmpty(folder)) return;

        if (unhide) ShowHidden = true;

        await NavigateAsync(folder).ConfigureAwait(true);

        var found = new List<string>();

        foreach (var target in targets)
            if (RowFor(target) is { FullPath: { } real }) found.Add(real);

        if (found.Count == 0)
        {
            Status = targets.Count == 1
                ? $"{PathRules.LeafName(targets[0])} is no longer there"
                : "those items are no longer there";

            return;
        }

        Reselect(found);
    }

    public async Task NavigateAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        // Already here, already loaded: do nothing at all.
        //
        // Reloading tore the listing down and rebuilt it — and because entries
        // paint in readdir order and only sort once enumeration finishes, the
        // rebuild flashed the same files in filesystem order before they
        // settled. Clicking a place you are already viewing looked like the
        // folder briefly changed. Refreshing on purpose is F5's job; a
        // navigation to where you already are is not a request to refresh.
        // VirtualPaths.SamePlace, not Ordinal: CurrentPath is normalised on
        // load, so a navigation spelled with a trailing separator or the other
        // case is the same place — compared ordinally it reloaded anyway AND
        // pushed a history entry whose Back went nowhere. It is PathRules.Same
        // for a folder and Ordinal for a search, because a search path carries
        // a question and the case box makes its capitals part of it.
        if (IsLoaded && !IsLoading && VirtualPaths.SamePlace(CurrentPath, path))
            return;

        if (!string.IsNullOrEmpty(CurrentPath) && !VirtualPaths.SamePlace(CurrentPath, path))
        {
            _back.Push(CurrentPath);
            _forward.Clear();
        }

        await LoadAsync(path).ConfigureAwait(false);

        // After the load, and only if it worked — a path that could not be read
        // is not somewhere the user goes, and counting it would push dead
        // folders up the list.
        // Recording the recent listing itself would be circular: it would put
        // "recent locations" at the top of recent locations.
        if (IsLoaded && !VirtualPaths.IsVirtual(path))
        {
            Recording?.Record(path, RecentKind.Folder);
        }
    }

    [RelayCommand]
    public async Task GoBackAsync()
    {
        if (!CanGoBack) return;
        _forward.Push(CurrentPath);
        await LoadAsync(_back.Pop()).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task GoForwardAsync()
    {
        if (!CanGoForward) return;
        _back.Push(CurrentPath);
        await LoadAsync(_forward.Pop()).ConfigureAwait(false);
    }

    [RelayCommand]
    public async Task GoUpAsync()
    {
        // Guarded here as well as in CanGoUp: the button's IsEnabled binds to
        // that property, but a keyboard shortcut reaches this command directly
        // and would bypass it.
        if (!CanGoUp) return;

        // The top of a drive is not the top of the machine.
        if (PathRules.IsRoot(CurrentPath))
        {
            await NavigateAsync(VirtualPaths.Computer).ConfigureAwait(false);
            return;
        }

        if (_fs.GetParent(CurrentPath) is { Length: > 0 } parent)
            await NavigateAsync(parent).ConfigureAwait(false);
    }

    /// <summary>
    /// Alt+Home, which had no command at all — the home folder appeared eight
    /// times in this codebase, every one of them as a fallback start path and
    /// none of them as somewhere the user could ask to go.
    /// </summary>
    [RelayCommand]
    public Task GoHomeAsync()
        => NavigateAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [RelayCommand]
    public Task OpenAsync(FileEntry entry)
    {
        // **The bin guard was on the keyboard route only, and the pointer
        // walked straight past it.** Enter and the Open menu row go through
        // OpenSelectedAsync, which refuses in the bin; a double-click goes
        // through MainWindow's TryOpen, which calls THIS method directly. So
        // double-clicking a bin row launched whatever now occupies the path the
        // item USED to occupy — trash notes.txt, write a new notes.txt,
        // double-click the row, and a different file opens with nothing to say
        // so. The same shape as the delete that took the wrong file.
        //
        // Above the directory branch, not below it, because a binned FOLDER is
        // the worse half: it does not reach the launcher at all, it navigates
        // the pane to a path that is either gone or belongs to something else
        // now, and arriving somewhere plausible is harder to notice than a file
        // opening.
        //
        // Here rather than in each caller because both routes that open a
        // SELECTION funnel through this method: OpenSelectedAsync opens every
        // entry through it, and TryOpen calls it directly. It does not cover
        // "Open with", which reaches the launcher on its own — that one
        // carries its own copy below.
        if (RefusedInBin()) return Task.CompletedTask;

        if (entry.IsDirectory) return NavigateAsync(entry.FullPath);

        // **A shortcut to a folder navigates the pane**, rather than being
        // handed to the shell, which opened a separate Explorer window. Only
        // when it points at a folder: a shortcut to a program is still the
        // system's to launch, and following that one ourselves would be
        // re-implementing what the shell does properly.
        if (Shortcuts?.TargetOf(entry.FullPath) is { } target && Directory.Exists(target))
            return NavigateAsync(target);

        // Recorded on the ATTEMPT, not on the outcome. Asking to open a file is
        // the user's act either way, which is the recency semantic that
        // matters, and a launch the desktop accepts is still no promise that
        // anything appeared — so there is no better moment than this one.
        Recording?.Record(entry.FullPath, RecentKind.File);

        // **This was a bare call with nothing after it**, because Open returned
        // void — the comment above used to say so and treat it as the end of
        // the matter. Both launchers caught the failure and dropped it, so
        // double-clicking a row whose file had been deleted since the listing
        // was drawn did nothing at all: no window, no message, nothing to
        // distinguish it from a click that missed.
        //
        // One line covers both routes that reach a file: the pointer and Enter
        // come through here, and so does a path typed into the location bar,
        // which opens through this method rather than the launcher.
        if (_launcher?.Open(entry.FullPath) is { } failure)
            Status = Failures.Describe(failure, "open that file");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Drops the selected entries from the recency store — Dolphin's "Forget",
    ///
    /// **It removes the RECORD, never the file.** That distinction is the whole
    /// point of the action: a recent list you cannot prune is a log rather than
    /// a tool, but a Forget that deleted things would be catastrophic next to a
    /// Delete one row above it in the same menu.
    ///
    /// The listing is built from the store, so it has to be rebuilt afterwards
    /// — nothing watches a virtual path, by design.
    /// </summary>
    [RelayCommand]
    private async Task ForgetRecentAsync()
    {
        if (Recents is null) return;

        var paths = SelectionPaths();
        if (paths.Count == 0) return;

        foreach (var path in paths) Recents.Forget(path);

        if (VirtualPaths.IsRecent(CurrentPath)) await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>Whether there is anything in the bin this can destroy.</summary>
    public bool CanPurgeFromBin => Trash is not null && IsTrashListing && Selection.Count > 0;

    /// <summary>
    /// Destroys the selected trashed items, permanently.
    ///
    /// **A confirmed yes was refused.** Shift+Delete on a bin row showed the
    /// permanent-delete prompt, took the answer, and then declined — because
    /// the only ways out of the bin were Restore and Empty, and a bin row
    /// carries the path the file USED to occupy, which the file operations
    /// cannot act on. Asked and answered and nothing happened is worse than
    /// never offering, and both references delete just the items you picked.
    /// </summary>
    [RelayCommand]
    public async Task PurgeFromTrashAsync()
    {
        // Said out loud rather than returned from in silence. Restore gets away
        // with a quiet return because it is an inert button; this arrives from
        // a confirmation, and a destructive yes that produces nothing at all is
        // the very fault being fixed.
        if (Trash is null)
        {
            Status = $"{Core.Naming.TheBin} is not available";
            return;
        }

        if (!IsTrashListing) return;

        // **The row that was clicked, not the newest sharing its path.** Two
        // bin rows can carry the same original path — trash a file, restore it,
        // trash it again — and Restore resolves that by taking the newest,
        // because the loser stays put and can be restored next. Here the loser
        // is gone for good: taking the newest would destroy the item nobody
        // pointed at and leave the row they did point at on screen, which is
        // the wrong-thing-destroyed shape the bin's refusals exist to prevent.
        //
        // The row already tells them apart. The trash listing passes each
        // item's deletion time straight into LastWriteTime, so the pair
        // identifies exactly one item — and N selected rows destroy N items
        // rather than one.
        var wanted = Selection
            .Select(e => (e.FullPath, e.LastWriteTime))
            .ToHashSet();

        if (wanted.Count == 0) return;

        var chosen = Trash.List()
            .Where(item => wanted.Contains((item.OriginalPath, item.Deleted)))
            .ToList();

        var destroyed = 0;
        var failed = 0;

        foreach (var item in chosen)
        {
            try
            {
                Trash.Delete(item.TrashName);
                destroyed++;
            }
            catch (Exception ex)
            {
                // One failure must not abandon the rest of the selection, the
                // same rule restoring follows.
                failed++;
                Console.Error.WriteLine($"[vaktari] purge failed: {ex.Message}");
            }
        }

        var report = (destroyed, failed) switch
        {
            (0, 0) => "nothing deleted",
            (0, _) => $"could not delete {failed:N0} item(s) — see the log",
            (_, 0) => $"deleted {destroyed:N0} item(s) for good",
            _ => $"deleted {destroyed:N0} for good, {failed:N0} failed",
        };

        await RefreshAsync().ConfigureAwait(false);
        await SayAsync(report).ConfigureAwait(false);
    }

    /// <summary>
    /// Says something in the status line, after the reload it followed.
    ///
    /// **The report was wiped by the listing it was reporting on.** A bin
    /// action ends by refreshing, and a load ends by clearing Status — on
    /// purpose, so the item count does not appear twice in the status bar. Set
    /// before the refresh, "deleted 3 items for good" lived for as long as the
    /// reload took and was then blanked, which is the whole "asked, answered,
    /// nothing happened" fault one layer further in.
    ///
    /// On the dispatcher because the refresh was awaited with
    /// <c>ConfigureAwait(false)</c>: the caller resumes on a pool thread, and
    /// Status raises PropertyChanged straight into a binding.
    /// </summary>
    private Task SayAsync(string message)
        => Dispatcher.UIThread.InvokeAsync(() => Status = message).GetTask();

    /// <summary>
    /// Puts the selected trashed items back.
    ///
    /// The listing shows ORIGINAL paths, and Restore needs the trash KEY, so
    /// the mapping is looked up from the store rather than derived from the
    /// name — a deduplicated key like `notes.3.txt` cannot be reversed into
    /// `notes.txt` reliably, and guessing would restore the wrong file.
    ///
    /// **A restore onto an occupied name was completely silent.** Both bins
    /// restore beside rather than over — <see cref="ITrashMaintenance.Restore"/>
    /// returns the landing path for exactly that reason — and this loop threw
    /// the answer away and reported "restored 1 item(s)". The listing on screen
    /// is the bin, not the folder the file went to, so there was nothing
    /// anywhere to say that the name had changed.
    /// </summary>
    [RelayCommand]
    private async Task RestoreFromTrashAsync()
    {
        if (Trash is null || !IsTrashListing) return;

        var wanted = SelectionPaths().ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;

        var restored = 0;
        var failed = 0;

        // Where the items that could not have their own name back actually
        // went. Empty is the ordinary case, and costs one allocation.
        var renamed = new List<string>();

        // **One entry per selected row, not every entry that shares its path.**
        // The rows carry original paths, and two items in the bin can
        // legitimately have the same one — delete a file, restore it, delete it
        // again, which is exactly when somebody reaches for restore. Matching on
        // the path alone put BOTH back from one selected row, the second landing
        // beside the first under a deduplicated name.
        //
        // The newest wins, which is the row a person means when they say "put
        // that back".
        var chosen = Trash.List()
            .Where(item => wanted.Contains(item.OriginalPath))
            .GroupBy(item => item.OriginalPath, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => item.Deleted).First());

        foreach (var item in chosen)
        {
            try
            {
                var landed = Trash.Restore(item.TrashName);
                restored++;

                // PathRules.Same rather than ==, because this decides whether
                // to tell somebody their file came back under a different
                // name: the platform rules for what counts as the same place
                // are the ones that matter, and on Windows those ignore case
                // and both separator spellings.
                if (!PathRules.Same(landed, item.OriginalPath)) renamed.Add(landed);
            }
            catch (Exception ex)
            {
                // One failure must not abandon the rest of the selection.
                failed++;
                Console.Error.WriteLine($"[vaktari] restore failed: {ex.Message}");
            }
        }

        // **The failures are counted out loud.** Restoring four items where one
        // could not be put back reported "restored 3", and the row that stayed
        // behind looked like one the user had simply not selected. Console
        // output is not somewhere anybody is going to look.
        var report = (restored, failed) switch
        {
            (0, 0) => "nothing restored",
            (0, _) => $"could not restore {failed:N0} item(s) — see the log",
            (_, 0) => $"restored {restored:N0} item(s)",
            _ => $"restored {restored:N0}, {failed:N0} failed",
        };

        if (renamed.Count > 0) report += " — " + Landed(renamed);

        await RefreshAsync().ConfigureAwait(false);
        await SayAsync(report).ConfigureAwait(false);
    }

    /// <summary>
    /// What the report adds when the bin could not give an item its own name
    /// back.
    ///
    /// The single case names the new leaf, because there is one thing to look
    /// for and the person is standing in the bin rather than in the folder it
    /// went to. Twenty of them would be a paragraph, so several are counted.
    /// </summary>
    private static string Landed(IReadOnlyList<string> renamed)
        => renamed.Count == 1
            ? $"the name was taken, so it is back as {PathRules.LeafName(renamed[0])}"
            : $"{renamed.Count:N0} names were taken, so those are back under new ones";

    /// <summary>
    /// Permanently deletes everything in the trash. **Always confirmed by the
    /// caller** — this is the one action in the application with no undo and no
    /// per-item review, so the prompt is not a preference the way trashing is.
    /// </summary>
    public async Task EmptyTrashAsync()
    {
        if (Trash is null) return;

        string report;

        try
        {
            var result = await Trash.EmptyAsync(CancellationToken.None).ConfigureAwait(false);

            report = $"emptied {Core.Naming.BinName} — removed {result.Removed:N0}, "
                   + $"freed {ByteSize.Format(result.BytesFreed)}";
        }
        catch (Exception ex)
        {
            // **A failure here was completely silent.** Emptying is the one
            // action with no undo, so "did it work?" is a question people
            // actually ask — and a file the shell still has open, or a
            // permission the recycle bin will not give up, left the items in
            // place, the status line blank, and the listing unchanged. Nothing
            // to distinguish that from an empty bin.
            report = $"could not empty {Core.Naming.TheBin}: {ex.Message}";

            Console.Error.WriteLine($"[vaktari] empty failed: {ex}");
        }

        // Outside the try: whatever happened, some of it may have gone, and a
        // listing still showing deleted rows is worse than one that is late.
        if (IsTrashListing) await RefreshAsync().ConfigureAwait(false);

        await SayAsync(report).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches version-control state for a folder and publishes it if that
    /// folder is still the one being shown.
    ///
    /// Fire-and-forget from the load path, so it carries its own catch-all:
    /// an unobserved exception on a pool thread is a process abort, and this
    /// runs a subprocess.
    ///
    /// <paramref name="ct"/> is read by the CALLER, on the UI thread that owns
    /// <c>_cts</c>. Reading it in here means racing the next navigation, which
    /// disposes that source — and a disposed source throws from `.Token`, on a
    /// pool thread, where the only trace is a swallowed exception and marks
    /// that stopped appearing.
    /// </summary>
    private async Task RefreshVcsAsync(string path, int generation, CancellationToken ct)
    {
        var empty = new Dictionary<string, Vaktari.Core.Vcs.VcsState>();

        // The setting is read HERE rather than at startup, so turning it off
        // takes effect on the next folder load without a restart — and turning
        // it on does not need one either.
        // Written as "explicitly off" rather than "not on", so a settings group
        // that is somehow null reads as the DEFAULT (enabled) instead of
        // throwing. `SettingsState` declares `Vcs { get; init; } = new()` and
        // should never hand back null — but it did, this method's catch-all
        // swallowed the NullReferenceException, and the decorations silently
        // stopped. **A feature must not depend on a settings group being
        // non-null to work at all.**
        if (Vcs is null
            || Settings.AppSettings.Current.Vcs is { ShowDecorations: false }
            || VirtualPaths.IsVirtual(path))
        {
            VcsStates = empty;

            // Clear rather than skip: navigating from a repository into a
            // virtual listing must not leave the previous folder's marks
            // standing.
            Thumbnails.RowVcs.Publish(path, null);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsRepository = false;
                StartWatchingRepository(null);
            });

            // Say WHICH of the three reasons. Returning silently made "no marks"
            // mean "no provider", "switched off" or "not a real folder" with no
            // way to tell them apart — and this method's only other output is a
            // line that simply never appears.
            Console.Error.WriteLine(
                "[vaktari] vcs: skipped — "
                + (Vcs is null ? "no provider (is git installed?)"
                   : Settings.AppSettings.Current.Vcs is { ShowDecorations: false }
                       ? "disabled in settings"
                   : "virtual listing")
                + $" · {path}");

            return;
        }

        try
        {
            var snapshot = await Vcs.StatusAsync(path, ct).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The same guard every other dispatcher block here uses:
                // cancelling does not unqueue a callback already in flight, and
                // publishing this against a newer folder would decorate the
                // wrong rows.
                if (generation != _generation) return;

                VcsStates = snapshot?.States ?? empty;

                // Started from here because this is where the root is already
                // known — FindRoot walked for it, and asking twice would mean
                // two directory walks per folder open.
                StartWatchingRepository(snapshot?.Root);

                // A snapshot with a Root means we are in a repository even when
                // every file is clean — the column should appear, empty, rather
                // than flicker in only once something is modified.
                IsRepository = snapshot is not null;

                // Hand it to the row decorator. Rows are already on screen by
                // now — status is fetched AFTER the listing deliberately — so
                // publishing raises an event that makes the realized rows look
                // again.
                Thumbnails.RowVcs.Publish(path, snapshot?.States);

                Console.Error.WriteLine(
                    $"[vaktari] vcs: {Vcs.Name} · {VcsStates.Count} decorated "
                    + $"· root={snapshot?.Root ?? "(none)"} · {path}");
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; the newer one owns the state.
        }
        catch (Exception ex)
        {
            // Type as well as message. "Object reference not set" alone does not
            // say which reference, and this catch-all had been quietly turning a
            // crash into an absence of marks.
            Console.Error.WriteLine($"[vaktari] vcs: {ex.GetType().Name}: {ex}");
        }
    }

    /// <summary>Paths of the selection, falling back to the focused row.</summary>
    public IReadOnlyList<string> SelectionPaths()
        => Selection.Count > 0
            ? Selection.Select(e => e.FullPath).ToList()
            : SelectedEntry is { } one ? [one.FullPath] : [];

    /// <summary>
    /// The entries the user means, which is the whole selection when there is
    /// one and the focused row otherwise.
    ///
    /// **The twin of <see cref="SelectionPaths"/>, for the verbs that need the
    /// entry rather than the path.** Nine call sites used SelectionPaths and
    /// the open verbs used SelectedEntry, so selecting five files and pressing
    /// Enter opened one of them — silently, with no sign that the other four
    /// had been ignored.
    /// </summary>
    public IReadOnlyList<FileEntry> EntriesToActOn()
        => Selection.Count > 0
            ? Selection.ToList()
            : SelectedEntry is { } one ? [one] : [];

    /// <summary>
    /// How many things Vaktari will open at once before it stops and says so.
    ///
    /// Explorer asks for confirmation around fifteen; the number matters less
    /// than there being one, because "open" on a selection of four hundred
    /// files launches four hundred processes and the machine is gone.
    /// </summary>
    internal const int OpenLimit = 15;

    /// <summary>
    /// Takes on an operation this pane did not start, so it gets the bar, the
    /// progress, the pause and the cancel that any other one does.
    ///
    /// A public door onto the private tracking, for the retry: the offer is
    /// pressed on the shell's bar, but the operation behind it belongs to a
    /// pane like every other.
    /// </summary>
    public void Adopt(IOperationHandle handle) => Track(handle);

    private void Track(IOperationHandle handle)
    {
        OperationStarted?.Invoke(this, handle);

        // The listing is refreshed once, at the end — refreshing per item would
        // rebuild the view thousands of times during a large copy.
        //
        // And the history with it: what finished here is what Ctrl+Z will take
        // back, so the menu row has to learn its new name at the same moment
        // the rows appear.
        _ = handle.Completion.ContinueWith(
            _ => Dispatcher.UIThread.Post(() =>
            {
                RefreshUndoState();
                _ = RefreshAsync();
            }),
            TaskScheduler.Default);
    }

    partial void OnSelectedEntryChanged(FileEntry? value)
    {
        // The focused row counts as a selection on its own — a right-click sets
        // it before the menu opens, and on a single-click it is all there is.
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(CanCreateShortcut));
        OnPropertyChanged(nameof(CanPurgeFromBin));
        OnPropertyChanged(nameof(CanRenameInBulk));
        OnPropertyChanged(nameof(HasDirectorySelected));
        OnPropertyChanged(nameof(HasAnyDirectorySelected));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
        OnPropertyChanged(nameof(CanCompressSelection));
        OnPropertyChanged(nameof(CanExtractSelection));

        if (IsPreviewVisible) _ = RefreshPreviewAsync();

        OpenWithOptions.Clear();

        // Raised on the way out as well as after a refill: a folder clears the
        // list and returns here, and without this the row stayed visible from
        // whatever was selected before it.
        OnPropertyChanged(nameof(HasOpenWithOptions));

        if (_launcher is null || value is not { IsDirectory: false } entry) return;

        // Enumeration shells out to xdg-mime, so keep it off the UI thread.
        var path = entry.FullPath;
        _ = Task.Run(() =>
        {
            var options = _launcher.GetOpenWithOptions(path);

            // **Asked out here, beside the enumeration it belongs with.** It
            // was read inside the Post, on the UI thread, which cost nothing
            // while the only platform that answered yes returned a constant.
            // A desktop answers it by scanning every .desktop file the machine
            // has, and the first right-click would have paid for that scan on
            // the thread that draws the menu.
            var chooser = _launcher.CanChooseApplication;

            Dispatcher.UIThread.Post(() =>
            {
                var wanted = new List<LaunchOption>(options);

                // Last, and only where there is a chooser to show. The
                // installed applications are the answer most of the time; this
                // is the way out when none of them is, and it belongs at the
                // bottom of the list it escapes from.
                if (chooser)
                {
                    wanted.Add(
                        new LaunchOption("Choose another app…", "", null) { IsChooser = true });
                }

                // **Only when it actually differs.** Clearing an
                // ObservableCollection destroys the containers built from it, so
                // a refill that lands while this submenu is open would close it
                // under the pointer — which is exactly how the shell menu's
                // submenus blinked and never appeared. This arrives after the
                // selection changed, which is before the menu is even drawn,
                // but on a desktop where the answer comes from shelling out to
                // xdg-mime the two can overlap. Almost every time it carries
                // what is already there.
                if (OpenWithOptions.SequenceEqual(wanted)) return;

                OpenWithOptions.Clear();
                foreach (var option in wanted) OpenWithOptions.Add(option);

                OnPropertyChanged(nameof(HasOpenWithOptions));
            });
        });
    }

    [RelayCommand]
    public void OpenWithApp(LaunchOption? option)
    {
        // **The same bin hazard, through the other door.** "Open with" is drawn
        // on a bin row — OpenWithMenu binds HasOpenWithOptions, which is filled
        // for any file selection and never asks whether the listing is the bin
        // — and it hands the launcher a path itself rather than going through
        // OpenAsync, so the guard up there left this open. Choosing an
        // application for a binned notes.txt opened whatever now holds that
        // path, with nothing to say so.
        if (RefusedInBin()) return;

        if (option is null || SelectedEntry is not { } entry) return;

        // The chooser opens the file itself once something is picked, so it
        // records the same way — and is checked BEFORE the recent entry,
        // because a cancelled chooser opened nothing and must not claim to.
        //
        // One file only: the system's chooser takes a single path, and asking
        // it five times would stack five dialogs.
        if (option.IsChooser)
        {
            if (_launcher is not { } launcher) return;

            // **The platform's own dialog first, and ours only where there is
            // none.** Windows' browses for an executable and registers the
            // choice, which is what SHOpenWithDialog's ALLOW_REGISTRATION is
            // for; nothing drawn here does either, so nothing drawn here is
            // offered in its place. A desktop, which has no such dialog to
            // show, answers false and hands over its list instead.
            if (launcher.ChooseApplication(entry.FullPath))
            {
                Recording?.Record(entry.FullPath, RecentKind.File);
                return;
            }

            // Empty is the answer of a platform whose chooser was the dialog
            // above, and of a machine whose desktop database is unreadable.
            // Neither has anything to put in a window.
            if (launcher.AllApplications is not { Count: > 0 } installed) return;

            ChooseApplicationRequested?.Invoke(this, new ChooseApplicationViewModel(
                entry.Name,
                installed,
                chosen =>
                {
                    // Recorded when something is picked, not when the window
                    // opens — the same rule the branch above keeps by asking
                    // first and recording after. A chooser that was dismissed
                    // opened nothing and must not claim to.
                    Recording?.Record(entry.FullPath, RecentKind.File);
                    launcher.OpenWith(entry.FullPath, chosen);
                }));

            return;
        }

        // All of them, for the same reason Open does: choosing an application
        // for five selected images and having one open is a silent loss.
        var files = EntriesToActOn().Where(e => !e.IsDirectory).ToList();

        if (files.Count == 0 || TooMany(files.Count)) return;

        foreach (var file in files)
        {
            // Same act as OpenAsync, so it belongs in the recent list too.
            // Missing this would make the list quietly depend on WHICH way you
            // opened something, which nobody would guess from the UI.
            Recording?.Record(file.FullPath, RecentKind.File);

            _launcher?.OpenWith(file.FullPath, option);
        }
    }

    /// <summary>
    /// The terminals this machine has, the user's choice first and marked.
    ///
    /// **The preference is applied here rather than in the launcher**, because
    /// settings live in this assembly and the launcher's does not reference it.
    /// The platform reports what it found; which one is "the" terminal is a
    /// question about the user, not about the machine.
    ///
    /// An id naming something not installed is ignored rather than honoured
    /// into a failure — uninstalling Warp must not break F4.
    /// </summary>
    public IReadOnlyList<Vaktari.Core.FileSystem.TerminalOption> Terminals
    {
        get
        {
            var found = _launcher?.Terminals ?? [];
            var wanted = Settings.AppSettings.Current.General.PreferredTerminal;

            if (string.IsNullOrEmpty(wanted)) return found;
            if (found.FirstOrDefault(t => t.Id == wanted) is not { } chosen) return found;

            return [chosen with { IsPreferred = true }, .. found.Where(t => t.Id != wanted)];
        }
    }

    /// <summary>
    /// Whether to offer a choice at all. With one terminal installed — which is
    /// most machines — a submenu holding a single entry is a hover for nothing,
    /// so the menu shows the plain command instead.
    /// </summary>
    public bool HasSeveralTerminals => Terminals.Count > 1;

    /// <summary>
    /// The two shapes of the terminal entry, each carrying the folder gate as
    /// well as the count. Combined here rather than in the markup because a
    /// menu row hides when it would do nothing -- the convention CanActOnSelection
    /// sets -- and hiding on two conditions at once is a view-model question.
    /// </summary>
    public bool ShowOneTerminal => IsRealFolder && !HasSeveralTerminals;

    public bool ShowTerminalChoice => IsRealFolder && HasSeveralTerminals;

    /// <summary>F4 and the plain entry: the chosen terminal.</summary>
    [RelayCommand]
    public void OpenTerminalHere()
    {
        // A terminal cannot be opened in a listing that is not a folder, and
        // "cd vaktari:trash" is what it was being asked to do.
        if (!IsRealFolder) return;

        if (Terminals.FirstOrDefault() is { } preferred)
        {
            _launcher?.OpenTerminal(CurrentPath, preferred);
            return;
        }

        // Nothing was detected, which is not the same as nothing being
        // installed: the launcher still has its own fall-through.
        _launcher?.OpenTerminal(CurrentPath);
    }

    /// <summary>One named terminal, chosen from the submenu.</summary>
    [RelayCommand]
    public void OpenTerminalIn(Vaktari.Core.FileSystem.TerminalOption? terminal)
    {
        if (terminal is null || !IsRealFolder) return;

        _launcher?.OpenTerminal(CurrentPath, terminal);
    }












    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CurrentPath);

    [RelayCommand]
    /// <summary>
    /// Opens what is selected — all of it.
    ///
    /// **This used to open exactly one file however many were selected.** Pick
    /// five images, press Enter, and one opened with nothing to say the other
    /// four had been dropped. A folder is still navigated rather than opened
    /// alongside, and only when it is the only thing chosen: navigating "into"
    /// five folders has no meaning.
    /// </summary>
    public async Task OpenSelectedAsync()
    {
        // **Opening a bin row opened the wrong file, or none.** A binned row
        // carries the path the item USED to occupy. If nothing is there now the
        // gesture does nothing and says nothing; if a new file of that name has
        // since been written, Enter opens THAT — a different file, with no sign
        // anything unusual happened. The same shape as the delete that took the
        // wrong file.
        //
        // OpenAsync refuses too, and has to, because the pointer route reaches
        // it without passing through here. This copy is not redundant: it runs
        // BEFORE the count check below, so a large selection in the bin is told
        // it is in the bin rather than told to select fewer — the count refusal
        // would otherwise answer a question the user is not being stopped for.
        if (RefusedInBin()) return;

        var entries = EntriesToActOn();

        if (entries.Count == 0) return;

        if (entries.Count == 1)
        {
            await OpenAsync(entries[0]).ConfigureAwait(true);
            return;
        }

        if (TooMany(entries.Count)) return;

        // Files only past this point: a multi-selection containing folders
        // opens the files and leaves the folders alone, because there is no
        // sensible "navigate into all of these".
        foreach (var entry in entries.Where(e => !e.IsDirectory))
            await OpenAsync(entry).ConfigureAwait(true);
    }

    /// <summary>
    /// Refuses to launch an unreasonable number of things at once, and says so
    /// rather than doing nothing.
    /// </summary>
    private bool TooMany(int count)
    {
        if (count <= OpenLimit) return false;

        Status = $"that would open {count} things at once — select fewer";
        return true;
    }








    partial void OnCurrentPathChanged(string value)
    {
        // CurrentPath is assigned from LoadListingAsync after a ConfigureAwait,
        // so this runs on a pool thread. Breadcrumbs is bound to the UI, and
        // mutating it from here is a crash waiting for a slow directory.
        // The column flags depend on the path too — a recent listing shows the
        // parent-path column and hides the metadata one — and they are bound,
        // so they are raised on the same hop rather than from here.
        Dispatcher.UIThread.Post(() =>
        {
            RebuildBreadcrumbs();
            OnPropertyChanged(nameof(IsRecentListing));
            OnPropertyChanged(nameof(IsTrashListing));

            // All six, because a search moves between searches: retyping the
            // query or ticking either box changes the path from one search
            // to another, and a band that only appeared and disappeared would
            // go on showing the previous question.
            OnPropertyChanged(nameof(IsSearchListing));

            // The menu row that goes to where a row lives is bound to this one,
            // and a change announced for IsSearchListing is not a change
            // announced for this: without the line the row keeps whatever
            // visibility the previous listing left it with.
            OnPropertyChanged(nameof(CanGoToLocation));
            OnPropertyChanged(nameof(SearchQueryText));
            OnPropertyChanged(nameof(CanScopeSearch));
            OnPropertyChanged(nameof(SearchScopedHere));
            OnPropertyChanged(nameof(SearchScopeLabel));
            OnPropertyChanged(nameof(SearchMatchesCase));

            OnPropertyChanged(nameof(IsRealFolder));

            // Beside IsRealFolder because it IS IsRealFolder, and the row
            // template and the column heading both reserve the triangle's slot
            // from it: without this the slot would keep whatever width the
            // previous listing left it with.
            OnPropertyChanged(nameof(CanExpandRows));
            OnPropertyChanged(nameof(DisplayPath));
            OnPropertyChanged(nameof(EmptyText));
            OnPropertyChanged(nameof(Terminals));
            OnPropertyChanged(nameof(HasSeveralTerminals));
            OnPropertyChanged(nameof(ShowOneTerminal));
            OnPropertyChanged(nameof(ShowTerminalChoice));
            OnPropertyChanged(nameof(CanActOnSelection));

            // Both read IsRealFolder, so both change when the pane moves
            // between a folder and one of the virtual listings — a change
            // announced for IsRealFolder is not a change announced for these.
            OnPropertyChanged(nameof(CanCompressSelection));
            OnPropertyChanged(nameof(CanExtractSelection));

            // Beside CanActOnSelection because it carries the same selection
            // half — but its OTHER half is IsRealFolder, and a change announced
            // for IsRealFolder is not a change announced for this. Without the
            // line the row keeps whatever visibility the previous listing left
            // it with, the same way CanGoToLocation would above.
            OnPropertyChanged(nameof(CanCreateShortcut));
            OnPropertyChanged(nameof(CanPurgeFromBin));
        OnPropertyChanged(nameof(CanPurgeFromBin));
        OnPropertyChanged(nameof(CanRenameInBulk));
            OnPropertyChanged(nameof(ShowParentPath));
            OnPropertyChanged(nameof(ShowMetadata));

            // CanGoUp depends on CurrentPath, and the copy of this call inside
            // LoadListingAsync runs on a POOL THREAD — where a binding update
            // is not guaranteed to be applied. CanGoForward hid that, because
            // it also changes when Back is pressed, which is on the UI thread;
            // CanGoUp changes only with the path, so it stayed stale and the
            // Up button remained enabled on a virtual listing.
            NotifyNavigationState();
        });

        _ = RefreshFreeSpaceAsync(value);

        // A virtual listing has no filename to fall back on: GetFileName of
        // "vaktari:recent-files" is the whole string, since it contains no
        // separator, and that is what the tab would have been titled.
        if (VirtualPaths.IsVirtual(value))
        {
            Title = VirtualPaths.Label(value);
            return;
        }

        // **A drive root was titled "C:".** LeafName gives a root back as
        // itself, while the sidebar three inches away called the same drive
        // "Windows (C:)" — because building THAT list is where the volume label
        // is read. One machine, two names for one drive, and the useless one in
        // the place you look most.
        //
        // The places provider answers from what its last listing worked out,
        // never by asking the disk, so this cannot wait on a mapped drive that
        // has gone away. It answers null for anything that is not a drive, and
        // then LeafName is what it always was — which also keeps the "/"
        // fallback from being a Linux-shaped guess about what a root looks
        // like.
        Title = Places?.NameFor(value) ?? PathRules.LeafName(value);
    }

    /// <summary>
    /// Restored tabs enumerate only when first activated. Recreating twenty
    /// tabs eagerly means twenty listings at startup, and one of them sitting
    /// on an unreachable share costs the whole window its SMB timeout.
    /// </summary>
    partial void OnIsActiveChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            Detached(LoadRestoredAsync(CurrentPath), "load");
    }

    /// <summary>
    /// Adopt persisted state without touching the filesystem. ShowHidden is set
    /// under suppression because its change handler triggers a reload, which is
    /// exactly what lazy restore is trying to avoid.
    ///
    /// **_restoringView as well, or restoring a session gave every folder that
    /// was open an opinion it never had.** CurrentPath is assigned first here,
    /// so by the time `View = tab.View` runs RememberFolderView has a folder to
    /// write against — and _suppressReload does not gate that write, only
    /// _restoringView does. Measured in this worktree with RememberViewPerFolder
    /// on, a default of Grid and an empty store: restoring one Details tab left
    /// one entry behind, and the next tab opened at that folder then came up
    /// Details. The write itself is not new — it fires whenever a saved value
    /// differs from the field's starting one — but while that starting value
    /// was the literal Details it could only ever record the layout the tab was
    /// already in. A settable default makes it record the OLD layout in
    /// defiance of the new one, which is the thing the "use this view for all
    /// folders" ForgetAll exists to prevent.
    /// </summary>
    public void RestoreFrom(TabState tab)
    {
        _restoringView = true;
        _suppressReload = true;
        try
        {
            CurrentPath = tab.Path;
            PathText = tab.Path;
            Sort = tab.Sort;
            SortDescending = tab.SortDescending;
            ShowHidden = tab.ShowHidden;
            View = tab.View;
            GroupBy = tab.GroupBy;
            HideSizeColumn = tab.HideSize;
            HideModifiedColumn = tab.HideModified;
            ShowTypeColumn = tab.ShowType;
            ShowCreatedColumn = tab.ShowCreated;

            // Guarded: a session written before these existed deserialises as
            // 0, which would restore an invisible pane.
            FontScale = tab.FontScale > 0 ? tab.FontScale : 1.0;
            IconScale = tab.IconScale > 0 ? tab.IconScale : 1.0;

            // Details keeps the original pair; the other two restore their own
            // and fall back to it. **Zero means absent** — deserialization does
            // not run property initializers here, so a session written before
            // v13 has no grid or compact keys at all and every layout should
            // start where details was left.
            SeedScales(FontScale, IconScale);

            _scales[ViewMode.Grid] = (
                tab.GridFontScale > 0 ? tab.GridFontScale : FontScale,
                tab.GridIconScale > 0 ? tab.GridIconScale : IconScale);

            _scales[ViewMode.Compact] = (
                tab.CompactFontScale > 0 ? tab.CompactFontScale : FontScale,
                tab.CompactIconScale > 0 ? tab.CompactIconScale : IconScale);

            // The active layout's pair has to become the live one, or a tab
            // restored into grid would show the details size until the next
            // switch.
            var (font, icon) = _scales[View];

            _swappingScales = true;
            try { FontScale = font; IconScale = icon; }
            finally { _swappingScales = false; }

            // **Absent means null here, not empty.** The `= []` on TabState is
            // decorative for the same reason the scale defaults above are, so a
            // session without these keys — hand-edited, or from a build that
            // kept no history — crashed startup in this loop. A session file
            // must never prevent startup; SessionState says so on itself.
            _back.Clear();
            if (!ReferenceEquals(tab.BackStack, null))
                foreach (var p in tab.BackStack) _back.Push(p);

            _forward.Clear();
            if (!ReferenceEquals(tab.ForwardStack, null))
                foreach (var p in tab.ForwardStack) _forward.Push(p);
        }
        finally
        {
            _suppressReload = false;
            _restoringView = false;
        }

        IsLoaded = false;
        Status = "not loaded";
        NotifyNavigationState();
    }

    /// <summary>
    /// Load now if the pane was restored but never activated into a load.
    ///
    /// **Assigning ActiveTab DOES reach the activate handler**, contrary to what
    /// this said: PaneGroupViewModel.OnActiveTabChanged sets IsActive on the
    /// incoming tab unconditionally, and ShellViewModel's _restoring flag guards
    /// only MarkDirty. Measured on a real group — assigning ActiveTab to a
    /// restored, unloaded pane starts exactly one load, and this call then finds
    /// IsLoading already true and does nothing. It is kept as the guard for a
    /// restored tab that reaches neither door, and the two are kept from both
    /// running by LoadRestoredAsync claiming IsLoading before its first await.
    /// </summary>
    public void RefreshIfUnloaded()
    {
        if (!IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            Detached(LoadRestoredAsync(CurrentPath), "load");
    }

    /// <summary>
    /// How long a restored tab's folder has to say it is there before the tab
    /// is called dead.
    ///
    /// Short, because it is not the listing — it is one existence check. Two
    /// seconds is a judgement and not a measurement; the test pins only that the
    /// probe is bounded and under ten, so the number can be retuned here. A
    /// share that
    /// needs longer than this loses nothing but the automatic load: the
    /// sentence is on screen and any navigation to the path, including F5, goes
    /// straight to the listing without a probe.
    /// </summary>
    private static readonly TimeSpan ReachabilityProbe = TimeSpan.FromSeconds(2);

    /// <summary>
    /// What a restored tab says when its folder does not answer.
    ///
    /// **Both what happened, not one of them.** The probe cannot tell a folder
    /// that has been deleted from a server that is not answering — Directory
    /// .Exists returns false for the first and the timeout returns false for
    /// the second — so the sentence claims only what was measured: the path
    /// could not be reached. Failures.Describe has separate sentences for
    /// both, and it earns them from an exception this path never gets.
    /// </summary>
    private const string Unreachable = "that folder could not be reached";

    /// <summary>
    /// The first load of a tab that session restore left standing, which asks
    /// whether the path answers at all before enumerating it.
    ///
    /// **IsReachableAsync was implemented on both providers and called from
    /// nowhere**, and its own doc comment claimed this caller existed. Both
    /// doors into a restored tab's first load — this and OnIsActiveChanged —
    /// went straight to LoadAsync, so a restored tab whose folder had gone
    /// entered the listing and stayed in it: IsLoading true, LoadError empty,
    /// and nothing on screen separating "still reading" from "never going to
    /// work" until the enumeration itself failed. How long that takes on a
    /// share whose server has gone away is the providers' own comments to
    /// state, and both of them do; what is measured here is that the pane had
    /// no answer of its own in the meantime.
    ///
    /// Only on this path, not in LoadListingAsync. Every other navigation is
    /// somebody asking for a specific folder right now, and a probe in front of
    /// those would put an extra existence check on the front of every folder
    /// open for a message the catch block already produces from the real error.
    /// A restored tab is the opposite case: nobody asked for it just now, it
    /// was simply where the window was last time.
    /// </summary>
    private async Task LoadRestoredAsync(string path)
    {
        // No empty-path guard: both callers already have one, and a second
        // copy here would be a branch no test could ever reach.

        // **Claimed before the first await.** Both callers can fire for one
        // tab — ReopenClosedTab assigns ActiveTab, which reaches
        // OnIsActiveChanged, and then calls RefreshIfUnloaded — and until this
        // method existed the second one found IsLoading already true, because
        // LoadListingAsync sets it before it yields. A probe in front of that
        // moved the first await earlier, so without this both would run.
        IsLoading = true;

        // Read before the probe and again after it, the way every other
        // resumption in this file checks it: a navigation while the probe is in
        // flight has already taken the pane somewhere else, and this
        // continuation must not drag it back to a path nobody is on any more.
        var generation = _generation;

        // Virtual listings are not folders, and the probe is Directory.Exists
        // on both platforms. Measured: Directory.Exists answers false for
        // "vaktari:trash", "vaktari:computer" and "vaktari:recent-files", so
        // probing one would report the bin, This PC or either recent listing
        // unreachable, so clicking a restored bin, This PC or recent tab would
        // put that sentence up in place of the listing every single time. The
        // sidebar row would still work, because it navigates and navigation
        // never probes — which is a worse bug for being half-hidden.
        if (!VirtualPaths.IsVirtual(path))
        {
            var reachable = await _fs
                .IsReachableAsync(path, ReachabilityProbe, CancellationToken.None)
                .ConfigureAwait(true);

            if (generation != _generation) return;

            if (!reachable)
            {
                // Said in the listing and in the status bar, exactly as the
                // catch in LoadListingAsync says its sentence in both: the bar
                // describes the ACTIVE pane, so the other half of a split would
                // otherwise report nothing at all.
                LoadError = Unreachable;
                Status = Unreachable;

                // IsLoaded stays false, so switching away and back probes
                // again — which is the retry, and costs the same two seconds.
                IsLoading = false;
                return;
            }
        }

        await LoadAsync(path).ConfigureAwait(true);
    }

    public TabState ToTabState() => new()
    {
        Path = CurrentPath,
        Sort = Sort,
        SortDescending = SortDescending,
        ShowHidden = ShowHidden,
        View = View,
        GroupBy = GroupBy,
        HideSize = HideSizeColumn,
        HideModified = HideModifiedColumn,
        ShowType = ShowTypeColumn,
        ShowCreated = ShowCreatedColumn,
        // **All three read from `_scales`, including details.** The live
        // `FontScale`/`IconScale` hold whichever layout is ON SCREEN, so writing
        // them into the details slot would have saved the grid's size as the
        // details size whenever the tab was left in grid. `_scales[View]` is kept
        // current by `OnFontScaleChanged`, so the dictionary is the honest source.
        FontScale = _scales[ViewMode.Details].Font,
        IconScale = _scales[ViewMode.Details].Icon,

        GridFontScale = _scales[ViewMode.Grid].Font,
        GridIconScale = _scales[ViewMode.Grid].Icon,
        CompactFontScale = _scales[ViewMode.Compact].Font,
        CompactIconScale = _scales[ViewMode.Compact].Icon,

        // Stacks serialise oldest-first so RestoreFrom can push in order.
        BackStack = _back.Reverse().ToList(),
        ForwardStack = _forward.Reverse().ToList(),
    };

    partial void OnShowHiddenChanged(bool value)
    {
        if (!_suppressReload) Detached(LoadAsync(CurrentPath), "load");
    }

    /// <summary>
    /// Starts work nobody is going to await, and reports what it throws.
    ///
    /// **`_ = SomeAsync()` discards the Task and with it the exception**, which
    /// then surfaces — if at all — as an unobserved-task crash long after the
    /// call that caused it. `async void` here is deliberate and safe precisely
    /// because it carries a catch; that is the rule this project already applies
    /// to its event handlers.
    /// </summary>
    private static async void Detached(Task work, string area)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Vaktari.Core.Quiet.Swallowed(area, ex);
        }
    }

    /// <summary>
    /// Flips hidden-file visibility. Exists for the keyboard route only — the
    /// settings flyout binds `ShowHidden` directly, so this must stay a plain
    /// flip with no extra behaviour, or the two paths would diverge.
    /// </summary>
    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    partial void OnIsLoadedChanged(bool value) => NotifyListingState();
    partial void OnIsLoadingChanged(bool value) => NotifyListingState();

    partial void OnSortChanged(SortField value)
    {
        NotifySortGlyphs();
        if (!_suppressReload) ResortInPlace();
    
        RememberFolderView();
    }

    /// <summary>
    /// Debounced because filtering rebuilds the visible collection, and doing
    /// that per keystroke on a 200k listing would stutter badly.
    /// </summary>
    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce?.Cancel();
        _filterDebounce?.Dispose();
        _filterDebounce = new CancellationTokenSource();
        var ct = _filterDebounce.Token;

        _ = Task.Delay(120, ct).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            Dispatcher.UIThread.Post(ApplyFilter);
        }, TaskScheduler.Default);
    }






    /// <summary>Swaps the crumbs for an editable box — Ctrl+L, or clicking the
    /// empty space beside them, exactly as Dolphin does it.</summary>
    [ObservableProperty] private bool _isPathEditing;

    private readonly PathCompleter _completer = new();


    private bool _completingPath;












    [RelayCommand] private void SortByName() => SortBy("name");
    [RelayCommand] private void SortBySize() => SortBy("size");
    [RelayCommand] private void SortByModified() => SortBy("modified");



    /// <summary>
    /// Escape pressed in the LISTING, as opposed to inside the filter box.
    ///
    /// **It closed a filter bar that was meant to stay open.** The startup
    /// setting "show the filter bar" opens it deliberately, for people who
    /// filter constantly — and any Escape in the listing took it away again,
    /// for a key people press to mean "never mind" about anything at all. The
    /// way back was a chip two levels into a menu.
    ///
    /// Closing the box is something you do TO the box, so it stays on the box's
    /// own Escape. This one clears the text and the pending cut, which are the
    /// two things Escape has always promised here.
    /// </summary>
    [RelayCommand]
    public void DismissInListing()
    {
        if (FilterText.Length > 0) FilterText = "";

        CutMarks.Clear();
    }

    [RelayCommand]
    public void ClearFilter()
    {
        if (FilterText.Length > 0) FilterText = "";
        else IsFilterVisible = false;

        // Escape also abandons a pending cut, as it does in Explorer. Last,
        // so it never costs the filter its own use of the key: whichever of
        // the two the user meant, the other is harmless.
        CutMarks.Clear();
    }

    /// <summary>
    /// Enter or Down in the filter box: hand the keyboard to the rows.
    ///
    /// **Getting out of the filter needed Tab, F6 or the mouse.** Explorer's
    /// box runs the search and moves focus to the results, and Enter in a box
    /// above a list means "I am done here" everywhere else in the desktop — so
    /// the two keys somebody presses to leave both did nothing, and the filter
    /// they had just typed sat there holding the keyboard.
    ///
    /// The filter and its text stay: this is crossing to the rows, not
    /// finishing with the filter. Escape is still what clears it.
    /// </summary>
    [RelayCommand]
    public void GoToListing()
    {
        // **The crossing was invisible, and Down crossed nothing.** The
        // binding is claimed while the box still has the keyboard, and the
        // listing is focused a dispatcher turn later, so the keystroke that
        // crossed never reaches the rows — and a filter that has just narrowed
        // the listing leaves no selection behind, because the rows it was on
        // are gone. Enter would have changed nothing on screen at all, and
        // Down, which means "go down one row", would have moved zero. The first
        // row is where both Explorer and Dolphin land.
        //
        // Only when there is nothing picked, because a selection that survived
        // the filter is the person's own and must not be thrown away.
        //
        // **And only when there is a row to pick.** FileEntry is a record
        // STRUCT, so FirstOrDefault over an empty listing hands back a
        // zero-valued one rather than null — which is not nothing, it is a row
        // with an empty name and an empty path, and everything downstream that
        // acts on a selection would have taken it for a real file.
        if (SelectedEntry is null && Entries.Count > 0) SelectedEntry = Entries[0];

        FocusListing = true;
        FocusListing = false;
    }

    /// <summary>
    /// Pulses true to put the caret in the address bar and select what is in
    /// it.
    ///
    /// A signal rather than a state, the same shape as <see cref="FocusFilter"/>
    /// and <see cref="FocusListing"/>. Only the SECOND press needs it — the
    /// first is answered by the box appearing, which is a different behaviour
    /// on the same control and stays, because the address bar closes when focus
    /// leaves it and so never re-appears on a tab switch the way the filter
    /// does.
    /// </summary>
    [ObservableProperty] private bool _focusPathBox;

    /// <summary>
    /// Pulses true to put the keyboard on the rows.
    ///
    /// A signal rather than a state, the same shape as <see cref="FocusFilter"/>
    /// and for the same reason: the focus behaviour acts on the false-to-true
    /// edge, so the gesture has to work a second time.
    ///
    /// Bound by all three listings. Only one is on screen at once, and focusing
    /// a hidden control is a no-op that fails quietly — so the visible one
    /// answers and the other two do nothing.
    /// </summary>
    [ObservableProperty] private bool _focusListing;

    [RelayCommand]
    public void ToggleFilter()
    {
        IsFilterVisible = !IsFilterVisible;
        if (!IsFilterVisible && FilterText.Length > 0) FilterText = "";

        // Asking for the box is the only thing that moves the keyboard into it.
        if (!IsFilterVisible) return;

        FocusFilter = true;
        FocusFilter = false;
    }

    /// <summary>
    /// Pulses true to put the caret in the filter box.
    ///
    /// **The box took the keyboard every time it APPEARED, and a tab switch is
    /// an appearance.** One field lives in the pane group's chrome with its
    /// visibility bound to ActiveTab.IsFilterVisible, so coming back to a tab
    /// that had the filter open flipped it from hidden to shown — and the
    /// behaviour that focuses on that edge answered it exactly as it answered
    /// Ctrl+I. An ordinary Ctrl+Tab left the arrow keys, Enter, Delete and
    /// type-ahead dead in a listing that looked ready for all four, with
    /// nothing on screen to say the keystrokes were going into a 200-pixel box
    /// up in the path bar.
    ///
    /// Focus belongs to the GESTURE, not to the appearance — the same rule the
    /// listing already follows when an editor closes.
    ///
    /// **True then false, which is the opposite order to the two pulses this
    /// otherwise copies.** Those end latched true, and they can: they bind
    /// straight off the sidebar, which does not re-point. This one binds
    /// through ActiveTab, so a value left true is pushed onto the control as a
    /// fresh false-to-true edge every time the active tab changes — which is
    /// this bug wearing a different hat. Reset in the same breath, and there is
    /// a test that counts the edges.
    /// </summary>
    [ObservableProperty] private bool _focusFilter;

    /// <summary>
    /// Rows whose names cannot be told apart by eye. Bound by every listing, so
    /// a row can mark itself — see <see cref="ConfusableNames"/> for why this
    /// exists at all.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<string> _confusable =
        new HashSet<string>(StringComparer.Ordinal);

    private void ApplyFilter()
    {
        // Through the same predicate the live watcher uses, so a file arriving
        // while a filter is up is judged by exactly the rule that built the
        // list it is joining.
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _all
            : _all.Where(MatchesFilter).ToList();

        _groupNow = DateTimeOffset.Now;

        var sorted = filtered.ToList();
        sorted.Sort(Compare);

        // Before the swap, so a row realized by ReplaceAll already has its
        // header available rather than reading a stale map.
        RecomputeGroups(sorted);

        RefreshConfusable();

        // **Typing in the filter box deselected everything.** ReplaceAll raises
        // a Reset, and the list empties its selection on one — exactly as it
        // does for a sort, which has always put it back afterwards. This
        // rebuild never did, so narrowing to the three files you had already
        // picked and then acting on them, which is most of what the box is for,
        // silently acted on nothing. Rows the filter now hides drop out of the
        // selection because Reselect only re-adds what the listing holds, and
        // that is the right answer: you cannot act on what you cannot see.
        var keep = SelectedPaths();

        Entries.ReplaceAll(sorted);

        // Between the rebuild and the reselect: the spliced listing is derived
        // from Entries, and Reselect walks whatever ends up on screen. The rows
        // inside an open folder are re-ordered here as well, because this is
        // one of the two rebuilds that can change what the order is.
        ReorderOpenFolders();
        Reproject();

        Reselect(keep);

        // Only when filtering. The plain count lives in Summary, and setting
        // both made the status bar print "36 items   36 items".
        Status = filtered.Count == _all.Count
            ? ""
            : $"filtered to {filtered.Count:N0} of {_all.Count:N0}";
    }

    partial void OnSortDescendingChanged(bool value)
    {
        NotifySortGlyphs();
        if (!_suppressReload) ResortInPlace();
    
        RememberFolderView();
    }

    private async Task LoadAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        await LoadListingAsync(path).ConfigureAwait(false);
    }

    private async Task LoadListingAsync(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        // **Normalised HERE, once, because everything downstream compares
        // against it as a string.** A path with a trailing separator is the same
        // folder and a different string, and the folder watcher decides whether
        // an event belongs on screen with
        // `Path.GetDirectoryName(change.Path) != watchedPath` — a comparison
        // GetDirectoryName can never satisfy, since it never returns a trailing
        // separator. Navigating to `C:\Users\me\Downloads\` therefore killed
        // live updates outright: a finished download, a file deleted from a
        // terminal, a rename by another program — none of it appeared until F5.
        //
        // Reachable by typing one by hand, and now reachable far more easily:
        // Tab-completion ends every offer with a separator, and Tab only
        // started working on Windows paths in this same change.
        //
        // Normalising the argument rather than the property, because the same
        // string is handed to StartWatching and to the version-control refresh
        // further down — fixing only CurrentPath would leave those two comparing
        // a normalised value against a raw one, which is the same bug moved.
        path = VirtualPaths.IsVirtual(path)
            ? path
            : Vaktari.Core.FileSystem.PathRules.Normalise(path);

        // **Nothing captured the selection before the rows were thrown away.**
        // A few lines down this clears _all and resets Entries, and the list
        // empties its own selection the moment that Reset arrives — so by the
        // time the finished listing reached ResortInPlace, whose Reselect is
        // what keeps a selection across a sort, there was nothing left to read.
        // F5 lost your place in a long folder, and so did every refresh a
        // rename, a paste, a delete or an undo fires afterwards.
        //
        // Taken here rather than lower down for two reasons: the old rows are
        // still standing, and ApplyFolderView has not yet run — it can change
        // View, and SelectedEntries answers whichever layout is current, so a
        // capture after it would read the wrong collection.
        //
        // Only when staying put. Somewhere else has no rows in common, and two
        // listings DO carry paths from elsewhere: the bin's rows hold the path
        // a file used to occupy, and a search result holds one from anywhere on
        // the machine — so a path carried into either of those would match and
        // light up a row nobody picked.
        //
        // Undo and redo reach this from a pool thread (they await the refresh
        // with ConfigureAwait(false)), so this read is off the UI thread there.
        // It only reads, and the clear a few lines down has always run in the
        // same place, so it is no worse than what shipped before.
        List<string> carry = VirtualPaths.SamePlace(CurrentPath, path) ? SelectedPaths() : [];

        // Whatever an operation has asked for by name joins them: the row it
        // means does not exist yet, and the one it replaces is already stale.
        carry.AddRange(_selectAfterLoad);
        _selectAfterLoad = [];

        // Cancelling the previous navigation is what stops a dead network path
        // from wedging the pane. It is not an optimisation.
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var setupWatch = Stopwatch.StartNew();

        var generation = ++_generation;

        // Before CurrentPath moves, and guarded so the property setters this
        // triggers do not immediately write the folder's own state back at it.
        // BOTH flags. _restoringView stops the change hooks writing the folder's
        // own state straight back at it; _suppressReload is the codebase's
        // existing guard — without it, setting Sort here fires ResortInPlace and
        // setting GroupBy fires ApplyFilter, both against the PREVIOUS folder's
        // entries, mid-load. RestoreFrom has always used it for the same reason.
        _restoringView = true;
        _suppressReload = true;
        try { ApplyFolderView(path); }
        finally
        {
            _suppressReload = false;
            _restoringView = false;
        }

        // **The filter followed you into the next folder.** Type "report" to
        // find something, open a folder from the results, and the new folder
        // came up filtered by a word that has nothing to do with it — reading
        // as an empty folder. Explorer and Dolphin both drop the filter when
        // you leave. Cleared before the load so nothing renders through it.
        if (!VirtualPaths.SamePlace(CurrentPath, path))
        {
            FilterText = "";

            // **And a raised cap would have followed you too.** Keep looking
            // belongs to the question that was cut off; carried into the next
            // one it would quietly make an unrelated search twice as expensive,
            // and more with every press, with nothing on screen saying why.
            // SamePlace rather than equality, so a refresh — which is what Keep
            // looking performs — keeps the budget it just raised.
            SearchLimit = SearchListing.Limit;

            // And so does any folder opened in place. Under the same test as
            // the two above and as the selection carry, for the reason
            // ClearExpansion spells out: a refresh is the same folder and has
            // to keep the tree, and a rename, a paste, a delete and an undo all
            // end in one.
            ClearExpansion();
        }

        CurrentPath = path;
        PathText = path;
        IsLoading = true;

        // **Cleared here, or a failed load leaves the last SUCCESSFUL one's
        // answer standing.** IsLoaded is only ever set true, at the end of a
        // load that worked, so after a failure it still said yes — and two
        // guards read it. Navigating again to the same path returned early as
        // "already there", so plugging the drive in and retyping the path did
        // nothing at all and the only way forward was to visit some other
        // folder first. The same stale yes recorded the dead path in Recent
        // locations, which is exactly what that check exists to prevent.
        IsLoaded = false;
        LoadError = "";

        // Cleared with the rest of the previous answer, next to LoadError and
        // for the same reason: a reload that finds fewer than the cap must not
        // leave the last run's "there are more" standing over it. Stop is why
        // this cannot wait for the completion block — it ends a listing
        // without going through one.
        SearchHitLimit = false;

        _all.Clear();
        Entries.Reset();
        NotifyNavigationState();

        var phaseSetup = setupWatch.ElapsedMilliseconds;

        var options = new ListingOptions { IncludeHidden = ShowHidden, BatchSize = 500 };

        // The ONE branch that makes a recent listing possible. Both sources are
        // the same IAsyncEnumerable shape, so everything below — batching, the
        // generation guard, sorting, filtering, the status line — runs
        // unchanged and knows nothing about where the rows came from.
        // Written from the pool by the listing below and read on the dispatcher
        // when the load finishes; the await between them is what orders the
        // two. A property the band binds to cannot be raised from the pool, so
        // the notice lands in a local first.
        var capped = false;

        var source =
            VirtualPaths.IsRecent(path) ? RecentListing.EnumerateAsync(Recents, path, ct)
            : path == VirtualPaths.Trash ? RecentListing.EnumerateTrashAsync(Trash, ct)
            : path == VirtualPaths.Computer ? ComputerListing.EnumerateAsync(Places, ct)
            : VirtualPaths.IsSearch(path)
                ? SearchListing.EnumerateAsync(
                    Search, path, options, ct, SearchLimit, () => capped = true)
            : _fs.EnumerateAsync(path, options, ct);

        var sw = Stopwatch.StartNew();
        var sinceFlush = Stopwatch.StartNew();
        var pending = new List<FileEntry>(4096);
        var count = 0;

        try
        {
            await foreach (var batch in source.ConfigureAwait(false))
            {
                pending.AddRange(batch);

                if (sinceFlush.ElapsedMilliseconds < FlushIntervalMs) continue;

                var flush = pending;
                pending = new List<FileEntry>(4096);
                sinceFlush.Restart();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Cancelling the token does NOT unqueue a dispatcher
                    // callback that is already on its way. Without this check a
                    // superseded enumeration appends its batch into the list the
                    // newer navigation just cleared — which is the flash of
                    // wrong files you get from clicking a place twice.
                    if (generation != _generation) return;

                    _all.AddRange(flush);
                    Entries.AddRange(flush);
                    count += flush.Count;
                    Status = $"{count:N0} items…";
                });
            }

            if (pending.Count > 0)
            {
                var tail = pending;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (generation != _generation) return;

                    _all.AddRange(tail);
                    Entries.AddRange(tail);
                    count += tail.Count;
                });
            }

            var enumerateMs = sw.ElapsedMilliseconds;

            // Sorting happens once, after enumeration, rather than per batch.
            // Entries appear in readdir order while loading and settle when the
            // listing completes — which keeps first paint at a few milliseconds.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The worst one to miss: a superseded run reaching here would
                // point the watcher at the folder it was loading, clear
                // IsLoading for a navigation still in flight, and sort a list
                // that now belongs to somewhere else.
                if (generation != _generation) return;

                // Through the filter when there is one, which the resort now
                // does for itself — a listing that settles while a filter is up
                // must settle filtered.
                ResortInPlace();

                // After the sort, not before it: Reselect walks Entries, and
                // the rows it walks have to be the ones that will still be
                // there. The resort above rebuilds the collection whichever
                // route it takes, and restores only what it was holding when
                // it started — which, on a reload, is nothing.
                Reselect(carry);

                // A reload of the same folder keeps whatever was opened in
                // place, and this is what re-reads it: the watcher watches
                // CurrentPath and nothing below it, so a refresh is the only
                // moment an open subfolder can learn that something inside it
                // changed. Fire-and-forget with the generation captured, like
                // the watcher's own stat pass; it does nothing at all when
                // nothing is open, which is the ordinary case.
                _ = ReloadExpandedAsync(generation);

                // Nothing to watch: there is no directory behind a recent
                // listing. Skipped explicitly rather than left to fail inside
                // StartWatching's catch, because a silently swallowed failure
                // is exactly the kind of thing that reads as working.
                if (!VirtualPaths.IsVirtual(path)) StartWatching(path);
                sw.Stop();

                // Cleared, NOT set to the count. Summary already shows
                // "36 items" and Status sat beside it showing the same thing,
                // so the status bar read "36 items   36 items". Status is for
                // messages; the count has an owner and this is not it.
                Status = "";
                IsLoading = false;
                IsLoaded = true;

                // **This is the sentence the walk never said.** Set here rather
                // than where the truncation was noticed: it is noticed on the
                // pool, and the band binds to this.
                SearchHitLimit = capped;

                // AFTER the listing is on screen, never before it. Status can
                // take seconds on a large repository and the folder must not
                // wait on it — decorations arriving late is the correct
                // trade-off, a listing that stalls is not.
                // Off the dispatcher, not merely un-awaited. An async method
                // runs synchronously up to its first await, and the stretch
                // before this one's is not free: git status walks parent
                // directories looking for .git and then STARTS A PROCESS, both
                // on the thread that has just finished drawing the listing.
                // "After the listing is on screen" was already the intent; this
                // is what makes it true rather than nearly true.
                var vcsToken = _cts?.Token ?? default;
                _ = Task.Run(() => RefreshVcsAsync(path, generation, vcsToken));

                // Says what the listing actually produced. "No files showing"
                // has two very different causes — nothing enumerated, or
                // nothing rendered — and only this separates them.
                // Timing stays — it is how a 44-second stall was found at
                // all. Heap, GC and thread-pool counters were for that hunt
                // specifically and are noise in daily use, so they need asking
                // for: VAKTARI_LOAD_DEBUG=1.
                //
                // The three phases SUM to the total; sw starts after setup is
                // captured, so they are separate clocks, not slices of one.
                var detail = Environment.GetEnvironmentVariable("VAKTARI_LOAD_DEBUG") == "1"
                    ? $"heap {GC.GetTotalMemory(false) / (1024 * 1024)} MiB "
                      + $"gc {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)} "
                      + $"pool {ThreadPool.ThreadCount}t/{ThreadPool.PendingWorkItemCount}q "
                    : "";

                Console.Error.WriteLine(
                    $"[vaktari] listing: {Entries.Count:N0} of {_all.Count:N0} "
                    + $"in {phaseSetup + sw.ElapsedMilliseconds} ms "
                    + $"(setup {phaseSetup} · enumerate {enumerateMs} · "
                    + $"finish {sw.ElapsedMilliseconds - enumerateMs}) "
                    + detail
                    + $"· {View} · {path}");
            });
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer navigation; the newer one owns the status.
        }
        catch (Exception ex)
        {
            // Dead paths stay visible with an explanation rather than being
            // dropped or silently redirected — silently dropping a restored tab
            // is what "it forgot" feels like.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A failure in a navigation nobody is waiting for any more is
                // not worth reporting over the one they are.
                if (generation != _generation) return;

                // **One sentence, said in both places.** The status bar used
                // to report the exception's type — a fact about the code, not
                // about the folder — while the listing behind it, from this
                // very block, said something a person could act on.
                //
                // Said in the listing as well as the status bar because the bar
                // describes the ACTIVE pane, so the other half of a split would
                // otherwise report nothing whatsoever.
                LoadError = Failures.Describe(ex, "open that folder");
                Status = LoadError;

                IsLoading = false;
            });
        }
    }










    /// <summary>
    /// Whether a row survives the filter.
    ///
    /// **A pattern is used when it looks like one.** The same "*.png" works in
    /// the search box and hid everything here, because this only ever asked
    /// whether the name CONTAINED the text — and no name contains an asterisk.
    /// Dolphin offers plain, glob and regex as modes; a filter that simply
    /// notices the wildcard needs no mode and no extra control.
    /// </summary>
    private bool MatchesFilter(FileEntry entry)
    {
        if (string.IsNullOrWhiteSpace(FilterText)) return true;

        return LooksLikeAPattern(FilterText)
            ? System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(FilterText, entry.Name,
                                                     ignoreCase: true)
            : entry.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeAPattern(string text)
        => text.Contains('*', StringComparison.Ordinal)
           || text.Contains('?', StringComparison.Ordinal);

    /// <summary>Binary search for the insertion point under the current sort,
    /// so a new file lands where it belongs instead of forcing a re-sort.</summary>
    private int FindSortedIndex(IList<FileEntry> list, FileEntry entry)
    {
        int low = 0, high = list.Count;

        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Compare(list[mid], entry) < 0) low = mid + 1;
            else high = mid;
        }

        return low;
    }

    /// <summary>
    /// Only says something when a filter is actually hiding rows, because that
    /// is the part Summary cannot express — Summary counts what is on screen
    /// and has no way to say "out of how many". With no filter there is nothing
    /// to add, so it says nothing rather than repeating the count.
    /// </summary>
    private void UpdateCountStatus()
        => Status = Entries.Count == _all.Count
            ? ""
            : $"{Entries.Count:N0} of {_all.Count:N0} items";

    /// <summary>
    /// Re-sorts the rows already on screen, without going back to the disk.
    ///
    /// **Through the filter, because the filter is part of what is on screen.**
    /// This rebuilt Entries from `_all`, the UNFILTERED master list, so
    /// clicking a column heading — or clicking it again to reverse — in a
    /// filtered folder put every hidden row back, with the filter box still
    /// holding the words that were meant to be hiding them.
    ///
    /// The rule lives here rather than at the call sites because five places
    /// ask for a resort and only two of them had written the check out for
    /// themselves: OnViewChanged and the load path. The other three —
    /// OnSortChanged, OnSortDescendingChanged and SortBy — called it bare, and
    /// three callers out of five getting it wrong is a method with the wrong
    /// contract rather than three separate mistakes.
    ///
    /// Ahead of the empty-listing guard rather than after it, so a bare call
    /// behind a filter is the same thing as `ApplyFilter()` and the two call
    /// sites that spelled the pair out can drop it without a behaviour
    /// question. The order is visible, not merely tidy. A filter matching
    /// nothing leaves Entries empty beside a full `_all`, and widening it does
    /// not refill the listing for 120 ms — the box is debounced. A heading
    /// clicked inside that window arrives here with an empty listing and a
    /// filter that now matches two rows; behind the guard the click did
    /// nothing at all.
    /// </summary>
    private void ResortInPlace()
    {
        if (FilterText.Length > 0)
        {
            ApplyFilter();
            return;
        }

        if (Entries.Count == 0) return;

        _groupNow = DateTimeOffset.Now;

        var keep = SelectedPaths();

        var items = _all.Count > 0 ? _all.ToList() : Entries.ToList();
        items.Sort(Compare);

        // The TOP LEVEL's order, before the splice: a row inside a folder
        // opened in place is not part of a band, so it must neither carry a
        // heading nor break the run it sits in the middle of.
        RecomputeGroups(items);
        Entries.ReplaceAll(items);

        // The other rebuild that changes the order, so the rows inside an open
        // folder turn over with the listing around them.
        ReorderOpenFolders();
        Reproject();

        Reselect(keep);
        RefreshConfusable();
    }

    /// <summary>What is selected, by path, so it can survive the rows being
    /// replaced by equal-but-different ones.</summary>
    /// <summary>
    /// **A focused row with nothing in the selection counted as nothing.** In a
    /// real list the two cannot come apart — SelectedItem and SelectedItems sit
    /// behind one selection model, so the binding that sets SelectedEntry fills
    /// DetailsSelection with it — but this view model is written to be driven
    /// without one, and the places that set the focused row on its own
    /// (ApplyBatch after a delete, a restored session, a test) would hand a
    /// rebuild nothing to keep. Belt and braces: the collection is still the
    /// answer whenever it has one.
    /// </summary>
    private List<string> SelectedPaths()
    {
        var paths = SelectedEntries.Select(e => e.FullPath).OfType<string>().ToList();

        if (paths.Count == 0 && SelectedEntry?.FullPath is { } focused) paths.Add(focused);

        return paths;
    }

    /// <summary>
    /// Puts the selection back after the rows have been rebuilt.
    ///
    /// **ReplaceAll clears the collection the view binds its selection to**, so
    /// every rebuild dropped it: F5 lost your place in a long folder, and
    /// sorting or filtering with files selected quietly deselected them.
    /// Explorer and Dolphin both keep the selection across all three.
    ///
    /// By path rather than by value: FileEntry is a record struct carrying a
    /// timestamp and a length, so the row for a file that changed while the
    /// listing was open is not equal to the one that was selected.
    /// </summary>
    private void Reselect(List<string> paths)
    {
        if (paths.Count == 0) return;

        var wanted = new HashSet<string>(paths, StringComparer.Ordinal);
        var selection = SelectedEntries;

        selection.Clear();

        // The rows on SCREEN rather than the folder's own, or a selected row
        // inside a folder opened in place would be dropped by every rebuild —
        // and a rebuild is what a sort, a filter and a refresh all are. The
        // same object as Entries whenever nothing is expanded.
        foreach (var entry in VisibleRows)
            if (entry.FullPath is { } path && wanted.Contains(path))
                selection.Add(entry);

        // The focused row too, or the keyboard would carry on from wherever it
        // happened to be rather than from what is selected.
        if (selection.Count > 0) SelectedEntry = selection[0];
    }

    /// <summary>
    /// Paths for the next load to select, put there by an operation that knows
    /// what it has just made.
    ///
    /// **A rename came back with nothing selected, including the file you had
    /// renamed.** The refresh that follows one rebuilds the listing from the
    /// file system, and the path that was selected went with the old name — so
    /// carrying the selection over, which is all a reload can do on its own,
    /// restores a row that is not there any more. The new name is known only to
    /// the rename.
    ///
    /// A path rather than a row, because the row does not exist yet: it arrives
    /// with the listing, out of the provider, carrying a length and a timestamp
    /// this side has never seen. The same reason Reselect works in paths, and
    /// the same reason the deletion counterpart _selectAfterRemoval does.
    /// </summary>
    private List<string> _selectAfterLoad = [];

    /// <summary>
    /// Asks the next load to select this path if the listing has it.
    ///
    /// Read and cleared by that load whether or not it found anything — a
    /// request older than that belongs to a listing that has gone.
    ///
    /// Public because the caller that knows the new name is the prompt, not
    /// this file: the rename engine is handed a name and never sees the
    /// gesture, and only the gesture knows whether the keyboard is staying on
    /// the file or moving to the next one.
    /// </summary>
    public void SelectAfterLoad(string path) => _selectAfterLoad.Add(path);

    /// <summary>
    /// Recomputes which rows cannot be told apart by eye.
    ///
    /// **Assigned here and nowhere inline, because it lived only in ApplyFilter
    /// and an ordinary navigation never goes there** — a plain folder load
    /// takes ResortInPlace, so the set stayed at its empty initial value and
    /// the look-alike mark never rendered for anybody, on any view, since the
    /// day it shipped. The unit tests all passed: they tested the set, and the
    /// set was right — nothing asked whether a LISTING ever received it.
    ///
    /// Over the WHOLE folder rather than the filtered view: two names collide
    /// whether or not a filter happens to be showing both.
    ///
    /// **The string the row DRAWS, not the file name.** Keyed on the file name
    /// this could never fire twice in one folder for two launchers, because
    /// file names in a directory are unique — and once a .desktop row started
    /// drawing its Name= key, org.kde.dolphin.desktop and dolphin.desktop both
    /// rendered the single word "Dolphin" with nothing to tell them apart and
    /// no mark. That is the exact thing this set exists to say.
    ///
    /// Costs the launcher read for every .desktop in the folder rather than for
    /// the visible ones — 97 µs each, once per path per process, and 0.1 µs on
    /// every later pass because the answer is cached under it.
    /// </summary>
    private void RefreshConfusable()
        => Confusable = ConfusableNames.Among(_all.Select(e => (e.FullPath, FileKind.DisplayName(e))));



    [RelayCommand] private void GroupByNone() => GroupBy = GroupMode.None;
    [RelayCommand] private void GroupByName() => GroupBy = GroupMode.Name;
    [RelayCommand] private void GroupBySize() => GroupBy = GroupMode.Size;
    [RelayCommand] private void GroupByModified() => GroupBy = GroupMode.Modified;
    [RelayCommand] private void GroupByKind() => GroupBy = GroupMode.Kind;


    /// <summary>
    /// The header a row should carry, or null. Computed once per rebuild rather
    /// than per row: a row cannot see its predecessor, and asking each one to
    /// work it out would be O(n) lookups on every realization.
    /// </summary>
    private readonly Dictionary<string, GroupHeader> _groupHeaders = new(StringComparer.Ordinal);

    // Captured once per sort: asking for the time inside a comparison would
    // make the ordering depend on when each comparison happened.
    private DateTimeOffset _groupNow = DateTimeOffset.Now;


    /// <summary>Raised when headers change, so realized rows re-read them.</summary>
    public event EventHandler? GroupingChanged;

    /// <summary>
    /// Takes a read-only list rather than a <c>List</c>, so the live watcher
    /// can hand it <c>Entries</c> itself.
    ///
    /// **It could only be given a List, so the watcher copied one per event.**
    /// `RecomputeGroups(Entries.ToList())` ran on both halves of every change —
    /// a 100k-element array allocated and thrown away for one file arriving.
    /// This only ever reads the order it is given.
    /// </summary>
    private void RecomputeGroups(IReadOnlyList<FileEntry> ordered)
    {
        _groupHeaders.Clear();

        // The LISTING's grouping rather than the pane's — see EffectiveGroupBy.
        // A layout that cannot draw a heading must not be left holding a map of
        // them either: the map is what a row asks, and a stale one is how a
        // heading outlives the layout it belonged to.
        var grouping = EffectiveGroupBy;

        if (grouping != GroupMode.None)
        {
            // Only the first row of a run carries the header; the rest are
            // plain, which is what makes it read as a group rather than a
            // repeated label.
            //
            // **The run's LENGTH is only knowable at its end**, which is why
            // this indexes rather than walking entries: the header is written
            // when the next label arrives, and the count is the distance back
            // to where the run started. Writing it on the first row instead
            // would have meant a second pass or a mutable header.
            string? previous = null;
            var start = 0;

            for (var i = 0; i < ordered.Count; i++)
            {
                var label = Grouping.Label(ordered[i], grouping, _groupNow);

                if (label == previous) continue;

                if (previous is not null)
                    _groupHeaders[ordered[start].FullPath] = new GroupHeader(previous, i - start);

                previous = label;
                start = i;
            }

            // The last run has no successor to close it.
            if (previous is not null)
                _groupHeaders[ordered[start].FullPath] =
                    new GroupHeader(previous, ordered.Count - start);
        }

        GroupingChanged?.Invoke(this, EventArgs.Empty);
    }


    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));

        // The menus behind the two chevrons are built from the same stacks.
        NotifyHistory();
    }

    public void Dispose()
    {
        _vcsRefresh?.Stop();

        // **Every timer, or a tick lands on a pane that is gone.** The settle
        // timer runs 200 ms after the last watcher event, so closing a tab
        // while files were still arriving left one pending on a pane whose
        // watcher and token source had already been torn down. In the tests it
        // is worse than that: a tick can arrive after the headless session that
        // created it has ended, which surfaces as "the calling thread cannot
        // access this object" in the cleanup of whatever test ran next.
        _settle?.Stop();
        _repoWatcher?.Dispose();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        _watcher?.Dispose();
        _watcher = null;
    }
}
