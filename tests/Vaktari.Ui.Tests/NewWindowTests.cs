using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Sharing;
using Vaktari.Core.Places;
using Vaktari.Core.Search;
using Vaktari.Core.Session;
using Vaktari.Core.Settings;
using Vaktari.Ui;
using Vaktari.Ui.Session;
using Vaktari.Ui.Settings;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A second window.
///
/// **There was no way to open one, and the application could not have survived
/// it.** MainWindow's constructor built the platform, four stores, the icon
/// theme and the hourly trash sweep and THEN wired a window to them, so a
/// second `new MainWindow()` would have been two writers of session.json, two
/// folder-view stores flushing stale snapshots over each other, a second set of
/// device watches and a second sweep deleting the same expired files. The
/// session schema has had a LIST of windows in it since it was written and
/// three readers all took `Windows.FirstOrDefault()`.
///
/// So this covers three separate things: that the family shares one of
/// everything, that the family is what gets saved and restored, and that the
/// subsystems which quietly became multi-window on the same day — the eject
/// veto, the transfer queue, the shares, the desktop's own request channel and
/// every never-released event subscription — behave when there is more than one
/// window to be wrong about.
/// </summary>
public sealed class NewWindowTests : OwnedViewModels
{
    private readonly SettingsState _settingsBefore = AppSettings.Current;
    private readonly ISearchProvider? _searchBefore = PaneViewModel.Search;

    public override void Dispose()
    {
        AppSettings.Apply(_settingsBefore);
        PaneViewModel.Search = _searchBefore;
        CutMarks.Clear();

        base.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- the harness -------------------------------------------------------

    private sealed class Inert : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>One removable drive, and a fixed disk so a test about refusing
    /// has something it is allowed to refuse.</summary>
    private sealed class OneStick(string root) : IPlacesProvider
    {
        public int Calls { get; private set; }

        public event EventHandler? PlacesChanged;

        /// <summary>How many handlers are still attached — a direct reading of
        /// "does that sidebar still hold this provider".</summary>
        public int Subscribers => PlacesChanged?.GetInvocationList().Length ?? 0;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>(
            [
                new PlaceGroup("DEVICES",
                [
                    new Place
                    {
                        Id = "dev:" + root,
                        Label = "STICK",
                        Path = root,
                        Kind = PlaceKind.RemovableDevice,
                        Icon = "usb",
                        CanEject = true,
                    },
                ]),
            ]);

        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
        {
            Calls++;
            return ValueTask.FromResult(EjectResult.Ejected("safe to unplug"));
        }

        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask PinAsync(string path, string? label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> orderedIds, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<int> ImportExistingAsync(CancellationToken ct) => ValueTask.FromResult(0);

        public void Raise() => PlacesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static FileEntry Folder(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 0,
               DateTimeOffset.UnixEpoch, EntryFlags.Directory);

    private static FileEntry File(string name)
        => new(name, Path.Combine(Path.GetTempPath(), name), 1,
               DateTimeOffset.UnixEpoch, EntryFlags.None);

    private ShellViewModel Shell(IPlacesProvider? places = null)
    {
        var shell = Own(new ShellViewModel(new Inert(), places: places));
        shell.Start(null, Path.GetTempPath());

        return shell;
    }

    private static void Settle() => Dispatcher.UIThread.RunJobs();

    /// <summary>
    /// The session this run's directory starts from.
    ///
    /// **Not optional, and it was measured.** The state directory is per test
    /// CLASS, a closing window writes its session into it, and xunit promises
    /// no order — so without this the two-window tests left a two-window
    /// session for whichever test ran next, which passed under --filter and
    /// failed in the full run.
    /// </summary>
    private static async Task SaveAsync(params WindowSession[] windows)
    {
        var directory = TestState.Current();
        Directory.CreateDirectory(directory);

        var store = new JsonSessionStore(directory);

        store.NotifyChanged(new SessionState
        {
            Version = SessionState.CurrentVersion,
            Windows = windows,
        });

        await store.FlushAsync(CancellationToken.None);
        await store.DisposeAsync();
    }

    /// <summary>A saved window sitting on one folder, so a restore is visible.</summary>
    private static WindowSession Saved(string folder, double width = 1000, double sidebar = 210)
        => new()
        {
            Width = width,
            Height = 680,
            SidebarWidth = sidebar,
            Panes = [new PaneState { Tabs = [new TabState { Path = folder }] }],
        };

    /// <summary>
    /// Preferences on disk, which is the only place a window reads them from:
    /// its own constructor applies what it loaded over whatever a test had set.
    /// </summary>
    private static void SaveSettings(StartupSettings startup)
        => new JsonSettingsStore(TestState.Current())
            .Save(AppSettings.Current with { Startup = startup });

    /// <summary>
    /// Closes the family, peers first, so the LAST close is the one that runs
    /// the last-window-out path.
    /// </summary>
    private static void CloseAll(WindowServices services)
    {
        foreach (var window in services.Windows.ToList().AsEnumerable().Reverse())
        {
            try { window.Close(); }
            catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        }

        Settle();
    }

    // ---- one application, several windows ----------------------------------

    /// <summary>
    /// The point of the whole design: a second window is the SAME application.
    /// One session store, one platform, one set of services — because two of
    /// any of them on one state directory is two writers of the same files.
    /// </summary>
    [AvaloniaFact]
    public async Task A_second_window_shares_the_first_windows_stores()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var peer = Assert.Single(founder.Services.Windows, w => !ReferenceEquals(w, founder));

            Assert.Same(founder.Services, peer.Services);
            Assert.Same(founder.Services.Session, peer.Services.Session);
            Assert.Same(founder.Services.Platform, peer.Services.Platform);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>
    /// What gets WRITTEN is the family, not whichever window happened to change
    /// last. Two windows with two DIFFERENT sidebar widths, so this cannot pass
    /// by listing one window twice.
    /// </summary>
    [AvaloniaFact]
    public async Task The_session_holds_every_window()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var services = founder.Services;
            var peer = services.Windows.First(w => !ReferenceEquals(w, founder));

            founder.Shell.Sidebar.Width = 265;
            peer.Shell.Sidebar.Width = 315;

            founder.Shell.NotifyWindowChanged();
            await services.Session.FlushAsync(CancellationToken.None);

            var onDisk = services.Session.Load();

            Assert.NotNull(onDisk);
            Assert.Equal(2, onDisk.Windows.Count);
            Assert.Equal([265d, 315d], onDisk.Windows.Select(w => w.SidebarWidth));
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>A window that has gone is out of the session before it is
    /// written, or the next launch would open it again.</summary>
    [AvaloniaFact]
    public async Task Closing_a_window_takes_it_out_of_the_session()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var services = founder.Services;
            var peer = services.Windows.First(w => !ReferenceEquals(w, founder));

            Assert.Equal(2, services.Compose().Windows.Count);

            await services.ReleaseAsync(peer);

            Assert.Single(services.Compose().Windows);

            // **And it is out BEFORE the session is composed, not merely gone
            // by the end of the release.** Removing it afterwards leaves the
            // file holding a window that has closed, which the next launch
            // would faithfully open again.
            var onDisk = services.Session.Load();

            Assert.NotNull(onDisk);
            Assert.Single(onDisk.Windows);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>
    /// **The asymmetry, and it is the whole restore contract.** The last window
    /// out stays IN the session it writes. Dropping it first would leave a file
    /// with no windows in it, which on the next launch is indistinguishable
    /// from having forgotten everything.
    /// </summary>
    [AvaloniaFact]
    public async Task The_last_window_still_writes_itself_into_the_session()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        founder.Show();
        Settle();

        var services = founder.Services;
        founder.Shell.Sidebar.Width = 287;

        await services.ReleaseAsync(founder);

        var onDisk = services.Session.Load();

        Assert.NotNull(onDisk);
        var saved = Assert.Single(onDisk.Windows);
        Assert.Equal(287d, saved.SidebarWidth);

        // And then it really is let go of, so nothing composes a window that
        // has been torn down.
        Assert.Empty(services.Windows);

        CloseAll(services);
    }

    /// <summary>
    /// The other half: a session holding two windows opens two windows.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restored_session_opens_every_window_it_saved()
    {
        var first = Directory.CreateTempSubdirectory("vaktari-w0").FullName;
        var second = Directory.CreateTempSubdirectory("vaktari-w1").FullName;

        await SaveAsync(Saved(first), Saved(second));
        SaveSettings(new StartupSettings { ShowOnStartup = StartupLocation.RestoreSession });
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            var services = founder.Services;

            Assert.Equal(2, services.Windows.Count);
            Assert.Equal(
                [first, second],
                services.Windows.Select(w => w.Shell.ActiveTab?.CurrentPath));
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>
    /// And each of them takes its OWN saved size, rather than every window
    /// coming back the size of the first one.
    /// </summary>
    [AvaloniaFact]
    public async Task A_restored_window_takes_its_own_saved_size()
    {
        var first = Directory.CreateTempSubdirectory("vaktari-g0").FullName;
        var second = Directory.CreateTempSubdirectory("vaktari-g1").FullName;

        await SaveAsync(Saved(first, width: 900), Saved(second, width: 742));
        SaveSettings(new StartupSettings { ShowOnStartup = StartupLocation.RestoreSession });
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            var restored = founder.Services.Windows[1];

            Assert.Equal(742d, restored.Width);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>
    /// **The preference decides whether the session is consulted AT ALL.** With
    /// Home or a specific folder chosen, window 0 correctly ignores what was
    /// saved — and a restore loop gated only on "am I the founder" would then
    /// open N-1 more windows out of the very session the preference had just
    /// said not to read.
    /// </summary>
    [AvaloniaFact]
    public async Task A_startup_folder_preference_opens_one_window_however_many_were_saved()
    {
        var first = Directory.CreateTempSubdirectory("vaktari-s0").FullName;
        var second = Directory.CreateTempSubdirectory("vaktari-s1").FullName;
        var chosen = Directory.CreateTempSubdirectory("vaktari-chosen").FullName;

        await SaveAsync(Saved(first), Saved(second));
        SaveSettings(new StartupSettings
        {
            ShowOnStartup = StartupLocation.SpecificFolder,
            StartupFolder = chosen,
        });
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            Assert.Single(founder.Services.Windows);
            Assert.Equal(chosen, founder.Shell.ActiveTab?.CurrentPath);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    // ---- what a new window opens on, and what it looks like ----------------

    /// <summary>
    /// A window asked for a folder opens THERE — not on home, and not on the
    /// startup preference's folder. That preference answers "where does a
    /// LAUNCH begin", and this is not a launch.
    /// </summary>
    [AvaloniaFact]
    public async Task A_new_window_opens_on_the_folder_it_was_asked_for()
    {
        var asked = Directory.CreateTempSubdirectory("vaktari-asked").FullName;

        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.OpenPlaceInNewWindowCommand.Execute(
                new PlaceItemViewModel(new Place
                {
                    Id = "pin:asked",
                    Label = "Asked",
                    Path = asked,
                    Kind = PlaceKind.Bookmark,
                    Icon = "folder",
                }));

            Settle();

            var peer = founder.Services.Windows.First(w => !ReferenceEquals(w, founder));

            Assert.Equal(asked, peer.Shell.ActiveTab?.CurrentPath);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    /// <summary>
    /// **The same principle NewTab states five lines above the command that
    /// opens this**: a new tab that resets hidden files, the layout, the sort,
    /// the grouping and the zoom is a new tab you have to set up. A window is
    /// the heavier version of that tab. Font scale in particular is an
    /// accessibility setting, not a preference — a window that arrives at 1.0
    /// for somebody who works at 1.4 is one they have to fix before they can
    /// read it.
    /// </summary>
    [AvaloniaFact]
    public async Task A_new_window_opens_at_the_zoom_you_are_working_at()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.FontScale = 1.4;
            founder.Shell.Sidebar.Width = 320;
            founder.Shell.ActiveTab!.ShowHidden = true;

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var peer = founder.Services.Windows.First(w => !ReferenceEquals(w, founder));

            Assert.Equal(1.4, peer.Shell.FontScale);
            Assert.Equal(320d, peer.Shell.Sidebar.Width);

            // And the tab's own half of the view, which is what `like` carries.
            Assert.True(peer.Shell.ActiveTab?.ShowHidden,
                        "the new window's tab did not carry the opener's view");
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    // ---- the commands ------------------------------------------------------

    /// <summary>Ctrl+N asks for a window where you already are, exactly once.</summary>
    [AvaloniaFact]
    public void Ctrl_n_asks_for_a_window_where_you_already_are()
    {
        var shell = Shell();
        var asked = new List<string?>();

        shell.NewWindowRequested += (_, folder) => asked.Add(folder);

        shell.NewWindowCommand.Execute(null);

        Assert.Equal([shell.ActiveTab!.CurrentPath], asked);
    }

    /// <summary>
    /// Three folders selected opens THREE windows. Acting on the single row a
    /// context menu hands over is the documented fault in OpenInNewTab's own
    /// comment, and it is not being reintroduced one entry below it.
    /// </summary>
    [AvaloniaFact]
    public void Open_in_new_window_takes_the_whole_selection()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;
        var asked = new List<string?>();

        shell.NewWindowRequested += (_, folder) => asked.Add(folder);

        foreach (var name in new[] { "one", "two", "three" })
            pane.SelectedEntries.Add(Folder(name));

        shell.OpenInNewWindowCommand.Execute(pane.SelectedEntries[0]);

        Assert.Equal(3, asked.Count);
    }

    /// <summary>Folders only, the mirror of Enter — there is no opening a text
    /// file in a file-manager window.</summary>
    [AvaloniaFact]
    public void And_the_files_among_them_are_left_alone()
    {
        var shell = Shell();
        var pane = shell.ActiveTab!;
        var asked = new List<string?>();

        shell.NewWindowRequested += (_, folder) => asked.Add(folder);

        pane.SelectedEntries.Add(Folder("one"));
        pane.SelectedEntries.Add(File("a.txt"));

        shell.OpenInNewWindowCommand.Execute(null);

        Assert.Single(asked);
    }

    /// <summary>
    /// The sidebar row is gated exactly like the row above it, which is to say
    /// not at all: both references put "open in new tab" on every node of the
    /// navigation pane, and a bin you can open in a tab is a bin you can open
    /// in a window.
    /// </summary>
    [AvaloniaFact]
    public void The_bin_can_be_opened_in_a_window_like_it_can_in_a_tab()
    {
        var shell = Shell();
        var asked = new List<string?>();

        shell.NewWindowRequested += (_, folder) => asked.Add(folder);

        var bin = new PlaceItemViewModel(new Place
        {
            Id = "bin",
            Label = "Recycle Bin",
            Path = VirtualPaths.Trash,
            Kind = PlaceKind.Virtual,
            Icon = "user-trash",
        });

        Assert.False(bin.HasRealPath, "the fixture is not a virtual row any more");

        shell.OpenPlaceInNewWindowCommand.Execute(bin);

        Assert.Equal([VirtualPaths.Trash], asked);
    }

    // ---- transfers, drives and shares --------------------------------------

    /// <summary>
    /// **The failure that costs files rather than a sentence.** The eject veto
    /// used to ask one window's list, so window A could "safely remove" a stick
    /// window B was still filling. The copy here belongs ENTIRELY to the other
    /// shell.
    /// </summary>
    [AvaloniaFact]
    public async Task A_drive_the_other_windows_transfer_is_using_is_not_ejected()
    {
        var root = Directory.CreateTempSubdirectory("vaktari-stick").FullName;
        var places = new OneStick(root);

        var here = Shell(places);
        var other = Shell();

        here.AllRunning = () => other.Running;

        await here.Sidebar.ReloadAsync();
        Settle();

        var handle = new OperationHandle { Paths = [Path.Combine(root, "videos")] };
        handle.Begin(1, totalBytes: 0);
        other.ActiveTab!.Adopt(handle);

        Assert.Empty(here.Running);

        var row = here.Sidebar.Groups.SelectMany(g => g.Places).First(p => p.CanEject);

        await here.EjectPlaceCommand.ExecuteAsync(row);

        Assert.Equal(0, places.Calls);
        Assert.Contains("STICK", here.ActiveTab!.Status);
    }

    /// <summary>
    /// A window with a transfer still running says so before it goes.
    ///
    /// Closing one used to kill its transfer and nothing survived to notice,
    /// because the process was ending too. With a second window the process
    /// carries on and the handle goes on writing with no bar showing it.
    ///
    /// The assertion on cancelling is on the TOKEN, not on State: Cancel()
    /// signals the worker and the worker calls Cancelled(), so a handle with no
    /// engine behind it stays Running for ever.
    /// </summary>
    [AvaloniaFact]
    public void A_window_with_a_transfer_still_running_says_so_before_it_closes()
    {
        var shell = Shell();

        Assert.Null(shell.RunningDescription());

        var handle = new OperationHandle { Paths = [Path.Combine(Path.GetTempPath(), "big.iso")] };
        handle.Begin(1, totalBytes: 0);
        shell.ActiveTab!.Adopt(handle);

        var question = shell.RunningDescription();

        Assert.NotNull(question);
        Assert.Contains("transfer", question);

        shell.CancelAllOperations();

        Assert.True(handle.Token.IsCancellationRequested,
                    "the transfer was left running after the question was answered");
    }

    /// <summary>
    /// **Only the last window out may stop the shares.** IFileSharing's own doc
    /// says StopAllAsync is "called on shutdown so nothing outlives the app",
    /// and platform.Sharing is ONE object for every window — so closing one of
    /// two windows was killing the other's server while its Sharing section
    /// went on listing the folder as served.
    /// </summary>
    [AvaloniaFact]
    public async Task Only_the_last_window_out_may_stop_the_shares()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            var services = founder.Services;

            Assert.True(services.IsLastWindow, "one window is the last window");

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            Assert.False(services.IsLastWindow,
                         "with two windows open, closing one must not stop the other's shares");

            var peer = services.Windows.First(w => !ReferenceEquals(w, founder));
            await services.ReleaseAsync(peer);

            Assert.True(services.IsLastWindow);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    // ---- the desktop's own requests ----------------------------------------

    /// <summary>
    /// A folder handed over by the desktop must land somewhere. `Active` is
    /// assigned from Activated, which is a FOCUS event — it is null before
    /// anything is focused, and again once the focused window closes, and a
    /// null there would silently discard the folder, which as a default file
    /// manager is the whole job.
    /// </summary>
    [AvaloniaFact]
    public async Task A_desktop_request_goes_to_a_window_that_is_still_open()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var services = founder.Services;
            var peer = services.Windows.First(w => !ReferenceEquals(w, founder));

            // Showing it focused it, which is the only thing that ever assigns
            // Active.
            Assert.Same(peer, services.Active);

            await services.ReleaseAsync(peer);

            Assert.Same(founder, services.ForDesktopRequest);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    // ---- what a closed window lets go of -----------------------------------

    /// <summary>
    /// **Measured before it was fixed**: after Dispose(), CutMarks.Mark still
    /// set this shell's CutPaths, because Dispose only stopped the rate timer
    /// and tore down the panes. CutMarks is a static that outlives every
    /// window, so a shell that never unsubscribes stays reachable from it — and
    /// reactive — for the life of the process.
    /// </summary>
    [AvaloniaFact]
    public void A_closed_shell_stops_hearing_about_cut_marks()
    {
        CutMarks.Clear();

        var shell = Shell();

        var live = Path.Combine(Path.GetTempPath(), "while-open.txt");
        CutMarks.Mark([live]);
        Settle();

        Assert.Contains(live, shell.CutPaths);

        shell.Dispose();

        var after = Path.Combine(Path.GetTempPath(), "after-close.txt");
        CutMarks.Mark([after]);
        Settle();

        Assert.DoesNotContain(after, shell.CutPaths);
    }

    /// <summary>
    /// And the same for shares, which had nothing watching it at all.
    ///
    /// **The sharing provider is ONE object for every window** — it is
    /// platform.Sharing, handed to each shell — so a shell that never lets go
    /// stays reachable from it for the life of the process and goes on
    /// rebuilding its share list after the window has gone. Nothing exercised
    /// this: no test gave a shell a sharing provider and then disposed it, so
    /// the unsubscribe could be deleted with the whole suite green.
    ///
    /// Counted through Active, which RefreshShares reads exactly once per
    /// notification — so the count is "how many times did this shell react",
    /// which is the question, rather than what it made of the answer.
    /// </summary>
    [AvaloniaFact]
    public void A_closed_shell_stops_hearing_about_shares()
    {
        var sharing = new Sharing();

        var shell = Own(new ShellViewModel(new Inert(), sharing: sharing));
        shell.Start(null, Path.GetTempPath());

        var beforeClose = sharing.Reads;

        sharing.Raise();
        Settle();

        Assert.True(sharing.Reads > beforeClose, "a live shell ignored the provider");

        var afterOpen = sharing.Reads;

        shell.Dispose();

        sharing.Raise();
        Settle();

        Assert.Equal(afterOpen, sharing.Reads);
    }

    /// <summary>Answers nothing and announces on demand: what is under test is
    /// whether the shell is still listening, not what it hears.</summary>
    private sealed class Sharing : IFileSharing
    {
        public int Reads { get; private set; }

        public bool IsAvailable => true;
        public string? UnavailableReason => null;

        public IReadOnlyList<ShareSession> Active
        {
            get { Reads++; return []; }
        }

        public void Raise() => Changed?.Invoke(this, EventArgs.Empty);

        public Task<ShareSession> StartAsync(string path, bool writable, CancellationToken ct)
            => throw new NotSupportedException("this test never starts one");

        public Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct)
            => throw new NotSupportedException("this test installs nothing");

        public Task StopAsync(ShareSession session) => Task.CompletedTask;
        public Task StopAllAsync() => Task.CompletedTask;

        public event EventHandler? Changed;
    }

    /// <summary>
    /// The same for the sidebar: the places provider is ONE object shared by
    /// every window's sidebar, so a closed window's sidebar would go on
    /// rebuilding rows for a visual tree that has gone.
    /// </summary>
    [AvaloniaFact]
    public void A_closed_sidebar_stops_reloading_itself()
    {
        var places = new OneStick(Path.GetTempPath());

        // Through the SHELL, because that is the only route a real window has
        // to it: the window disposes the shell and the shell disposes what it
        // built.
        var shell = new ShellViewModel(new Inert(), places: places);
        shell.Start(null, Path.GetTempPath());

        Assert.Equal(1, places.Subscribers);

        shell.Dispose();

        Assert.Equal(0, places.Subscribers);
    }

    /// <summary>
    /// The eject veto asks the FAMILY, so a window has to be able to see what
    /// the others are doing. This is the wiring that makes that possible; the
    /// veto itself is tested above, over two shells.
    /// </summary>
    [AvaloniaFact]
    public async Task Every_windows_transfers_are_visible_to_the_others()
    {
        await SaveAsync();
        PaneViewModel.Search = null;

        var founder = new MainWindow();

        try
        {
            founder.Show();
            Settle();

            founder.Shell.NewWindowCommand.Execute(null);
            Settle();

            var peer = founder.Services.Windows.First(w => !ReferenceEquals(w, founder));

            var handle = new OperationHandle { Paths = [Path.Combine(Path.GetTempPath(), "big.iso")] };
            handle.Begin(1, totalBytes: 0);
            peer.Shell.ActiveTab!.Adopt(handle);

            Assert.Empty(founder.Shell.Running);

            var everywhere = founder.Shell.AllRunning?.Invoke().ToList();

            Assert.NotNull(everywhere);
            Assert.Contains(handle, everywhere);
        }
        finally
        {
            CloseAll(founder.Services);
        }
    }

    // ---- the eighth context-menu entry -------------------------------------

    /// <summary>
    /// The new row answers its OWN preference. Sharing "Open in new tab"'s
    /// switch would mean turning that off silently removed this too, on a page
    /// whose entire purpose is per-entry control.
    /// </summary>
    [AvaloniaFact]
    public void The_new_window_row_answers_its_own_preference()
    {
        var before = AppSettings.Current;

        AppSettings.Apply(before with
        {
            ContextMenu = before.ContextMenu with { ShowOpenInNewWindow = false },
        });

        var shell = Shell();
        shell.ActiveTab!.SelectedEntries.Add(Folder("one"));

        Assert.False(shell.ShowOpenInNewWindowInMenu);
        Assert.True(shell.ShowOpenInNewTabInMenu,
                    "the tab row was taken away by a preference that is not its own");
    }

    /// <summary>The preference reaches the file and comes back, like the seven
    /// beside it.</summary>
    /// <summary>
    /// **AvaloniaFact, not Fact.** The dialog's constructor asks the font
    /// manager which fonts are installed, and that needs an application — so
    /// this only ever passed because an Avalonia test ran before it in the same
    /// assembly. Run on its own it threw "Unable to locate IFontManagerImpl",
    /// on a clean tree, which is a test that depends on its neighbours rather
    /// than on the thing it is testing.
    /// </summary>
    [AvaloniaFact]
    public void The_new_window_preference_survives_the_settings_dialog()
    {
        var state = new SettingsState
        {
            ContextMenu = new ContextMenuSettings { ShowOpenInNewWindow = false },
        };

        var model = new SettingsViewModel(state);

        Assert.False(model.MenuOpenInNewWindow);

        model.MenuOpenInNewWindow = true;
        model.SaveCommand.Execute(null);

        Assert.True(model.Result.ContextMenu.ShowOpenInNewWindow);
    }

    // ---- guards ------------------------------------------------------------
    //
    // The four below and the two markup ones cannot fail for an unwritten
    // feature: they read the application's own source and would pass against
    // any code that happens to contain the same text. They exist to catch a
    // regression, and each still has a real one-line mutation that reddens it.

    private static string Window() => RepoSource.Ui("MainWindow.axaml.cs");

    /// <summary>
    /// GUARD. The dialog awaits ShowDialog and cannot be driven headlessly, so
    /// this reads OnClosing instead.
    ///
    /// "Instead", not "before": `else if` is exclusion, so a window with a
    /// transfer running and six tabs open asks ONE question, and the sentence
    /// carries the tab count when both would have applied.
    /// </summary>
    [Fact]
    public void The_closing_window_asks_about_the_transfer_instead_of_the_tabs()
    {
        var body = RepoSource.Body(
            Window(), "private async void OnClosing(object? sender, WindowClosingEventArgs e)");

        var transfer = body.IndexOf("RunningDescription()", StringComparison.Ordinal);
        var tabs = body.IndexOf("ConfirmClosingMultipleTabs", StringComparison.Ordinal);

        Assert.True(transfer >= 0, "the transfer question is not asked on close");
        Assert.True(tabs > transfer, "the tabs question is asked first, or not at all");

        Assert.Contains("else if (AppSettings.Current.General.ConfirmClosingMultipleTabs",
                        body, StringComparison.Ordinal);

        Assert.Contains("_shell.CancelAllOperations();", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD. AskConflict is a process-wide static that every window assigns,
    /// so it captured whichever window was constructed LAST — and after that
    /// one closed, one that is gone.
    /// </summary>
    [Fact]
    public void The_conflict_dialog_picks_its_owner_when_it_is_asked()
    {
        var source = Window();

        Assert.Contains("var owner = _services.Active ?? this;", source, StringComparison.Ordinal);
        Assert.Contains("new ConflictWindow(model).ShowDialog(owner)", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD. Both of the desktop's routes resolve the window when the request
    /// LANDS. `services.Active?.OpenPaths(...)` compiles perfectly well without
    /// the fallback, and so does a bare `OpenPaths(...)` — it is inside the
    /// instance constructor — which is exactly why this is easy to reintroduce.
    /// </summary>
    [Fact]
    public void The_desktops_requests_are_not_pinned_to_the_window_that_subscribed()
    {
        var source = Window();

        Assert.Contains(
            "services.ForDesktopRequest?.OpenPaths(paths, activate: true)",
            source, StringComparison.Ordinal);

        Assert.Contains(
            "services.ForDesktopRequest?.OnShowRequested(request)",
            source, StringComparison.Ordinal);

        Assert.Contains(
            "services.ForDesktopRequest?.OpenPaths(Program.StartupPaths, activate: false)",
            source, StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD. The share teardown is a PROCESS-wide one, and the answer has to
    /// be taken before the release below removes this window from the list —
    /// after it, the second-to-last window looks like the last.
    /// </summary>
    [Fact]
    public void The_share_teardown_is_gated_on_the_last_window()
    {
        var body = RepoSource.Body(
            Window(), "private async void OnClosing(object? sender, WindowClosingEventArgs e)");

        var asked = body.IndexOf("var last = _services.IsLastWindow;", StringComparison.Ordinal);
        var released = body.IndexOf("_services.ReleaseAsync(this)", StringComparison.Ordinal);
        var gate = body.IndexOf("if (last)", StringComparison.Ordinal);
        var stop = body.IndexOf("_shell.StopAllSharesAsync()", StringComparison.Ordinal);

        Assert.True(asked >= 0, "nothing asks whether this is the last window");
        Assert.True(released > asked, "the answer is taken after the window has left the list");
        Assert.True(gate > released && stop > gate, "the shares are stopped ungated");
    }

    /// <summary>
    /// GUARD. The theme provider belongs to the shared platform and there is no
    /// seam to hand a test a fake one, so this reads OnClosed.
    /// </summary>
    [Fact]
    public void A_closed_window_stops_following_the_desktop_theme()
    {
        var body = RepoSource.Body(
            Window(), "private void OnClosed(object? sender, EventArgs e)");

        Assert.Contains("_theme.Changed -= _onThemeChanged;", body, StringComparison.Ordinal);
        Assert.Contains("_shell.Dispose();", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD. Both folder menus and the view flyout reach the new-window
    /// commands, each bound to its own one — and the listing row carries its
    /// OWN visibility gate rather than the tab row's.
    /// </summary>
    [Fact]
    public void Both_folder_menus_offer_a_new_window()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        Assert.Contains("OpenInNewWindowCommand", markup, StringComparison.Ordinal);
        Assert.Contains("OpenPlaceInNewWindowCommand", markup, StringComparison.Ordinal);
        Assert.Contains("NewWindowCommand", markup, StringComparison.Ordinal);
        Assert.Contains("ShowOpenInNewWindowInMenu", markup, StringComparison.Ordinal);

        // The sidebar's row is deliberately ungated, like its twin above it.
        var sidebarRow = markup.IndexOf(
            "Header=\"Open in new window\"\n                                            Command=",
            StringComparison.Ordinal);

        Assert.True(sidebarRow > 0, "the sidebar's row is not where this test looks for it");

        var next = markup.IndexOf("</MenuItem>", sidebarRow, StringComparison.Ordinal);
        var end = next < 0 ? markup.IndexOf("<MenuItem", sidebarRow + 1, StringComparison.Ordinal) : next;

        Assert.DoesNotContain("IsVisible", markup[sidebarRow..end], StringComparison.Ordinal);
    }

    /// <summary>
    /// GUARD. The context-menu settings page exists to answer one question per
    /// entry, so every flag on ContextMenuSettings gets a checkbox of its own.
    /// Sharing "Open in new tab"'s switch would make one box answer two.
    /// </summary>
    [Fact]
    public void Each_context_menu_entry_has_its_own_toggle()
    {
        var settings = RepoSource.Ui("SettingsWindow.axaml");

        var flags = typeof(ContextMenuSettings).GetProperties()
            .Select(p => p.Name)
            .Where(name => name.StartsWith("Show", StringComparison.Ordinal))
            .ToList();

        // Eight of Dolphin's nine. A reflection walk that stopped matching
        // would report nothing and pass.
        Assert.Equal(8, flags.Count);

        foreach (var flag in flags)
        {
            var binding = "{Binding Menu" + flag["Show".Length..] + "}";

            Assert.Contains(binding, settings, StringComparison.Ordinal);
        }
    }
}
