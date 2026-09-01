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

    public IEnumerable<FileEntry> DetailsEntries
        => View == ViewMode.Details ? Entries : NoEntries;

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
    /// Lazily for a second reason: no ordinary right-click should pay for
    /// something that lives behind one more hover.
    /// </summary>
    public async Task OpenShellMenuAsync()
    {
        if (ShellMenu is not { } provider) return;

        // The selection, or the folder when the click was on empty space — the
        // same rule the rest of the menu follows.
        var paths = SelectionPaths();
        if (paths.Count == 0) paths = [CurrentPath];

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

        var menu = await Task.Run(() => provider.Build(paths)).ConfigureAwait(false);

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
            ShellMenuItems.Clear();

            foreach (var item in Flatten(menu?.Entries ?? [])) ShellMenuItems.Add(item);

            // Never empty: an empty ItemsSource closes the submenu out from
            // under the pointer, and "nothing" reads as a fault rather than as
            // an answer.
            if (ShellMenuItems.Count == 0)
                ShellMenuItems.Add(new Vaktari.Core.FileSystem.ShellMenuEntry(
                    "Nothing offered here", -1, IsEnabled: false));
        });
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

        ShellMenuItems.Clear();
        ShellMenuItems.Add(Waiting());

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
    /// **Only for things Windows can actually start elevated.** The runas verb
    /// on a .txt does nothing at all — no error, no elevation, no editor — so
    /// offering it for every file would be an entry that silently fails on most
    /// of them. This is the set Explorer itself offers it for.
    /// </summary>
    /// <summary>
    /// **No longer behind Shift.** Explorer shows "Run as administrator" for
    /// every executable on a plain right-click; only its EXTENDED verbs hide
    /// behind Shift. Copying the gate onto this entry meant an ordinary
    /// right-click on an .exe showed no elevation at all, and the person went
    /// hunting through submenus for something that looked buried — because it
    /// was. The admin TERMINAL keeps the Shift gate: that one is an extended
    /// verb by Explorer's own convention too.
    /// </summary>
    public bool CanRunSelectionAsAdministrator =>
        _launcher?.CanElevate == true
        && !IsTrashListing && !IsRecentListing
        && SelectedEntry is { IsDirectory: false } entry
        && Executable.Contains(Path.GetExtension(entry.FullPath));

    private static readonly HashSet<string> Executable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe", ".msi", ".bat", ".cmd", ".ps1", ".com", ".lnk", ".msc", ".vbs", ".reg",
        };

    /// <summary>
    /// Hands the selection to the system to start elevated. The consent dialog
    /// is the system's, and Vaktari itself stays unelevated whatever is chosen.
    /// </summary>
    [RelayCommand]
    public void RunAsAdministrator()
    {
        if (!CanRunSelectionAsAdministrator || SelectedEntry is not { } entry) return;

        _launcher?.OpenElevated(entry.FullPath);
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
        OnPropertyChanged(nameof(HasDirectorySelected));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));
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

    /// <summary>Raised when an operation starts, so the shell can show progress.</summary>
    public event EventHandler<IOperationHandle>? OperationStarted;

    /// <summary>Raised when a rename is requested, so the view can prompt.</summary>
    public event EventHandler<FileEntry>? RenameRequested;

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
    [ObservableProperty] private SortField _sort = SortField.Name;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private ViewMode _view = ViewMode.Details;

    /// <summary>Highlights the pane a drop would land in.</summary>
    [ObservableProperty] private bool _isDropTarget;

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
    private const double BaseIconSize = 26;

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

    public bool ShowSize => ViewportWidth >= 340 * TextScale;
    public bool ShowModified => ViewportWidth >= 520 * TextScale;
    public bool ShowPermissions => ViewportWidth >= 680 * TextScale;
    public bool ShowMetadata =>
        ViewportWidth >= 840 * TextScale && !IsRecentListing && !IsTrashListing;

    /// <summary>
    /// True only in the two recent listings, where the rows come from a store
    /// rather than a directory.
    /// </summary>
    public bool IsRecentListing => VirtualPaths.IsRecent(CurrentPath);

    /// <summary>True in the trash listing, which gates restore and empty.</summary>
    public bool IsTrashListing => CurrentPath == VirtualPaths.Trash;

    /// <summary>
    /// The parent folder of each row, shown ONLY in a recent listing — and not
    /// optional there: those entries span the whole filesystem, so a bare
    /// filename says nothing about which of four `config.toml` files you are
    /// looking at.
    ///
    /// Shares column 2 with the metadata column rather than adding a seventh:
    /// the two are mutually exclusive by construction (ShowMetadata is false
    /// here), and inserting a column would renumber every element after it in
    /// two separate grids — the kind of edit that goes wrong quietly.
    /// </summary>
    public bool ShowParentPath =>
        (IsRecentListing || IsTrashListing) && ViewportWidth >= 420 * TextScale;

    partial void OnTextScaleChanged(double value) => NotifyColumns();

    private void NotifyColumns()
    {
        OnPropertyChanged(nameof(ShowSize));
        OnPropertyChanged(nameof(ShowModified));
        OnPropertyChanged(nameof(ShowPermissions));
        OnPropertyChanged(nameof(ShowMetadata));
        OnPropertyChanged(nameof(ShowParentPath));
    }

    partial void OnViewportWidthChanged(double value) => NotifyColumns();

    [ObservableProperty] private string _previewTitle = "";
    [ObservableProperty] private string _previewDetail = "";


    private CancellationTokenSource? _previewCts;







    /// <summary>An empty listing used to look identical to one still loading.</summary>
    public bool IsEmpty => IsLoaded && !IsLoading && Entries.Count == 0 && !HasLoadError;

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

        return files == 0 ? "" : $" ({ByteSize.Format(total)})";
    }

    public string Summary => Selection.Count switch
    {
        0 => $"{Entries.Count:N0} items",
        1 => $"{Entries.Count:N0} items · 1 selected{SelectionSize()}",
        var n => $"{Entries.Count:N0} items · {n:N0} selected{SelectionSize()}",
    };

    private void NotifyListingState()
    {
        OnPropertyChanged(nameof(CanUseTileLayouts));
        OnPropertyChanged(nameof(CanUseGrid));
        OnPropertyChanged(nameof(CanUseCompact));

        // The drop-back to list view lived here: entering a folder past the
        // compact limit switched layout and said so. Both tile layouts virtualize
        // now, so there is nothing left to rescue anyone from.

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ShareTargetLabel));
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

        // Populate the incoming layout FIRST. Its ListBox cannot hold a
        // selection for items it does not yet have, so carrying the selection
        // before the items exist would silently drop it.
        NotifyLayoutEntries();

        CarrySelection(oldValue, newValue);

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
                           && !string.IsNullOrEmpty(_fs.GetParent(CurrentPath));

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
        // PathRules.Same, not Ordinal: CurrentPath is normalised on load, so a
        // navigation spelled with a trailing separator or the other case is the
        // same place — compared ordinally it reloaded anyway AND pushed a
        // history entry whose Back went nowhere.
        if (IsLoaded && !IsLoading && PathRules.Same(CurrentPath, path))
            return;

        if (!string.IsNullOrEmpty(CurrentPath) && !PathRules.Same(CurrentPath, path))
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
            Recents?.Record(path, RecentKind.Folder);
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

        if (_fs.GetParent(CurrentPath) is { Length: > 0 } parent)
            await NavigateAsync(parent).ConfigureAwait(false);
    }

    [RelayCommand]
    public Task OpenAsync(FileEntry entry)
    {
        if (entry.IsDirectory) return NavigateAsync(entry.FullPath);

        // Recorded on the ATTEMPT, not on success: IApplicationLauncher.Open
        // returns void, so there is nothing to test. Asking to open a file is
        // the user's act either way, which is the recency semantic that matters
        // — and a file with no handler is rare next to the cost of pretending
        // to know whether the launch worked.
        Recents?.Record(entry.FullPath, RecentKind.File);

        _launcher?.Open(entry.FullPath);
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

    /// <summary>
    /// Puts the selected trashed items back.
    ///
    /// The listing shows ORIGINAL paths, and Restore needs the trash KEY, so
    /// the mapping is looked up from the store rather than derived from the
    /// name — a deduplicated key like `notes.3.txt` cannot be reversed into
    /// `notes.txt` reliably, and guessing would restore the wrong file.
    /// </summary>
    [RelayCommand]
    private async Task RestoreFromTrashAsync()
    {
        if (Trash is null || !IsTrashListing) return;

        var wanted = SelectionPaths().ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0) return;

        var restored = 0;
        var failed = 0;

        foreach (var item in Trash.List())
        {
            if (!wanted.Contains(item.OriginalPath)) continue;

            try
            {
                Trash.Restore(item.TrashName);
                restored++;
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
        Status = (restored, failed) switch
        {
            (0, 0) => "nothing restored",
            (0, _) => $"could not restore {failed:N0} item(s) — see the log",
            (_, 0) => $"restored {restored:N0} item(s)",
            _ => $"restored {restored:N0}, {failed:N0} failed",
        };

        await RefreshAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Permanently deletes everything in the trash. **Always confirmed by the
    /// caller** — this is the one action in the application with no undo and no
    /// per-item review, so the prompt is not a preference the way trashing is.
    /// </summary>
    public async Task EmptyTrashAsync()
    {
        if (Trash is null) return;

        try
        {
            var result = await Trash.EmptyAsync(CancellationToken.None).ConfigureAwait(false);

            // On the UI thread. ConfigureAwait(false) above means this
            // continuation is on a pool thread, and Status raises
            // PropertyChanged straight into a binding — every other status
            // written after an await in this class goes through the dispatcher
            // for that reason, and this one did not.
            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = $"emptied {Core.Naming.BinName} — removed {result.Removed:N0}, "
                       + $"freed {ByteSize.Format(result.BytesFreed)}");
        }
        catch (Exception ex)
        {
            // **A failure here was completely silent.** Emptying is the one
            // action with no undo, so "did it work?" is a question people
            // actually ask — and a file the shell still has open, or a
            // permission the recycle bin will not give up, left the items in
            // place, the status line blank, and the listing unchanged. Nothing
            // to distinguish that from an empty bin.
            await Dispatcher.UIThread.InvokeAsync(() =>
                Status = $"could not empty {Core.Naming.TheBin}: {ex.Message}");

            Console.Error.WriteLine($"[vaktari] empty failed: {ex}");
        }

        // Outside the try: whatever happened, some of it may have gone, and a
        // listing still showing deleted rows is worse than one that is late.
        if (IsTrashListing) await RefreshAsync().ConfigureAwait(false);
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

    private void Track(IOperationHandle handle)
    {
        OperationStarted?.Invoke(this, handle);

        // The listing is refreshed once, at the end — refreshing per item would
        // rebuild the view thousands of times during a large copy.
        _ = handle.Completion.ContinueWith(
            _ => Dispatcher.UIThread.Post(() => _ = RefreshAsync()),
            TaskScheduler.Default);
    }

    partial void OnSelectedEntryChanged(FileEntry? value)
    {
        // The focused row counts as a selection on its own — a right-click sets
        // it before the menu opens, and on a single-click it is all there is.
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanActOnSelection));
        OnPropertyChanged(nameof(HasDirectorySelected));
        OnPropertyChanged(nameof(CanRunSelectionAsAdministrator));
        OnPropertyChanged(nameof(CanMountSelection));
        OnPropertyChanged(nameof(CanUnmountSelection));

        if (IsPreviewVisible) _ = RefreshPreviewAsync();

        OpenWithOptions.Clear();
        if (_launcher is null || value is not { IsDirectory: false } entry) return;

        // Enumeration shells out to xdg-mime, so keep it off the UI thread.
        var path = entry.FullPath;
        _ = Task.Run(() =>
        {
            var options = _launcher.GetOpenWithOptions(path);
            Dispatcher.UIThread.Post(() =>
            {
                var wanted = new List<LaunchOption>(options);

                // Last, and only where the desktop has a chooser to show. The
                // installed applications are the answer most of the time; this
                // is the way out when none of them is, and it belongs at the
                // bottom of the list it escapes from.
                if (_launcher.CanChooseApplication)
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
            });
        });
    }

    [RelayCommand]
    public void OpenWithApp(LaunchOption? option)
    {
        if (option is null || SelectedEntry is not { } entry) return;

        // The chooser opens the file itself once something is picked, so it
        // records the same way — and is checked BEFORE the recent entry,
        // because a cancelled chooser opened nothing and must not claim to.
        if (option.IsChooser)
        {
            if (_launcher?.ChooseApplication(entry.FullPath) is true)
                Recents?.Record(entry.FullPath, RecentKind.File);

            return;
        }

        // Same act as OpenAsync, so it belongs in the recent list too. Missing
        // this would make the list quietly depend on WHICH way you opened
        // something, which nobody would guess from the UI.
        Recents?.Record(entry.FullPath, RecentKind.File);

        _launcher?.OpenWith(entry.FullPath, option);
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

    /// <summary>F4 and the plain entry: the chosen terminal.</summary>
    [RelayCommand]
    public void OpenTerminalHere()
    {
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
        if (terminal is null) return;

        _launcher?.OpenTerminal(CurrentPath, terminal);
    }












    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(CurrentPath);

    [RelayCommand]
    public Task OpenSelectedAsync()
        => SelectedEntry is { } entry ? OpenAsync(entry) : Task.CompletedTask;








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
            OnPropertyChanged(nameof(Terminals));
            OnPropertyChanged(nameof(HasSeveralTerminals));
            OnPropertyChanged(nameof(CanActOnSelection));
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

        // LeafName gives the root back as itself, so the "/" fallback is no
        // longer a Linux-shaped guess about what a root looks like.
        Title = PathRules.LeafName(value);
    }

    /// <summary>
    /// Restored tabs enumerate only when first activated. Recreating twenty
    /// tabs eagerly means twenty listings at startup, and one of them sitting
    /// on an unreachable share costs the whole window its SMB timeout.
    /// </summary>
    partial void OnIsActiveChanged(bool value)
    {
        if (value && !IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            Detached(LoadAsync(CurrentPath), "load");
    }

    /// <summary>
    /// Adopt persisted state without touching the filesystem. ShowHidden is set
    /// under suppression because its change handler triggers a reload, which is
    /// exactly what lazy restore is trying to avoid.
    /// </summary>
    public void RestoreFrom(TabState tab)
    {
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

            _back.Clear();
            foreach (var p in tab.BackStack) _back.Push(p);

            _forward.Clear();
            foreach (var p in tab.ForwardStack) _forward.Push(p);
        }
        finally
        {
            _suppressReload = false;
        }

        IsLoaded = false;
        Status = "not loaded";
        NotifyNavigationState();
    }

    /// <summary>
    /// Load now if the pane was restored but never activated into a load.
    /// Start() assigns ActiveTab while change notifications are suppressed, so
    /// the usual activate-triggers-load path doesn't fire for it.
    /// </summary>
    public void RefreshIfUnloaded()
    {
        if (!IsLoaded && !IsLoading && !string.IsNullOrEmpty(CurrentPath))
            Detached(LoadAsync(CurrentPath), "load");
    }

    public TabState ToTabState() => new()
    {
        Path = CurrentPath,
        Sort = Sort,
        SortDescending = SortDescending,
        ShowHidden = ShowHidden,
        View = View,
        GroupBy = GroupBy,
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

    [RelayCommand]
    public void ToggleFilter()
    {
        IsFilterVisible = !IsFilterVisible;
        if (!IsFilterVisible && FilterText.Length > 0) FilterText = "";
    }

    /// <summary>
    /// Rows whose names cannot be told apart by eye. Bound by every listing, so
    /// a row can mark itself — see <see cref="ConfusableNames"/> for why this
    /// exists at all.
    /// </summary>
    [ObservableProperty] private IReadOnlySet<string> _confusable =
        new HashSet<string>(StringComparer.Ordinal);

    private void ApplyFilter()
    {
        var filtered = string.IsNullOrWhiteSpace(FilterText)
            ? _all
            : _all.Where(e => e.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase)).ToList();

        _groupNow = DateTimeOffset.Now;

        var sorted = filtered.ToList();
        sorted.Sort(Compare);

        // Before the swap, so a row realized by ReplaceAll already has its
        // header available rather than reading a stale map.
        RecomputeGroups(sorted);

        RefreshConfusable();

        Entries.ReplaceAll(sorted);

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

        CurrentPath = path;
        PathText = path;
        IsLoading = true;
        LoadError = "";
        _all.Clear();
        Entries.Reset();
        NotifyNavigationState();

        var phaseSetup = setupWatch.ElapsedMilliseconds;

        var options = new ListingOptions { IncludeHidden = ShowHidden, BatchSize = 500 };

        // The ONE branch that makes a recent listing possible. Both sources are
        // the same IAsyncEnumerable shape, so everything below — batching, the
        // generation guard, sorting, filtering, the status line — runs
        // unchanged and knows nothing about where the rows came from.
        var source =
            VirtualPaths.IsRecent(path) ? RecentListing.EnumerateAsync(Recents, path, ct)
            : path == VirtualPaths.Trash ? RecentListing.EnumerateTrashAsync(Trash, ct)
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

                if (FilterText.Length > 0) ApplyFilter(); else ResortInPlace();

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










    private void RemoveByPathSilently(string path)
    {
        var masterIndex = _all.FindIndex(e => e.FullPath == path);
        if (masterIndex >= 0) _all.RemoveAt(masterIndex);

        for (var i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].FullPath != path) continue;
            Entries.RemoveAt(i);
            break;
        }
    }

    private bool MatchesFilter(FileEntry entry)
        => string.IsNullOrWhiteSpace(FilterText)
           || entry.Name.Contains(FilterText, StringComparison.OrdinalIgnoreCase);

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

    private void ResortInPlace()
    {
        if (Entries.Count == 0) return;

        _groupNow = DateTimeOffset.Now;

        var items = _all.Count > 0 ? _all.ToList() : Entries.ToList();
        items.Sort(Compare);

        RecomputeGroups(items);
        Entries.ReplaceAll(items);

        RefreshConfusable();
    }

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
    /// </summary>
    private void RefreshConfusable()
        => Confusable = ConfusableNames.Among(_all.Select(e => (e.FullPath, e.Name)));



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
    private readonly Dictionary<string, string> _groupHeaders = new(StringComparer.Ordinal);

    // Captured once per sort: asking for the time inside a comparison would
    // make the ordering depend on when each comparison happened.
    private DateTimeOffset _groupNow = DateTimeOffset.Now;


    /// <summary>Raised when headers change, so realized rows re-read them.</summary>
    public event EventHandler? GroupingChanged;

    private void RecomputeGroups(List<FileEntry> ordered)
    {
        _groupHeaders.Clear();

        if (GroupBy != GroupMode.None)
        {
            string? previous = null;

            foreach (var entry in ordered)
            {
                var label = Grouping.Label(entry, GroupBy, _groupNow);

                // Only the first row of a run carries the header; the rest are
                // plain, which is what makes it read as a group rather than a
                // repeated label.
                if (label != previous)
                {
                    _groupHeaders[entry.FullPath] = label;
                    previous = label;
                }
            }
        }

        GroupingChanged?.Invoke(this, EventArgs.Empty);
    }


    private void NotifyNavigationState()
    {
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        OnPropertyChanged(nameof(CanGoUp));
    }

    public void Dispose()
    {
        _vcsRefresh?.Stop();
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
