using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The folder tree where it lives: rooted at the sidebar's own places, shown
/// only when asked for, and following the pane only while anyone can see it.
///
/// The tree's own behaviour is <see cref="FolderTreeTests"/>' business. What is
/// here is the wiring — which is where the two halves can disagree.
/// </summary>
public sealed class SidebarFolderTreeTests : OwnedViewModels
{
    private readonly Vaktari.Core.Settings.SettingsState _settingsBefore = AppSettings.Current;

    public override void Dispose()
    {
        base.Dispose();
        AppSettings.Apply(_settingsBefore);
    }

    /// <summary>Switches the tree on or off, the way a settings save does.</summary>
    private static void TreeSetting(bool on)
        => AppSettings.Apply(AppSettings.Current with
        {
            Views = AppSettings.Current.Views with { ShowFolderTree = on },
        });

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task WaitUntil(Func<bool> done)
    {
        for (var i = 0; i < 500 && !done(); i++)
        {
            await Task.Delay(10);
            Settle();
        }
    }

    private static string P(params string[] parts)
        => OperatingSystem.IsWindows()
            ? @"C:\" + string.Join('\\', parts)
            : "/" + string.Join('/', parts);

    private static Place At(string path, string label) => new()
    {
        Id = label,
        Label = label,
        Path = path,
        Kind = PlaceKind.UserFolder,
        Icon = "folder",
    };

    private sealed class Places(params Place[] places) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string path) => null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([new("PLACES", places)]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);
    }

    /// <summary>Answers nothing, and counts what it was asked.</summary>
    private sealed class Quiet : IFileSystemProvider
    {
        public List<string> Read { get; } = [];

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Read.Add(path);
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// A fixed set of folders, counting what it is asked — and leaving out a
    /// name that starts with a dot unless the read asks for hidden ones, which
    /// is the one thing the tree's hidden-folder wiring can be measured by.
    /// </summary>
    private sealed class Folders : IFileSystemProvider
    {
        private readonly Dictionary<string, List<FileEntry>> _folders = new(PathRules.Comparer);

        public List<string> Read { get; } = [];

        private (string Path, bool Hidden, TaskCompletionSource Gate)? _held;

        /// <summary>
        /// Holds the next reads of <paramref name="folder"/> made with hidden
        /// folders shown (or not, by <paramref name="hidden"/>) until the task
        /// returned is completed. A real provider reads on the pool and hands
        /// the rows back later; without this every read here is over before
        /// the line that asked for it returns, which is the one thing a race
        /// between two reveals needs not to be.
        /// </summary>
        public TaskCompletionSource HoldBack(string folder, bool hidden)
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            _held = (folder, hidden, gate);

            return gate;
        }

        public Folders Holding(string folder, params string[] names)
        {
            _folders[folder] = [.. names.Select(name => new FileEntry(
                name,
                Path.Combine(folder, name),
                0,
                default,
                EntryFlags.Directory | (name.StartsWith('.') ? EntryFlags.Hidden : EntryFlags.None)))];

            return this;
        }

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Read.Add(path);
            await Task.CompletedTask;

            if (_held is { } held && PathRules.Same(held.Path, path) && held.Hidden == options.IncludeHidden)
                await held.Gate.Task.ConfigureAwait(true);

            yield return _folders.TryGetValue(path, out var rows)
                ? [.. rows.Where(row => options.IncludeHidden || !row.IsHidden)]
                : [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => !OperatingSystem.IsWindows();

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// **Every test that calls this needs the headless dispatcher**, so they
    /// are AvaloniaFacts: ReloadAsync publishes its groups through
    /// <c>Dispatcher.UIThread.InvokeAsync</c>, and awaiting that with nothing
    /// pumping is a deadlock rather than a slow test — measured, as a run that
    /// never finished.
    /// </summary>
    private static async Task<SidebarViewModel> Loaded(IPlacesProvider places, IFileSystemProvider fs)
    {
        var sidebar = new SidebarViewModel(places, fs: fs);

        await sidebar.ReloadAsync();

        return sidebar;
    }

    /// <summary>A sidebar with no filesystem behind it — every test fake, and
    /// the two other construction sites — has no tree and no section.</summary>
    [Fact]
    public void Without_a_filesystem_there_is_no_tree_at_all()
    {
        var sidebar = new SidebarViewModel(new Places());

        Assert.Null(sidebar.Tree);
        Assert.False(sidebar.ShowFolderTree);
    }

    [AvaloniaFact]
    public async Task The_roots_are_the_places()
    {
        var fs = new Quiet();

        var sidebar = await Loaded(new Places(At(P("home"), "Home"), At(P("work"), "Work")), fs);

        Assert.Equal(["Home", "Work"], sidebar.Tree!.Roots.Select(r => r.Label));

        // And nothing was read to find that out.
        Assert.Empty(fs.Read);
    }

    /// <summary>
    /// **A root has to be somewhere a filesystem can be asked about.** The bin
    /// and both Recent listings are places, and rooting a tree at one would
    /// offer a triangle onto a path no provider can enumerate.
    /// </summary>
    [AvaloniaFact]
    public async Task A_place_that_is_not_a_folder_is_not_a_root()
    {
        var sidebar = await Loaded(
            new Places(
                At(P("home"), "Home"),
                At(VirtualPaths.Trash, "Bin"),
                At(VirtualPaths.Files, "Recent files")),
            new Quiet());

        Assert.Equal(["Home"], sidebar.Tree!.Roots.Select(r => r.Label));
    }

    /// <summary>
    /// **Revealing reads a folder per level, so it waits until anyone can see
    /// it.** A folded section that still walked the tree on every navigation
    /// would be work for nobody.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folded_section_does_not_follow_the_pane()
    {
        var fs = new Quiet();

        // Switched on, so the fold is the only thing standing in the way.
        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = true;

        sidebar.SetCurrentPath(P("home", "docs"));

        Assert.Empty(fs.Read);
    }

    [AvaloniaFact]
    public async Task An_open_section_follows_the_pane()
    {
        var fs = new Quiet();

        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = false;

        sidebar.SetCurrentPath(P("home", "docs"));

        // Home is opened looking for docs. It answers nothing here, so the walk
        // stops there — which is the point: one level, not a search.
        Assert.Equal([P("home")], fs.Read);
    }

    /// <summary>
    /// **The tree is always built and off by default, and it read anyway.** The
    /// only question asked before revealing was whether the section was
    /// folded, so with the setting off — every install that never ticked it —
    /// each navigation into a branch not yet open read the root and every
    /// folder down to the target, for a section that was not on screen.
    /// </summary>
    [AvaloniaFact]
    public async Task A_tree_that_is_switched_off_does_not_follow_the_pane()
    {
        var fs = new Folders()
            .Holding(P("home"), "docs")
            .Holding(P("home", "docs"), "work");

        TreeSetting(on: false);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = false;

        sidebar.SetCurrentPath(P("home", "docs", "work"));
        Settle();

        Assert.Empty(fs.Read);
        Assert.False(sidebar.Tree!.Roots[0].IsExpanded);
    }

    /// <summary>
    /// **Unfolding showed wherever the tree had been when it was folded.** A
    /// folded tree rightly does not follow the pane, and the comment that said
    /// "unfolding goes through the same call" was describing a call that
    /// unfolding never made — so the branch and the current-folder mark stayed
    /// one navigation stale until the next one.
    /// </summary>
    [AvaloniaFact]
    public async Task Unfolding_the_section_catches_up_with_the_pane()
    {
        var fs = new Folders()
            .Holding(P("home"), "docs", "pics")
            .Holding(P("home", "docs"), "work");

        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.IsFoldersCollapsed = true;

        sidebar.SetCurrentPath(P("home", "docs", "work"));
        Settle();

        Assert.Null(sidebar.Tree!.CurrentPath);

        sidebar.IsFoldersCollapsed = false;

        await WaitUntil(() => sidebar.Tree.CurrentPath is not null);

        Assert.Equal(P("home", "docs", "work"), sidebar.Tree.CurrentPath);
        Assert.Equal(["Home", "docs", "work", "pics"], sidebar.Tree.Rows.Select(r => r.Label));
    }

    /// <summary>
    /// **Showing hidden files re-reads what the tree has open.** A home folder
    /// read without its hidden children goes on lacking them otherwise —
    /// opening an open folder reads nothing — so a reveal into ~/.config stops
    /// at home, one level short, however the pane is set.
    /// </summary>
    [AvaloniaFact]
    public async Task Showing_hidden_folders_reads_the_open_ones_again()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app")
            .Holding(P("home", "docs"), "work");

        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        // docs is open too, and has to stay open: a re-read that rebuilt the
        // nodes would shut every branch the person had opened.
        await sidebar.Tree!.RevealAsync(P("home", "docs", "work"));

        sidebar.SetCurrentPath(P("home", ".config"));
        Settle();

        Assert.Equal(["docs"], sidebar.Tree.Roots[0].Children.Select(c => c.Label));
        Assert.NotEqual(P("home", ".config"), sidebar.Tree.CurrentPath);

        sidebar.FollowHidden(true);

        await WaitUntil(() => sidebar.Tree.CurrentPath == P("home", ".config"));

        Assert.Equal(P("home", ".config"), sidebar.Tree.CurrentPath);
        Assert.Equal([".config", "docs"], sidebar.Tree.Roots[0].Children.Select(c => c.Label));
        Assert.True(sidebar.Tree.Roots[0].Children.Single(c => c.Label == "docs").IsExpanded);
        Assert.Equal(
            ["Home", ".config", "docs", "work"],
            sidebar.Tree.Rows.Select(r => r.Label));
    }

    /// <summary>
    /// **While the tree is hidden the re-read waits for it to be shown.** It
    /// reads a folder per open level, which is the work a hidden tree is not
    /// to do — but it must not be forgotten either, or the tree comes back
    /// showing the old answer.
    /// </summary>
    [AvaloniaFact]
    public async Task A_hidden_folder_change_while_folded_is_read_on_unfolding()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app");

        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);

        sidebar.SetCurrentPath(P("home", "docs"));
        await WaitUntil(() => sidebar.Tree!.CurrentPath is not null);

        sidebar.IsFoldersCollapsed = true;
        sidebar.SetCurrentPath(P("home", ".config"));

        fs.Read.Clear();
        sidebar.FollowHidden(true);
        Settle();

        Assert.Empty(fs.Read);

        sidebar.IsFoldersCollapsed = false;

        await WaitUntil(() => sidebar.Tree!.CurrentPath == P("home", ".config"));

        Assert.Equal(P("home", ".config"), sidebar.Tree!.CurrentPath);
    }

    // ---- through the shell -------------------------------------------------

    private ShellViewModel Shell(IFileSystemProvider fs, IPlacesProvider places, string at)
    {
        var shell = Own(new ShellViewModel(fs, places: places));

        shell.Start(null, at);

        return shell;
    }

    /// <summary>
    /// **The tree's ShowHidden had no writer outside the tests**, so every
    /// read left hidden folders out whatever the pane was showing. The shell
    /// is what knows which pane is active, so it is the shell that hands the
    /// answer on — here, by the pane's own toggle.
    /// </summary>
    [AvaloniaFact]
    public async Task A_pane_showing_hidden_files_shows_them_in_the_tree_too()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app");

        TreeSetting(on: true);

        var shell = Shell(fs, new Places(At(P("home"), "Home")), P("home", ".config"));
        var tree = shell.Sidebar.Tree!;

        // Revealed as far as it can go without hidden folders: home is open,
        // and .config is not in it.
        await WaitUntil(() => tree.Roots.Count == 1 && tree.Roots[0].IsExpanded && !tree.Roots[0].IsLoading);

        Assert.False(shell.ActiveTab!.ShowHidden);
        Assert.Null(tree.CurrentPath);

        shell.ActiveTab.ShowHidden = true;

        await WaitUntil(() => tree.CurrentPath is not null);

        Assert.True(tree.ShowHidden);
        Assert.Equal(P("home", ".config"), tree.CurrentPath);
    }

    /// <summary>
    /// **The tree follows the ACTIVE pane's answer.** A background tab showing
    /// hidden files changes nothing until it is the one being looked at, and
    /// then switching to it is enough — no toggle is pressed at that moment,
    /// so the tab switch has to carry the answer itself.
    /// </summary>
    [AvaloniaFact]
    public async Task Switching_to_a_tab_that_shows_hidden_files_takes_its_answer()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app");

        TreeSetting(on: true);

        var shell = Shell(fs, new Places(At(P("home"), "Home")), P("home", "docs"));
        var tree = shell.Sidebar.Tree!;

        await WaitUntil(() => tree.CurrentPath == P("home", "docs"));

        var other = shell.Left.AddTab(P("home", ".config"), activate: false);

        Settle();

        other.ShowHidden = true;
        Settle();

        // Not the active pane, so not the tree's business yet.
        Assert.False(tree.ShowHidden);
        Assert.Equal(["docs"], tree.Roots[0].Children.Select(c => c.Label));

        shell.Left.ActiveTab = other;

        await WaitUntil(() => tree.CurrentPath == P("home", ".config"));

        Assert.True(tree.ShowHidden);
        Assert.Equal(P("home", ".config"), tree.CurrentPath);
    }

    /// <summary>
    /// **The re-read marked the folder the pane had just left.** Switching to a
    /// tab that shows hidden files hands the tree the new answer and THEN the
    /// new place, so the re-read began while the sidebar still held the old
    /// one and revealed it when it finished. The new place's own reveal had
    /// already run through a home folder still read without its dot folders,
    /// and stopped there. With a real provider the re-read is still reading
    /// when the new place arrives; the hold here is what makes that so.
    /// </summary>
    [AvaloniaFact]
    public async Task A_re_read_reveals_where_the_pane_is_when_it_finishes()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app");

        TreeSetting(on: true);

        var shell = Shell(fs, new Places(At(P("home"), "Home")), P("home", "docs"));
        var tree = shell.Sidebar.Tree!;

        await WaitUntil(() => tree.CurrentPath == P("home", "docs"));

        var other = shell.Left.AddTab(P("home", ".config"), activate: false);

        other.ShowHidden = true;
        Settle();

        var gate = fs.HoldBack(P("home"), hidden: true);

        shell.Left.ActiveTab = other;
        Settle();

        Assert.True(tree.Roots[0].IsLoading, "GUARD: the re-read was not held, so nothing raced it");
        Assert.Equal(P("home", "docs"), tree.CurrentPath);

        gate.SetResult();

        await WaitUntil(() => tree.CurrentPath == P("home", ".config"));

        Assert.Equal(P("home", ".config"), tree.CurrentPath);
        Assert.Contains(".config", tree.Roots[0].Children.Select(c => c.Label));
    }

    /// <summary>
    /// **An older reveal could finish last and move the mark back.** Going
    /// into a branch nobody has opened starts a reveal that reads a folder per
    /// level; going straight on to a folder whose branch is open marks it at
    /// once — and the first reveal then landed and marked the folder already
    /// left.
    /// </summary>
    [AvaloniaFact]
    public async Task An_older_reveal_finishing_last_does_not_move_the_mark()
    {
        var fs = new Folders()
            .Holding(P("home"), "docs", "pics")
            .Holding(P("home", "pics"), "deep");

        TreeSetting(on: true);

        var sidebar = await Loaded(new Places(At(P("home"), "Home")), fs);
        var tree = sidebar.Tree!;

        await tree.RevealAsync(P("home", "docs"));

        var gate = fs.HoldBack(P("home", "pics"), hidden: false);

        var slow = tree.RevealAsync(P("home", "pics", "deep"));

        await tree.RevealAsync(P("home"));

        Assert.Equal(P("home"), tree.CurrentPath);
        Assert.False(slow.IsCompleted, "GUARD: the first reveal was not held, so it could not finish last");

        gate.SetResult();
        await slow;

        Assert.Equal(P("home"), tree.CurrentPath);
    }

    /// <summary>
    /// **Clicking into the other half of a split left the tree on the half
    /// just left.** The sidebar hears where the active pane is from a group's
    /// LocationChanged, and a group becoming the active one raises none — so a
    /// right half showing hidden files was followed by a tree without them,
    /// marking the left half's folder, until the right half next moved.
    /// </summary>
    [AvaloniaFact]
    public async Task Activating_the_other_side_takes_its_place_and_its_answer()
    {
        var fs = new Folders()
            .Holding(P("home"), ".config", "docs")
            .Holding(P("home", ".config"), "app");

        TreeSetting(on: true);

        var shell = Shell(fs, new Places(At(P("home"), "Home")), P("home", "docs"));
        var tree = shell.Sidebar.Tree!;

        await WaitUntil(() => tree.CurrentPath == P("home", "docs"));

        shell.ToggleSplit();
        shell.ActivateGroup(shell.Left);

        var right = shell.Right!.ActiveTab!;

        await right.NavigateAsync(P("home", ".config"));

        right.ShowHidden = true;

        await WaitUntil(() => right.IsLoaded);

        Assert.False(tree.ShowHidden, "GUARD: the tree took the answer of a side that is not active");
        Assert.Equal(P("home", "docs"), tree.CurrentPath);

        shell.ActivateGroup(shell.Right);

        await WaitUntil(() => tree.CurrentPath == P("home", ".config"));

        Assert.True(tree.ShowHidden);
        Assert.Equal(P("home", ".config"), tree.CurrentPath);
    }

    /// <summary>
    /// **"Show a folder tree in the sidebar" did nothing until a restart.** The
    /// section's visibility is a computed property over the live settings, and
    /// nothing said it had changed, so the binding kept the answer it read when
    /// the window opened. And a tree that was off has not been following the
    /// pane, so appearing is also the moment it has to catch up.
    /// </summary>
    [AvaloniaFact]
    public async Task Switching_the_tree_on_shows_it_at_once_and_where_the_pane_is()
    {
        var fs = new Folders()
            .Holding(P("home"), "docs")
            .Holding(P("home", "docs"), "work");

        TreeSetting(on: false);

        var shell = Shell(fs, new Places(At(P("home"), "Home")), P("home", "docs"));
        var sidebar = shell.Sidebar;

        await WaitUntil(() => sidebar.Tree!.Roots.Count == 1);
        Settle();

        Assert.False(sidebar.ShowFolderTree);
        Assert.Null(sidebar.Tree!.CurrentPath);

        var raised = new List<string?>();
        sidebar.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        TreeSetting(on: true);
        shell.OnSettingsChanged();

        Assert.Contains(nameof(SidebarViewModel.ShowFolderTree), raised);
        Assert.True(sidebar.ShowFolderTree);

        await WaitUntil(() => sidebar.Tree.CurrentPath is not null);

        Assert.Equal(P("home", "docs"), sidebar.Tree.CurrentPath);
    }

    /// <summary>
    /// **An unmounted volume is given an empty Path on purpose** — GoToPlace
    /// relies on exactly that to refuse the click — so without a guard it
    /// becomes a root whose enumeration is of "", which is the working
    /// directory rather than anywhere the person can see.
    /// </summary>
    [AvaloniaFact]
    public async Task A_volume_that_is_not_mounted_is_not_a_root()
    {
        var sidebar = await Loaded(
            new Places(At(P("home"), "Home"), At("", "Backup drive")),
            new Quiet());

        Assert.Equal(["Home"], sidebar.Tree!.Roots.Select(r => r.Label));
    }

    /// <summary>Rebuilding the places — plugging a stick in — rebuilds the
    /// roots rather than adding to them.</summary>
    [AvaloniaFact]
    public async Task Reloading_the_places_rebuilds_the_roots()
    {
        var places = new Places(At(P("home"), "Home"));

        var sidebar = await Loaded(places, new Quiet());

        Assert.Single(sidebar.Tree!.Roots);

        await sidebar.ReloadAsync();

        Assert.Single(sidebar.Tree!.Roots);
    }
}
