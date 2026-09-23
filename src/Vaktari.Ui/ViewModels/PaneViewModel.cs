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
    /// What is inside this crumb, as menu rows — filled by <see cref="Menu"/>
    /// when the press that opens the flyout arrives, and refilled on every
    /// later press.
    ///
    /// **The bar only went UP.** Every crumb navigated to an ancestor and
    /// nothing anywhere enumerated one, so reaching a SIBLING of the folder you
    /// are in — the commonest move there is — meant clicking the parent, waiting
    /// for its listing, and finding the row. Explorer puts that folder's
    /// contents behind the chevron after each crumb and Dolphin behind the same
    /// spot; this is that menu.
    ///
    /// An ObservableCollection rather than a list handed over whole, because
    /// the flyout is already open by the time the directory read answers: the
    /// press opens the popup and starts the read in the same gesture, so the
    /// rows have to be able to arrive into a menu that is on screen.
    /// </summary>
    public ObservableCollection<PathSegment> Children { get; init; } = new();

    /// <summary>
    /// Fills <see cref="Children"/>. Null for a crumb with nothing to list — the
    /// ellipsis, which stands for several folders rather than one, and a virtual
    /// listing that is not This PC.
    /// </summary>
    public ICommand? Menu { get; init; }

    /// <summary>Whether this crumb offers the menu of what is inside it, which
    /// is exactly whether it was given the command that fills one.</summary>
    public bool HasMenu => Menu is not null;

    /// <summary>
    /// The separator drawn as plain text rather than as the button that opens
    /// the menu.
    ///
    /// **A crumb with no menu still needs its separator.** The button carries
    /// the glyph now, so hanging the whole thing off <see cref="HasMenu"/>
    /// would have dropped the mark after the ellipsis — leaving `C:\ … Vaktari`
    /// with the two halves of an elided path run together.
    /// </summary>
    public bool ShowPlainSeparator => ShowSeparator && !HasMenu;

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

    /// <summary>
    /// The ancestors this crumb is standing in for, nearest the root first.
    /// Empty on every crumb but the ellipsis.
    ///
    /// **The dropped ancestors were known and never offered.** The panel works
    /// out which crumbs come off the bar on every arrange and parks them
    /// off-screen, and that list was thrown away — while the "…" marking their
    /// absence opened the path EDITOR, which replaces the bar you were reading
    /// with a text box and answers a different question. The one thing the mark
    /// exists to say is "there are folders here", so the one thing it must be
    /// able to do is name them.
    ///
    /// Not <see cref="Children"/>, which is what is INSIDE a crumb and is read
    /// off a disk when the chevron beside it is pressed. These are ancestors,
    /// they are already in the bar, and they cost no read at all — so the
    /// ellipsis needs no "reading…" row and no guard against a second press.
    ///
    /// Filled by <see cref="BreadcrumbPanel"/> rather than by the view model,
    /// because the width of the toolbar decides the answer and only the panel
    /// knows it — it changes as the window and the split are dragged, with no
    /// navigation to rebuild the crumbs.
    /// </summary>
    public ObservableCollection<PathSegment> Hidden { get; } = new();

    /// <summary>
    /// Records what the bar had no room for.
    ///
    /// **Nothing is written when the set has not changed**, because this is
    /// called from arrange: a collection that reported a reset on every layout
    /// pass would rebuild the menu under the pointer of somebody reading it,
    /// and layout runs on a pointer-driven splitter drag.
    ///
    /// **The count alone decides it**, and comparing the names as well was dead
    /// code. Measured: a version that THREW when the counts matched and the
    /// paths did not ran the whole UI suite — 1911 tests, real windows arranged
    /// at many widths — without firing once. What the panel drops is always the
    /// run path[1..firstTail) of one unchanging list of crumbs, so two sets of
    /// equal length off the same bar hold the same crumbs in the same order; a
    /// navigation does not arrive here with a different run, it clears the bar
    /// and builds a new mark whose Hidden starts empty.
    /// </summary>
    internal void StandsFor(IReadOnlyList<PathSegment> hidden)
    {
        if (Hidden.Count == hidden.Count) return;

        Hidden.Clear();

        foreach (var crumb in hidden) Hidden.Add(crumb);
    }

    /// <summary>
    /// **Its command opened the path editor**, which is the fault the menu in
    /// <see cref="Hidden"/> replaces. The mark is not a place: pressing its
    /// face must not navigate, and must not swap the bar you were reading for
    /// a text box.
    ///
    /// Refused through CanExecute rather than left as a command that quietly
    /// does nothing — and defensively, because **no control on screen binds
    /// this any more**. The only one that ever did is the navigating half of
    /// the crumb template, and that half carries IsVisible="{Binding
    /// !IsEllipsis}": measured on the mark's own crumb in a real MainWindow,
    /// three buttons are realized and exactly one of them is effectively
    /// visible — the one carrying the menu, which has no command at all. So
    /// the refusal is what the command says about itself to whatever binds it
    /// next, not something a pointer can reach today.
    /// </summary>
    public static PathSegment Ellipsis() =>
        new("…", "", new RelayCommand(() => { }, () => false), IsLast: false)
        { IsEllipsis = true };
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

        // **The address bar's recent-locations menu is computed, and both of
        // the things it is computed FROM change behind its back.** The store
        // answers Record, Forget and ForgetAll; the setting decides whether the
        // store is consulted at all. Neither announced itself, so turning
        // "remember recent places" off — or ticking forget-on-close, which
        // empties the store as it saves — left the chevron standing on the
        // toolbar with a full menu of everywhere the user had been, which is
        // the opposite of what the setting was ticked for.
        //
        // Held in a field rather than re-read at Dispose: Recents is a settable
        // static, and unsubscribing from whatever it happens to be later would
        // leave the handler on the store it was actually attached to.
        _recents = Recents;

        if (_recents is not null) _recents.Changed += OnRecentPlacesChanged;

        Settings.AppSettings.Changed += OnSettingsChanged;

        // **The search menu had the same wound one control along, and worse.**
        // The store is one per application and this menu is one per pane, so
        // "Forget what was searched for" — pressed in one window — left every
        // other window's magnifier listing searches that were gone. Clicking
        // one of those rows did not merely fail: opening a history row is a
        // navigation to a search path, and a navigation to a search path
        // RECORDS it, so the entry the user had just asked to delete was
        // written straight back into the store.
        //
        // Held in a field for the reason above it: Searches is a settable
        // static, and unsubscribing from whatever it happens to be later would
        // leave the handler on the store it actually went on.
        _searches = Searches;

        if (_searches is not null) _searches.Changed += OnSearchHistoryChanged;

        // **A pane that is BUILT at 200% has to start there**, and this was
        // measured missing: the threshold multiplier was written only when the
        // pane's own zoom CHANGED, and every creation path assigns that zoom
        // the value it already holds — startup restores 1.0 over 1.0, a new tab
        // copies its neighbour's, a new window copies the opener's. So the
        // multiplier stayed at 1.0 while the text beside it was drawn at 200%,
        // and a 600px pane went on reserving 500px for two metadata columns
        // that no longer fitted, leaving 100px for the name.
        SyncTextScale();

        RefreshScripts();
        RefreshTemplates();
    }

    /// <summary>The store this pane's menu listens to, remembered so the
    /// handler comes off the same one it went on.</summary>
    private readonly IRecentStore? _recents;

    private void OnRecentPlacesChanged(object? sender, EventArgs e) => NotifyRecentPlaces();

    /// <summary>
    /// A settings save: recent places may have been switched off, and the key
    /// the magnifier's tooltip names may have moved. Hopped to the UI thread
    /// for the reason NotifyRecentPlaces gives.
    /// </summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        NotifyRecentPlaces();

        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) OnPropertyChanged(nameof(SearchTip));
        else Avalonia.Threading.Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SearchTip)));
    }

    /// <summary>The search history this pane's menu listens to, remembered for
    /// the same reason.</summary>
    private readonly Vaktari.Core.Search.ISearchHistory? _searches;

    private void OnSearchHistoryChanged(object? sender, EventArgs e) => RefreshSearchHistory();






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
    /// The key a view is remembered under.
    ///
    /// **One key for every usage listing.** The path carries the folder that was
    /// measured, so keyed as it is spelled, a person who looked at fifty folders
    /// would leave fifty records nobody can ever reach again — which is the
    /// reason <see cref="VirtualPaths.SearchViewKey"/> was written, and the
    /// shape a view for one of these actually has: "how I like these to look",
    /// not "how I like this folder's to look".
    ///
    /// **And one for every search, which is what that key was written for and
    /// never given.** A search was keyed by its own path, which carries the
    /// query, the folder, the scope and the case, so each distinct search left a
    /// record of its own — measured: two searches wrote two keys. The records
    /// already written are dropped when the store loads; see
    /// JsonFolderViewStore.
    /// </summary>
    private static string ViewKey(string path)
        => VirtualPaths.IsUsage(path) ? VirtualPaths.UsageViewKey
                   : VirtualPaths.IsDuplicates(path) ? VirtualPaths.DuplicatesViewKey
                   : VirtualPaths.IsSearch(path) ? VirtualPaths.SearchViewKey
                   : path;

    /// <summary>
    /// Applied on arrival, before the listing is asked for, so the folder is
    /// enumerated and sorted once under its own rules rather than sorted twice.
    /// Silent when the preference is off or the folder has no opinion.
    ///
    /// **Every line here reads "or keep what the pane had".** The record used
    /// to hold no way to say "I did not mention that", so a `.directory` naming
    /// one Dolphin key produced an opinion about all of them and arriving in
    /// such a folder pulled a pane out of the layout it was in. Null and zero
    /// are the two spellings of silence, and the pane's own value is what
    /// silence means.
    /// </summary>
    private void ApplyFolderView(string path)
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews?.Read(ViewKey(path)) is not { } view) return;

        View = view.View ?? View;
        Sort = view.Sort ?? Sort;
        SortDescending = view.SortDescending ?? SortDescending;
        GroupBy = view.GroupBy ?? GroupBy;

        // Zero means the folder expressed no opinion about scale, so the pane
        // keeps whatever it had — scale is an accessibility setting and a
        // folder must not be able to shrink someone's text.
        if (view.FontScale > 0) FontScale = view.FontScale;
        if (view.IconScale > 0) IconScale = view.IconScale;

        // A folder's opinion is about the pane, not about one layout.
        if (view.FontScale > 0 || view.IconScale > 0) SeedScales(FontScale, IconScale);

        // Before the listing, like everything else here: ShowHidden decides
        // `ListingOptions.IncludeHidden` further down LoadListingAsync, so a
        // folder that wants hidden files arrives with them rather than
        // enumerating twice.
        //
        // **Except under a reveal, which turned them on to reach a concealed
        // item.** LandOnAsync sets ShowHidden and then navigates, and this runs
        // during that navigation — so a destination recorded as "hide them" put
        // the item back out of sight before the listing was built, and "Show in
        // folder" on a dotfile landed in the right folder saying it was no
        // longer there. Measured with a store holding `ShowHidden = false`
        // for the target: the listing came back empty and the selection with it.
        if (!_revealingHidden) ShowHidden = view.ShowHidden ?? ShowHidden;

        HideSizeColumn = view.Columns?.HideSize ?? HideSizeColumn;
        HideModifiedColumn = view.Columns?.HideModified ?? HideModifiedColumn;
        ShowTypeColumn = view.Columns?.ShowType ?? ShowTypeColumn;
        ShowCreatedColumn = view.Columns?.ShowCreated ?? ShowCreatedColumn;
    }

    /// <summary>
    /// Records the current view against the current folder. Called when the
    /// user changes one of these, never on arrival — otherwise merely visiting
    /// a folder would give it an opinion it never had.
    ///
    /// **The scale was re-imposed on arrival and never re-recorded.**
    /// ApplyFolderView above has always restored a stored FontScale/IconScale,
    /// and this was reached only from the View, Sort, SortDescending and
    /// GroupBy hooks — so the scale was snapshotted as a side effect of the
    /// last layout change and then pinned. Measured: with the preference on, in
    /// a folder whose layout had been changed once, Ctrl+wheel to 130%, walk to
    /// the parent and back, and the folder came up at whatever the scale had
    /// been when the layout was chosen. The zoom held while you stayed and was
    /// undone by leaving, which reads as the application fighting you.
    ///
    /// **The whole pane is stamped, not merged into what the folder already
    /// held** — which means an axis the pane inherited from somewhere else is
    /// recorded here too. That is the shipped shape of this store and predates
    /// the hidden-files and column fields joining it: measured on this
    /// worktree with the change stashed, a folder recorded as Grid, walked out
    /// of into a folder with no record at all, then merely re-GROUPED there,
    /// left that second folder recorded as Grid. Adding a field to the record
    /// adds it to that behaviour; it does not create it.
    /// </summary>
    public void RememberFolderView()
    {
        if (!Settings.AppSettings.Current.General.RememberViewPerFolder) return;
        if (FolderViews is null || string.IsNullOrEmpty(CurrentPath)) return;
        if (_restoringView) return;

        FolderViews.Write(ViewKey(CurrentPath), new FolderViewState
        {
            View = View,
            Sort = Sort,
            SortDescending = SortDescending,
            GroupBy = GroupBy,
            FontScale = FontScale,
            IconScale = IconScale,
            ShowHidden = ShowHidden,
            Columns = new FolderColumns
            {
                HideSize = HideSizeColumn,
                HideModified = HideModifiedColumn,
                ShowType = ShowTypeColumn,
                ShowCreated = ShowCreatedColumn,
            },
        });
    }

    private bool _restoringView;

    /// <summary>
    /// Held while a reveal is turning hidden files on to reach a concealed
    /// item, which is neither folder's opinion about anything.
    ///
    /// **The unhide is a step on the way, not a view change.** It happens with
    /// CurrentPath still on the folder being LEFT, so recording it stamped that
    /// folder — and every other axis of the pane with it — on the strength of
    /// somebody clicking a search hit somewhere else entirely. Measured against
    /// a pane standing in a folder with no record at all: after
    /// <c>ShowAsync</c> of a dotfile elsewhere, that folder held a full
    /// FolderViewState it had never asked for.
    /// </summary>
    private bool _revealingHidden;




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

    /// <summary>
    /// Raised when the thing that was double-clicked is a program, so the view
    /// can ask before starting it.
    ///
    /// An event for the same reason the chooser above is one — and the pane
    /// checks whether anybody is listening before it asks, because a pane with
    /// no window behind it must still open the file rather than swallow it.
    /// </summary>
    public event EventHandler<RunFileViewModel>? RunFileRequested;

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







    /// <summary>
    /// The folder really has nothing in it — as distinct from a filter that
    /// matched nothing, which is a different sentence.
    ///
    /// An empty listing used to look identical to one still loading.
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

        // Above the catch-all for the same reason: a measured folder with
        // nothing in it is not "this folder is empty" about a folder the person
        // is not looking at.
        _ when IsUsageListing => "nothing is using space here",

        // And above it for the same reason: a folder in which nothing is a
        // copy of anything is not an empty folder, and saying so over a folder
        // full of files reads as data loss.
        _ when IsDuplicatesListing => "nothing here is a copy of anything else",

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

    /// <summary>
    /// Total size of the selection, so the status bar can report it the way
    /// Dolphin does. Directories contribute nothing — measuring them would mean
    /// walking the tree on every selection change.
    ///
    /// **Unless the listing has already walked it.** A measured row carries its
    /// total, so skipping it left the status bar blank while the Size column an
    /// inch above showed the very number it was refusing to add up.
    /// </summary>
    private string SelectionSize()
    {
        long total = 0;
        var files = 0;

        foreach (var entry in Selection)
        {
            if (entry.IsDirectory && !entry.IsMeasured) continue;
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

        return Count(folders, files);
    }

    /// <summary>
    /// The same sentence from two numbers, for a caller that has counted
    /// already — a measurement hands back totals rather than rows, and a
    /// second spelling of "5 folders, 12 files" beside this one would be free
    /// to drift from it.
    /// </summary>
    private static string Count(int folders, int files)
    {
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

        // Flagged for the whole navigation, not just the assignment: the flag
        // has two jobs and the second one lands inside the load. It keeps the
        // unhide from being recorded against the folder being LEFT, and it
        // keeps the folder being ARRIVED AT from undoing it. Cleared in a
        // finally so a load that throws does not leave the pane deaf to its
        // own folders' hidden-file setting for the rest of the session.
        _revealingHidden = unhide;

        try
        {
            if (unhide) ShowHidden = true;

            await NavigateAsync(folder).ConfigureAwait(true);
        }
        finally
        {
            _revealingHidden = false;
        }

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

        // **Nothing anywhere recorded what had been searched for.** Here rather
        // than in RunSearch, because RunSearch is one of four roads to a search
        // path — the scope box, the case box and a row of the history menu
        // itself are the others — and a question narrowed to a folder is a
        // different question, not a repeat of the one it was narrowed from.
        //
        // Above the load rather than below it, unlike the recent folder at the
        // end of this method: see RecordSearch for why a search that found
        // nothing is still a search that was made.
        if (VirtualPaths.IsSearch(path)) RecordSearch(path);

        await LoadAsync(path).ConfigureAwait(false);

        // After the load, and only if it worked — a path that could not be read
        // is not somewhere the user goes, and counting it would push dead
        // folders up the list.
        // Recording the recent listing itself would be circular: it would put
        // "recent locations" at the top of recent locations.
        if (IsLoaded && !VirtualPaths.IsVirtual(path))
        {
            // The address bar's menu is built from this store, and hears about
            // the write through the store's own Changed rather than from a
            // notice raised here: Forget and ForgetAll change the same list and
            // do not come through this line at all.
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

    /// <summary>
    /// Shows what is using the space in this folder: a row per child, each
    /// carrying everything underneath it.
    ///
    /// **Only from a real folder.** The listing is one folder looked at another
    /// way, and the views — a search, the bin, This PC, Recent, and one of these
    /// already — hold rows from anywhere, so there is no one folder to measure.
    /// Refused rather than measured wrongly.
    /// </summary>
    [RelayCommand]
    public Task ShowSpaceUsageAsync()
        => IsRealFolder ? NavigateAsync(VirtualPaths.Usage(CurrentPath)) : Task.CompletedTask;

    /// <summary>
    /// Shows the files below this folder that are copies of each other.
    ///
    /// **Only from a real folder**, for the reason
    /// <see cref="ShowSpaceUsageAsync"/> gives: the views hold rows from
    /// anywhere, so there is no one tree to scan.
    /// </summary>
    [RelayCommand]
    public Task ShowDuplicatesAsync()
        => IsRealFolder ? NavigateAsync(VirtualPaths.Duplicates(CurrentPath)) : Task.CompletedTask;

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

        // **A program was handed to the desktop's opener, which never runs
        // one.** On a desktop that meant a shell script opened in a text editor
        // and a binary or an AppImage opened nothing whatsoever — no window, no
        // message — so a file you had just marked runnable could not be started
        // from anywhere in this application. Asked rather than done, because a
        // double-click has meant "open this" everywhere else here and is not by
        // itself consent to execute something; the window draws the three
        // answers.
        //
        // The listener is checked as well as the platform. A pane with no
        // window behind it — which is how it is built in several dozen tests,
        // and how a future headless route would build it — has nobody to show a
        // question, and swallowing the open would be worse than the fault being
        // fixed. It falls through and opens, exactly as before.
        //
        // **And the listing, which this asked on and the menu row refused.**
        // RefusedInBin above covers the bin and nothing covered the recent
        // listing, so a double-click or Enter there offered to start whatever
        // occupies a remembered path — the fault RunnableListing exists to
        // prevent, on the one listing this change itself fills with programs.
        if (RunFileRequested is { } ask
            && RunnableListing
            && _launcher is { } runner
            && runner.CanRunFile(entry.FullPath))
        {
            ask(this, new RunFileViewModel(
                entry.Name, () => Start(entry, run: true), () => Start(entry, run: false)));

            return Task.CompletedTask;
        }

        Start(entry, run: false);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Hands one file to the platform, one way or the other, and says so when
    /// it will not go.
    ///
    /// **Recorded here rather than before the question**, which is the rule the
    /// chooser already keeps by recording inside its callback: a question that
    /// was dismissed opened nothing and ran nothing, and must not leave a
    /// recent entry claiming otherwise. Within an answer it is still recorded
    /// on the ATTEMPT — asking to start something is the user's act whatever
    /// the desktop then makes of it.
    ///
    /// **This was a bare call with nothing after it**, because Open returned
    /// void. Both launchers caught the failure and dropped it, so
    /// double-clicking a row whose file had been deleted since the listing was
    /// drawn did nothing at all: no window, no message, nothing to distinguish
    /// it from a click that missed. One place covers every route that reaches a
    /// file — the pointer and Enter through OpenAsync, a path typed into the
    /// location bar, and both answers to the question above.
    /// </summary>
    private void Start(FileEntry entry, bool run)
    {
        Recording?.Record(entry.FullPath, RecentKind.File);

        var failure = run
            ? _launcher?.Run(entry.FullPath)
            : _launcher?.Open(entry.FullPath);

        if (failure is { } refused)
            Status = Failures.Describe(refused, run ? "run that file" : "open that file");
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

                // **And whatever it just put here comes back selected.**
                // Registered BEFORE the refresh is asked for, because the
                // refresh is what drains the register — a paste, a drop, a Copy
                // to, a Duplicate and a retry all arrive down this one wire.
                //
                // Unguarded, because an operation with nothing to report hands
                // over an empty list and an empty list asks for nothing: a
                // trash and a delete, which land nothing anywhere; a run
                // cancelled at a clash, which reports nothing even for the
                // items ahead of the clash; and the administrator retry, whose
                // work happens in another process that answers with an exit
                // code and no paths. Testing the count here as well would be a
                // second spelling of the same answer.
                SelectOnlyAfterLoad(handle.Landed);

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
        OnPropertyChanged(nameof(CanRunSelection));
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

            // The pointer a row wears is computed from the path too — one click
            // opens everywhere except the bin — and IsTrashListing being raised
            // is not this being raised. Without the line, walking into the bin
            // with single click set carried the hand and the underline in with
            // it, and walking back out left them behind.
            OnPropertyChanged(nameof(OpensOnSingleClick));

            // All six, because a search moves between searches: retyping the
            // query or ticking either box changes the path from one search
            // to another, and a band that only appeared and disappeared would
            // go on showing the previous question.
            OnPropertyChanged(nameof(IsSearchListing));

            // The same shape, for the same reason: a pane moves from one usage
            // listing straight to another when a different folder is measured,
            // so a band that only appeared and disappeared would go on naming
            // the folder before it.
            OnPropertyChanged(nameof(IsUsageListing));
            OnPropertyChanged(nameof(UsageFolder));

            // And its twin, for the same reason: scanning a second folder moves
            // the pane from one duplicates listing straight to another.
            OnPropertyChanged(nameof(IsDuplicatesListing));

            // The menu row that goes to where a row lives is bound to this one,
            // and a change announced for IsSearchListing is not a change
            // announced for this: without the line the row keeps whatever
            // visibility the previous listing left it with.
            OnPropertyChanged(nameof(CanGoToLocation));

            // **The bin went on offering "Windows menu" after the pane left
            // the folder it was in.** Same shape as the line above, and worse
            // for being a row that hands the click to somebody else's code:
            // HasShellMenu is computed from the path, IsTrashListing being
            // raised is not this being raised, and measured on a headless
            // window at "vaktari:trash" the pane answered false while the row
            // — and the rule bound to the same gate — stayed on screen.
            OnPropertyChanged(nameof(HasShellMenu));
            OnPropertyChanged(nameof(SearchQueryText));
            OnPropertyChanged(nameof(CanScopeSearch));
            OnPropertyChanged(nameof(SearchScopedHere));
            OnPropertyChanged(nameof(SearchScopeLabel));
            OnPropertyChanged(nameof(SearchMatchesCase));
            OnPropertyChanged(nameof(SearchesContents));

            // Half of it is about the question, which is on the path: a
            // pattern is offered no contents box.
            OnPropertyChanged(nameof(CanSearchContents));

            // The box's tooltip asks the backend about this question, the way
            // the warning below does, and for the same reason.
            OnPropertyChanged(nameof(SearchContentsHint));

            // Both read the PATH now — the warning asks the backend about this
            // particular question, and the sentence names the folder the scope
            // box names. Without these lines a search narrowed from one pane to
            // another keeps the previous question's answer: the wrong folder in
            // the sentence, and on a KDE box a glob typed after a word would
            // keep the word's "an index is answering" silence while every
            // folder was read.
            OnPropertyChanged(nameof(SearchUnindexed));
            OnPropertyChanged(nameof(SearchBackendLine));

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

    /// <summary>
    /// **Ctrl+H was one of the five view changes a folder could not keep**, the
    /// four column ticks being the others. The reload was the whole of
    /// this hook, so with "remember the view for each folder" on, showing the
    /// dotfiles in a source tree survived exactly as long as you stayed in it.
    /// Worse where the folder already had an entry: the arrival re-applies what
    /// was last recorded, so coming back put them away again — the pane obeying
    /// a record of an answer nobody had updated.
    ///
    /// Recorded before the reload rather than after, so the write happens on
    /// the caller's thread with `CurrentPath` still the folder being looked at
    /// — the load is detached and would otherwise race an arrival elsewhere.
    ///
    /// **But not when a reveal turned them on.** That assignment happens in
    /// LandOnAsync with CurrentPath still the folder being left, and it is a
    /// step on the way to a concealed item rather than an opinion about
    /// anywhere. Unguarded it stamped the departing folder with the whole pane
    /// — layout, sort, columns and scales — because somebody clicked a search
    /// hit; measured against a pane in a folder the store had never heard of.
    /// </summary>
    partial void OnShowHiddenChanged(bool value)
    {
        if (!_revealingHidden) RememberFolderView();

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
    /// Flips hidden-file visibility. Two routes reach it — Ctrl+H, and the
    /// listing menu's View row, which is the only pointer route the left half
    /// of a split has. The settings flyout binds `ShowHidden` directly
    /// instead, so this must stay a plain flip with no extra behaviour, or the
    /// paths would diverge.
    ///
    /// **The menu row binds this command and reads ShowHidden OneWay**, never
    /// both ways at once: a two-way tick plus this command would flip the
    /// property and then flip it straight back.
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

    /// <summary>
    /// How each row compares with the other side while the two are being
    /// compared, keyed by this pane's own paths; empty otherwise. Bound by
    /// every listing the way <see cref="Confusable"/> is, and for the same
    /// reason: a new map is what makes every realized row look again.
    ///
    /// **Per pane, not the version-control store.** That one is shared and
    /// keyed by path, which it can afford because no path is in two folders.
    /// A compare mark depends on what the folder is compared WITH, so two
    /// windows comparing one folder against two others would overwrite each
    /// other's marks in a shared store.
    /// </summary>
    [ObservableProperty] private IReadOnlyDictionary<string, CompareMark> _compareMarks = NoMarks;

    internal static readonly IReadOnlyDictionary<string, CompareMark> NoMarks =
        new Dictionary<string, CompareMark>(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread whenever the listing has settled --
    /// loaded, re-sorted, filtered, or changed on disk -- at the moments the
    /// look-alike marks are worked out again.</summary>
    public event EventHandler? ListingSettled;

    /// <summary>The whole listing, whatever the filter box is hiding: what a
    /// comparison compares.</summary>
    internal IReadOnlyList<FileEntry> Listed => _all;

    /// <summary>Selects every row the comparison marked.</summary>
    [RelayCommand]
    private void SelectDifferences() => ReselectPaths([.. CompareMarks.Keys]);

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

        // Same reason, and a fresh tally for the same one: the next question's
        // count starts at nothing whether or not this one finished.
        SearchSkippedLine = "";
        var skips = _searchSkips = new Core.Search.ContentSkips();

        // And the backend's word that it is walking rather than asking its
        // index, which belongs to the load that heard it. Announced when it
        // was set, since a reload of the same search moves no path to say so.
        if (_walkingInstead)
        {
            _walkingInstead = false;
            OnPropertyChanged(nameof(SearchUnindexed));
            OnPropertyChanged(nameof(SearchBackendLine));
            OnPropertyChanged(nameof(SearchContentsHint));
        }

        // Same reason: the band would otherwise carry the last folder's total
        // while the next one is still being walked.
        //
        // GUARD, not a tested rule. The completion block assigns the total
        // again at the end of every load, and for a listing that measured
        // nothing that assignment is the default — so at a load BOUNDARY this
        // line changes nothing, and removing it reddens no test. Measured.
        // What it covers is the stretch in between, where the band is already
        // on screen and the walk has not finished, and nothing can watch that
        // without racing the walk it is waiting on.
        UsageTotal = new Usage();

        // Beside it, and BOTH are the same GUARD. The spare copies looked like
        // the exception — they are paths a command acts on, so a stale list is
        // a command aimed at the previous folder — and the mutation says
        // otherwise: clearing them reddens nothing. The completion block
        // replaces the list at the end of every load, so at a load BOUNDARY
        // this changes nothing; and in the stretch between, the listing has
        // just been emptied two lines below, so ReselectPaths can see none of
        // the stale rows and does nothing at all. Kept for the same reason the
        // line above it is: what it guards is true today by a coincidence of
        // ordering rather than by anything that says so.
        DuplicateTotal = new Copies();
        _extraCopies = [];

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

        // The measured folder's own total, in the same shape and for the same
        // reason: written on the pool by the listing below, read on the
        // dispatcher once the load finishes.
        var measured = new Usage();

        // The scan's own two, in the same shape and for the same reason.
        var copies = new Copies();
        IReadOnlyList<string> spare = [];

        var source =
            VirtualPaths.IsRecent(path) ? RecentListing.EnumerateAsync(Recents, path, ct)
            : path == VirtualPaths.Trash ? RecentListing.EnumerateTrashAsync(Trash, ct)
            : path == VirtualPaths.Computer ? ComputerListing.EnumerateAsync(Places, ct)
            : VirtualPaths.IsSearch(path)
                ? SearchListing.EnumerateAsync(
                    Search, path, options, ct, SearchLimit, () => capped = true, skips,
                    () => OnWalkingInstead(generation))
            : VirtualPaths.IsUsage(path)
                ? SpaceListing.EnumerateAsync(
                    VirtualPaths.FolderOf(path), ShowHidden, progress: null, ct,
                    total => measured = total)
            : VirtualPaths.IsDuplicates(path)
                ? DuplicateListing.EnumerateAsync(
                    VirtualPaths.FolderOf(path), ShowHidden, ct,
                    summary => copies = summary,
                    extras => spare = extras)
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

                // And whatever an operation reports having just PUT here, which
                // replaces the carried paths rather than joining them. See
                // SelectOnlyAfterLoad.
                //
                // **Drained HERE, past the generation guard, rather than at the
                // top beside the carried list — because a load that is
                // superseded never reaches this line.** Measured: paste two
                // files into a folder and let a second operation on the same
                // pane finish behind it, and its RefreshAsync bumps the
                // generation while the paste's own load is still enumerating.
                // Read at the top, the arrivals were spent by that dead load
                // and the surviving one got an empty list — the two files came
                // back with nothing selected at all. Read here, the register
                // outlives the superseded load and the next one that gets as
                // far as the rows picks it up.
                var arrived = _selectInsteadAfterLoad;
                _selectInsteadAfterLoad = [];

                // After the sort, not before it: Reselect walks Entries, and
                // the rows it walks have to be the ones that will still be
                // there. The resort above rebuilds the collection whichever
                // route it takes, and restores only what it was holding when
                // it started — which, on a reload, is nothing.
                Reselect(carry, arrived);

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
                var polled = !VirtualPaths.IsVirtual(path) && StartWatching(path);
                sw.Stop();

                // Cleared, NOT set to the count. Summary already shows
                // "36 items" and Status sat beside it showing the same thing,
                // so the status bar read "36 items   36 items". Status is for
                // messages; the count has an owner and this is not it.
                //
                // **The one message a load has of its own:** a folder read on
                // a timer lags behind one that is watched, and a lag with no
                // reason given reads as a slow application. Said here rather
                // than where the watch fell back, because this line would
                // clear it.
                Status = polled ? ReadOnATimer : "";
                IsLoading = false;
                IsLoaded = true;

                // **This is the sentence the walk never said.** Set here rather
                // than where the truncation was noticed: it is noticed on the
                // pool, and the band binds to this.
                SearchHitLimit = capped;

                // Beside it: counted on the pool by the walk, said here.
                SearchSkippedLine = SkippedLine(skips);

                // Beside it, and for the same reason: the total is worked out
                // on the pool, and the band binds to this.
                UsageTotal = measured;

                // Beside it, and both for the same reason: worked out on the
                // pool, read here on the dispatcher.
                DuplicateTotal = copies;
                _extraCopies = spare;

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

        // **An empty folder never said it had settled.** This returned ahead
        // of RefreshConfusable, the one place ListingSettled is raised, so the
        // two sides were not compared again when one of them opened an empty
        // folder: measured, the other side kept the marks it had before.
        // There is nothing to sort, but the listing has still settled.
        if (Entries.Count == 0)
        {
            RefreshConfusable();
            return;
        }

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
    /// <remarks>
    /// **The name WITH its extension, whatever the extension preference says.**
    /// Hiding extensions draws main.c and main.h as one word each, and this set
    /// keys on what a row draws — so a folder of C sources, or of .tex beside
    /// .pdf, marked every row "Look-alike" the moment the preference went on.
    /// That is noise, and noise in the one mark whose whole value is that it is
    /// rare.
    ///
    /// What makes the suppression safe is at the other end, in
    /// <c>FileKind.Runs</c>: the suffix that says a row STARTS something is
    /// never hidden, so the collision worth marking — "report.exe" and
    /// "report.pdf" both drawn "report" — cannot be created. What is left
    /// unmarked is a pair of documents whose only difference is an extension
    /// the person switched off themselves, and which pressing F2 still spells
    /// out in full — the Type column and the name tooltip say it too, each
    /// while its own switch is on.
    /// </remarks>
    private void RefreshConfusable()
    {
        Confusable = ConfusableNames.Among(
            _all.Select(e => (e.FullPath, FileKind.DisplayName(e, hideExtension: false))));

        // Every place that settles a listing comes through here, which is
        // what makes it the one place to say so.
        ListingSettled?.Invoke(this, EventArgs.Empty);
    }



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
        // **Both static, and both outlive every pane.** A tab closed while the
        // window stays open would otherwise keep answering settings saves and
        // store writes forever, and answering them by posting to a dispatcher
        // — which in the tests is a dispatcher the headless session that made
        // it has already finished with.
        if (_recents is not null) _recents.Changed -= OnRecentPlacesChanged;

        Settings.AppSettings.Changed -= OnSettingsChanged;

        if (_searches is not null) _searches.Changed -= OnSearchHistoryChanged;

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
