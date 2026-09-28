using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The gates the redesigned listing menus hide rows on, each against the
/// command it answers for.
///
/// **A row whose command refuses where the menu opened is not drawn** — that
/// was the rule the menus already claimed ("hide, don't disable") and in a
/// dozen places did not keep: the gate asked something narrower than the
/// command did. Each test here sets a pane where the command refuses or is
/// wrong and asserts the gate says so, and where it can the command is run as
/// well, so the gate and the refusal are checked against each other rather
/// than against a comment. ListingMenusTests watches the same rows vanish on a
/// real window; these are the reasons.
/// </summary>
public sealed class ListingMenuGatesTests : OwnedViewModels
{
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

    private PaneViewModel Pane(string at, IApplicationLauncher? launcher = null, IScriptRunner? scripts = null)
        => Own(new PaneViewModel(new Inert(), launcher: launcher, scripts: scripts) { CurrentPath = at });

    private static FileEntry File(string path, bool directory = false)
        => new(Path.GetFileName(path) is { Length: > 0 } name ? name : path, path, 0,
               DateTimeOffset.Now, directory ? EntryFlags.Directory : EntryFlags.None);

    private static string Temp => Path.GetTempPath();

    // ---- This PC ----------------------------------------------------------------

    /// <summary>
    /// **A drive offered to be cut, renamed, binned and sent.** The gate asked
    /// CanActOnSelection, which excludes the bin and nothing else. The rename
    /// engine refuses a root, and every place on a drive is inside it — so
    /// Copy to and Move to would answer "cannot be sent into itself" for most
    /// of their list. In an ordinary folder the same selection keeps them all.
    /// </summary>
    [AvaloniaFact]
    public void A_drive_in_this_pc_cannot_be_moved_renamed_or_binned()
    {
        var drives = Pane(VirtualPaths.Computer);
        drives.SelectedEntry = File(Path.GetPathRoot(Temp)!, directory: true);

        Assert.True(drives.CanActOnSelection, "Open and Copy went with the rest");
        Assert.False(drives.CanMoveSelection);

        var folder = Pane(Temp);
        folder.SelectedEntry = File(Path.Combine(Temp, "adir"), directory: true);

        Assert.True(folder.CanMoveSelection);
    }

    // ---- the bin ------------------------------------------------------------------

    /// <summary>
    /// **Open with was drawn in the bin, and the command refused.** The list
    /// fills for any file selection; OpenWithApp says "already in the bin".
    /// </summary>
    [AvaloniaFact]
    public void Open_with_is_not_offered_in_the_bin_where_it_refuses()
    {
        var bin = Pane(VirtualPaths.Trash);
        bin.SelectedEntry = File(Path.Combine(Temp, "gone.txt"));
        bin.OpenWithOptions.Add(new LaunchOption("Notepad", "notepad.exe", null));

        Assert.True(bin.HasOpenWithOptions, "no options, so this proves nothing");
        Assert.False(bin.CanOpenWith);

        bin.OpenWithApp(bin.OpenWithOptions[0]);

        Assert.StartsWith("already in", bin.Status, StringComparison.Ordinal);

        var folder = Pane(Temp);
        folder.SelectedEntry = File(Path.Combine(Temp, "here.txt"));
        folder.OpenWithOptions.Add(new LaunchOption("Notepad", "notepad.exe", null));

        Assert.True(folder.CanOpenWith);
    }

    /// <summary>
    /// **Copy and Scripts refuse nothing in the bin, and are wrong there.**
    /// Both read the selection's paths, which in the bin are where each item
    /// USED to be — so Copy put a path on the clipboard that the next Paste
    /// copied from whatever lives there now, and a script was handed it too.
    /// Copy's row reads CanActOnSelection; the scripts row reads
    /// CanRunScriptsOnSelection.
    /// </summary>
    [AvaloniaFact]
    public void Copy_and_scripts_are_not_offered_in_the_bin()
    {
        var runner = new Scripts(Path.Combine(Temp, "vaktari-gates-" + Guid.NewGuid().ToString("N")[..8]));
        runner.Add("tidy");

        var bin = Pane(VirtualPaths.Trash, scripts: runner);
        bin.RefreshScripts();
        bin.SelectedEntry = File(Path.Combine(Temp, "gone.txt"));

        Assert.True(bin.HasScripts, "no scripts, so this proves nothing");
        Assert.False(bin.CanActOnSelection);
        Assert.False(bin.CanRunScriptsOnSelection);

        var folder = Pane(Temp, scripts: runner);
        folder.RefreshScripts();
        folder.SelectedEntry = File(Path.Combine(Temp, "here.txt"));

        Assert.True(folder.CanRunScriptsOnSelection);
    }

    /// <summary>
    /// **A script offered in a search could not start.** The item menu showed
    /// Scripts wherever a row was selected outside the bin, and the run hands
    /// the script the folder on screen as its working directory — in a
    /// search, Recent, This PC or a scan, the listing's internal path, which
    /// the process refuses to start in. Not offered there, and refused there
    /// if reached, with nothing handed to the runner.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("vaktari:search:report::everywhere")]
    [InlineData(VirtualPaths.Files)]
    [InlineData(VirtualPaths.Computer)]
    public async Task Scripts_run_only_where_there_is_a_folder_to_run_in(string listing)
    {
        var runner = new Scripts(Path.Combine(Temp, "vaktari-gates-" + Guid.NewGuid().ToString("N")[..8]));
        var script = runner.Add("tidy");

        var view = Pane(listing, scripts: runner);
        view.RefreshScripts();
        view.SelectedEntry = File(Path.Combine(Temp, "here.txt"));

        Assert.False(view.CanRunScriptsOnSelection);

        await view.RunScriptCommand.ExecuteAsync(script);

        Assert.Empty(runner.Handed);

        var folder = Pane(Temp, scripts: runner);
        folder.RefreshScripts();
        folder.SelectedEntry = File(Path.Combine(Temp, "here.txt"));

        Assert.True(folder.CanRunScriptsOnSelection);
    }

    // ---- listings that are not folders ------------------------------------------

    /// <summary>
    /// **Duplicate was drawn in a search, Recent and This PC, and refused.**
    /// It writes the copy into the folder on screen, and RefusedVirtualDestination
    /// turns every listing that is not one away — its row asked only
    /// CanActOnSelection.
    /// </summary>
    [AvaloniaTheory]
    [InlineData("vaktari:search:report::everywhere")]
    [InlineData(VirtualPaths.Files)]
    [InlineData(VirtualPaths.Computer)]
    public void Duplicate_is_offered_only_where_it_can_write(string listing)
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Temp);

        var pane = shell.ActiveTab!;

        pane.SelectedEntry = File(Path.Combine(Temp, "here.txt"));
        Assert.True(shell.ShowDuplicateInMenu, "not offered in a real folder either");

        pane.CurrentPath = listing;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ShowDuplicateInMenu);

        pane.DuplicateSelected();

        // This PC's rows are drives, and a drive is refused in the words every
        // other verb uses for one — see VolumeRefusalTests'
        // Duplicate_on_a_drive_says_what_every_other_verb_says.
        Assert.Equal(
            listing == VirtualPaths.Computer
                ? VolumeRoots.Refusal
                : "this listing is a view, not a folder — open a real folder first",
            pane.Status);
    }

    /// <summary>
    /// **The admin terminal was offered "here" in a search.** Its gate excluded
    /// the bin and Recent; a search, This PC and the scans handed the terminal
    /// the listing's internal path. The ordinary terminal row has always
    /// wanted a real folder.
    /// </summary>
    [AvaloniaFact]
    public void The_admin_terminal_wants_a_real_folder()
    {
        var launcher = new Elevating();

        var search = Pane("vaktari:search:report::everywhere", launcher);
        search.AdminRequested = true;

        Assert.False(search.ShowAdminEntries);

        search.OpenAdminTerminalHereCommand.Execute(null);

        Assert.Null(launcher.TerminalIn);

        var folder = Pane(Temp, launcher);
        folder.AdminRequested = true;

        Assert.True(folder.ShowAdminEntries);
    }

    /// <summary>
    /// **A network share was offered for a drive root and for a listing's
    /// internal path.** CopypartyShare refuses a root outright ("refusing to
    /// share the whole filesystem"), and a search or This PC is not a folder at
    /// all. A folder inside a drive may be shared.
    /// </summary>
    [AvaloniaFact]
    public void A_network_share_is_offered_only_for_a_real_folder_that_is_not_a_root()
    {
        var shell = Own(new ShellViewModel(new Inert(), sharing: new Sharing()));

        Assert.False(shell.CanNetworkShare(Path.GetPathRoot(Temp)));
        Assert.False(shell.CanNetworkShare(VirtualPaths.Computer));
        Assert.False(shell.CanNetworkShare("vaktari:search:report::everywhere"));
        Assert.False(shell.CanNetworkShare(null));

        Assert.True(shell.CanNetworkShare(Path.Combine(Temp, "adir")));

        // And with no backend at all, nowhere.
        Assert.False(Own(new ShellViewModel(new Inert())).CanNetworkShare(Path.Combine(Temp, "adir")));
    }

    /// <summary>
    /// **Select what differs and Copy what is newer were offered whenever the
    /// window was split**, and with either side showing a view the copy
    /// refused and the select had nothing to select. They want two folders;
    /// the switch that turns comparing on keeps the split's gate alone.
    /// </summary>
    [AvaloniaFact]
    public void The_actions_on_a_comparison_want_two_folders()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Temp);

        Assert.False(shell.CanActAcrossSides, "offered with no other side");

        shell.ToggleSplitCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(shell.IsSplit);
        Assert.True(shell.CanActAcrossSides, "not offered across two folders");

        shell.OtherGroup!.ActiveTab!.CurrentPath = VirtualPaths.Computer;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(shell.CanActAcrossSides);

        shell.RequestCopyAcrossCommand.Execute(null);

        Assert.Equal("cannot copy across: one side is a view, not a folder", shell.ActiveTab!.Status);
    }

    // ---- the background menu acts on the folder --------------------------------

    /// <summary>
    /// **"Add this folder to places" hid whenever a folder was selected**,
    /// because the two Add rows shared one slot in the one menu. They are in
    /// different menus now, and a right-click on empty space keeps the
    /// selection — so the background row must not care what is selected.
    /// </summary>
    [AvaloniaFact]
    public void Add_this_folder_stays_on_the_background_menu_with_a_folder_selected()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Temp);

        shell.ActiveTab!.SelectedEntry = File(Path.Combine(Temp, "adir"), directory: true);

        Assert.True(shell.ShowAddSelectionToPlaces, "the item menu's row went");
        Assert.True(shell.ShowAddCurrentToPlaces, "the background menu's row hid for a selection");
    }

    /// <summary>
    /// **The background menu's Properties describes the folder**, whatever is
    /// selected — the Alt+Enter command describes the selection, which a
    /// right-click on empty space keeps. And only where there is a folder.
    /// </summary>
    [AvaloniaFact]
    public void Folder_properties_describe_the_folder_whatever_is_selected()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Temp);

        var asked = new List<string>();
        shell.ShowPropertiesRequested += (_, path) => asked.Add(path);

        var pane = shell.ActiveTab!;
        pane.SelectedEntry = File(Path.Combine(Temp, "here.txt"));

        shell.ShowFolderPropertiesCommand.Execute(null);

        Assert.Equal([pane.CurrentPath], asked);

        pane.CurrentPath = VirtualPaths.Computer;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        shell.ShowFolderPropertiesCommand.Execute(null);

        Assert.Single(asked);
    }

    // ---- scripts -------------------------------------------------------------------

    /// <summary>
    /// **The background menu's scripts run with nothing selected.** A
    /// right-click on empty space keeps the selection, so the item menu's
    /// command — which hands a script the selection — would have run it on
    /// files the menu is not about.
    /// </summary>
    [AvaloniaFact]
    public async Task A_script_from_the_background_menu_is_handed_no_selection()
    {
        var runner = new Scripts(Path.Combine(Temp, "vaktari-gates-" + Guid.NewGuid().ToString("N")[..8]));
        var script = runner.Add("tidy");

        var pane = Pane(Temp, scripts: runner);
        pane.SelectedEntry = File(Path.Combine(Temp, "here.txt"));

        await pane.RunScriptHereCommand.ExecuteAsync(script);

        Assert.Equal([], runner.Handed.Single());

        await pane.RunScriptCommand.ExecuteAsync(script);

        Assert.Equal([Path.Combine(Temp, "here.txt")], runner.Handed[1]);
    }

    /// <summary>
    /// **The submenu's last row opens the scripts folder, and makes it if it is
    /// missing.** The runners create it once, when they are built; a folder
    /// deleted since would have been handed to the launcher to fail on. This
    /// row replaced "Add your own scripts", the standing row both menus showed
    /// whenever there were no scripts, and is the feature's only way in.
    /// </summary>
    [AvaloniaFact]
    public async Task Open_scripts_folder_makes_the_folder_and_opens_it()
    {
        var where = Path.Combine(Temp, "vaktari-gates-" + Guid.NewGuid().ToString("N")[..8]);
        var runner = new Scripts(where);
        var launcher = new Elevating();

        try
        {
            var pane = Pane(Temp, launcher, runner);

            pane.RefreshScripts();
            pane.RefreshScriptRows();

            // With no scripts, the submenu is the folder row alone.
            Assert.Equal([ScriptsFolderRow.Instance], pane.ScriptRows);

            Assert.False(Directory.Exists(where));

            await pane.RunScriptHereCommand.ExecuteAsync(ScriptsFolderRow.Instance);

            Assert.True(Directory.Exists(where), "the folder was not made");
            Assert.Equal(where, launcher.Opened);

            // With one, the script, a rule, and the folder row last.
            runner.Add("tidy");
            pane.RefreshScripts();
            pane.RefreshScriptRows();

            Assert.Equal(3, pane.ScriptRows.Count);
            Assert.IsType<ScriptCommand>(pane.ScriptRows[0]);
            Assert.IsType<Separator>(pane.ScriptRows[1]);
            Assert.Same(ScriptsFolderRow.Instance, pane.ScriptRows[2]);
        }
        finally
        {
            if (Directory.Exists(where)) Directory.Delete(where, recursive: true);
        }
    }

    /// <summary>
    /// The background menu's Scripts is shown wherever there is a folder and a
    /// runner, scripts or none; the item menu's only when there is a script.
    /// </summary>
    [AvaloniaFact]
    public void The_folder_scripts_row_needs_a_folder_and_a_runner_but_no_scripts()
    {
        var runner = new Scripts(Path.Combine(Temp, "vaktari-gates-" + Guid.NewGuid().ToString("N")[..8]));

        Assert.True(Pane(Temp, scripts: runner).CanRunScriptsHere);
        Assert.False(Pane(VirtualPaths.Computer, scripts: runner).CanRunScriptsHere);
        Assert.False(Pane(Temp).CanRunScriptsHere);
    }

    // ---- the tab menu ----------------------------------------------------------------

    /// <summary>
    /// **A lone tab offered to close the others; the last tab, the ones to its
    /// right.** Each closing row is gated on there being something to close,
    /// which the group tells every tab whenever the tabs change — opening,
    /// closing and reordering all move the answer for tabs that were not
    /// touched.
    /// </summary>
    [AvaloniaFact]
    public void The_tab_menus_closing_rows_follow_the_tabs()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert()) { CurrentPath = Temp }));

        var first = group.AddTab(Temp);

        Assert.False(first.HasOtherTabs);
        Assert.False(first.HasTabsToTheRight);

        var second = group.AddTab(Temp);

        Assert.True(first.HasOtherTabs);
        Assert.True(first.HasTabsToTheRight);
        Assert.True(second.HasOtherTabs);
        Assert.False(second.HasTabsToTheRight);

        group.Tabs.Move(1, 0);

        Assert.True(second.HasTabsToTheRight);
        Assert.False(first.HasTabsToTheRight);

        group.CloseTab(second);

        Assert.False(first.HasOtherTabs);
        Assert.False(first.HasTabsToTheRight);
    }

    /// <summary>
    /// **"Reopen closed tab" was on every tab menu, and did nothing until a
    /// tab had been closed** (0.11.1 changelog check). It is gated like the
    /// closing rows: shown once this side has a closed tab, on every tab of
    /// the side, and gone again once the last one is put back.
    /// </summary>
    [AvaloniaFact]
    public void The_tab_menus_reopen_row_follows_the_closed_tabs()
    {
        var group = new PaneGroupViewModel(() => Own(new PaneViewModel(new Inert()) { CurrentPath = Temp }));

        var first = group.AddTab(Temp);
        var second = group.AddTab(Temp);

        Assert.False(first.CanReopenClosedTab);
        Assert.False(second.CanReopenClosedTab);

        group.CloseTab(second);

        Assert.True(first.CanReopenClosedTab);

        var back = group.ReopenClosedTab();

        Assert.NotNull(back);
        Assert.False(first.CanReopenClosedTab, "nothing is left to reopen");
        Assert.False(back.CanReopenClosedTab);
    }

    /// <summary>
    /// **A tab's menu in the inactive half acted on the active half.** Close
    /// other tabs, Close tabs to the right and Duplicate all used ActiveGroup,
    /// so with the left side active, "Close other tabs" on a right-side tab
    /// closed the LEFT side's tabs. They act on the side the tab belongs to.
    /// </summary>
    [AvaloniaFact]
    public void A_tab_menu_acts_on_the_side_its_tab_is_on()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Temp);

        shell.ToggleSplitCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        shell.Left.AddTab(Temp);
        shell.Left.AddTab(Temp);
        shell.Right!.AddTab(Temp);

        shell.ActivateGroup(shell.Left);

        var left = shell.Left.Tabs.Count;
        var right = shell.Right.Tabs[0];

        Assert.Equal(2, shell.Right.Tabs.Count);
        Assert.True(left >= 3);

        shell.DuplicateTabCommand.Execute(right);

        Assert.Equal(left, shell.Left.Tabs.Count);
        Assert.Equal(3, shell.Right.Tabs.Count);

        shell.CloseTabsToTheRightCommand.Execute(right);

        Assert.Equal(left, shell.Left.Tabs.Count);
        Assert.Single(shell.Right.Tabs);

        shell.Right.AddTab(Temp);
        shell.ActivateGroup(shell.Left);

        shell.CloseOtherTabsCommand.Execute(right);

        Assert.Equal(left, shell.Left.Tabs.Count);
        Assert.Same(right, Assert.Single(shell.Right.Tabs));

        // And Reopen closed tab, whose row is gated on the tab's own side: the
        // left side has closed nothing, so reopening there would do nothing
        // under a row that promised a tab.
        shell.ActivateGroup(shell.Left);
        Assert.True(right.CanReopenClosedTab);

        shell.ReopenClosedTabCommand.Execute(right);

        Assert.Equal(left, shell.Left.Tabs.Count);
        Assert.Equal(2, shell.Right.Tabs.Count);
    }

    // ---- doubles -----------------------------------------------------------------------

    /// <summary>A scripts folder that lists what it is told to, and records
    /// what each run was handed.</summary>
    private sealed class Scripts(string directory) : IScriptRunner
    {
        private readonly List<ScriptCommand> _scripts = [];

        public string ScriptsDirectory => directory;

        public List<IReadOnlyList<string>> Handed { get; } = [];

        public ScriptCommand Add(string name)
        {
            var script = new ScriptCommand(name, Path.Combine(directory, name));
            _scripts.Add(script);
            return script;
        }

        public IReadOnlyList<ScriptCommand> Discover() => [.. _scripts];

        public ValueTask<string> RunAsync(
            ScriptCommand script, string workingDirectory, IReadOnlyList<string> paths, CancellationToken ct)
        {
            Handed.Add([.. paths]);
            return ValueTask.FromResult("");
        }
    }

    /// <summary>A launcher that can elevate, and records what it was asked to
    /// open.</summary>
    private sealed class Elevating : IApplicationLauncher
    {
        public bool CanElevate => true;
        public bool CanElevateFile(string path) => false;

        public string? Opened { get; private set; }
        public string? TerminalIn { get; private set; }

        public void OpenElevated(string path) { }

        public void OpenElevatedTerminal(string directory, TerminalOption? terminal = null)
            => TerminalIn = directory;

        public Exception? Open(string path)
        {
            Opened = path;
            return null;
        }

        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    /// <summary>A sharing backend that exists and is never asked to share.</summary>
    private sealed class Sharing : Vaktari.Core.Sharing.IFileSharing
    {
        public bool IsAvailable => true;
        public string? UnavailableReason => null;
        public IReadOnlyList<Vaktari.Core.Sharing.ShareSession> Active => [];

        public event EventHandler? Changed { add { } remove { } }

        public Task<Vaktari.Core.Sharing.ShareSession> StartAsync(
            string path, Vaktari.Core.Sharing.ShareOptions options, CancellationToken ct)
            => throw new InvalidOperationException("not in a test");

        public Task StopAsync(Vaktari.Core.Sharing.ShareSession session) => Task.CompletedTask;
        public Task StopAllAsync() => Task.CompletedTask;

        public Task<bool> InstallAsync(IProgress<string> progress, CancellationToken ct) => Task.FromResult(true);
    }
}
