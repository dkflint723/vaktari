using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Vcs;
using Vaktari.Tests;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Renaming a folder in a real window, with the platform's own engine and
/// real watchers — where, on Windows, Vaktari's own handles are what refused
/// the rename (rename-notes, plan §1e): a background tab in a subfolder, the
/// watch a tab keeps on a repository's .git, and a tab in another window.
/// Each of these fails on Windows with the hand-over taken away; on Linux,
/// where nothing of ours can block a rename, they prove the tabs follow.
///
/// The in-use prompt is here too, because only a window has a keyboard.
/// </summary>
public sealed class HandoverWindowTests : OwnedViewModels
{
    private readonly IVersionControl? _vcsBefore = PaneViewModel.Vcs;

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "vaktari-handover-win-" + Guid.NewGuid().ToString("N")[..10]);

    public HandoverWindowTests() => Directory.CreateDirectory(_root);

    public override void Dispose()
    {
        base.Dispose();

        PaneViewModel.Vcs = _vcsBefore;

        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }
    }

    private string At(params string[] parts) => Path.Combine([_root, .. parts]);

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    private static async Task Until(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (!condition() && DateTime.UtcNow < deadline)
        {
            Settle();
            await Task.Delay(5);
        }

        Settle();

        Assert.True(condition(), because);
    }

    private static void CloseAll(MainWindow founder)
    {
        foreach (var window in founder.Services.Windows.ToList().AsEnumerable().Reverse())
        {
            try { window.Close(); }
            catch (Exception ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        }

        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (founder.Services.Windows.Count > 0 && DateTime.UtcNow < deadline)
        {
            Settle();
            Thread.Sleep(1);
        }
    }

    /// <summary>A real window on <see cref="_root"/>, its first tab loaded.</summary>
    private async Task<MainWindow> OpenAsync()
    {
        UseSearch(PaneViewModel.Search);

        var window = new MainWindow();
        window.Show();
        Settle();

        await window.Shell.ActiveTab!.NavigateAsync(_root);
        await window.Shell.ActiveTab.RefreshAsync();
        await Until(() => window.Shell.ActiveTab.IsLoaded, "the window never listed the folder");

        return window;
    }

    private static FileEntry Row(PaneViewModel pane, string name)
        => pane.Entries.Single(e => e.Name == name);

    /// <summary>
    /// A file made in <paramref name="pane"/>'s folder after it has loaded
    /// arrives as a row — which only a running watcher can deliver, the
    /// listing having been read before the file existed.
    /// </summary>
    private static async Task Watching(PaneViewModel pane, string because)
    {
        var name = "late-" + Guid.NewGuid().ToString("N")[..6] + ".txt";

        File.WriteAllText(Path.Combine(pane.CurrentPath, name), "x");

        await Until(() => pane.Entries.Any(e => e.Name == name), because);
    }

    /// <summary>
    /// **A background tab in a subfolder refused the rename of the folder
    /// above it** — its watcher, on Windows. The hold lets it go, the folder
    /// is renamed, and the tab follows it and watches its new place.
    /// </summary>
    [AvaloniaFact]
    public async Task A_background_tab_in_a_subfolder_does_not_refuse_the_rename_above_it()
    {
        Directory.CreateDirectory(At("photos", "2026"));

        var window = await OpenAsync();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var inside = shell.Left.AddTab(At("photos", "2026"), activate: false);
            await Until(() => inside.IsLoaded, "the background tab never loaded");

            Assert.True(await here.TryRenameAsync(Row(here, "photos"), "pictures"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(inside.CurrentPath, At("pictures", "2026")) && inside.IsLoaded && !inside.IsLoading,
                $"the background tab is at {inside.CurrentPath}, loaded {inside.IsLoaded}");

            await Watching(inside, "the tab that followed is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>A version-control backend that calls any folder holding a
    /// .git folder a repository, without running git.</summary>
    private sealed class Repositories : IVersionControl
    {
        public string Name => "fake";
        public bool IsAvailable => true;

        public string? FindRoot(string folder)
            => Directory.Exists(Path.Combine(folder, ".git")) ? folder : null;

        public Task<VcsSnapshot?> StatusAsync(string folder, CancellationToken ct)
            => Task.FromResult(FindRoot(folder) is { } root
                ? new VcsSnapshot(root, new Dictionary<string, VcsState>())
                : null);
    }

    private static object? RepoWatcher(PaneViewModel pane)
        => typeof(PaneViewModel)
            .GetField("_repoWatcher", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(pane);

    /// <summary>
    /// **The watch a tab keeps on its repository's .git refused the rename**
    /// of the repository — the tab's own folder watch does not, being on the
    /// folder itself (plan §1a). The hold lets it go, and the tab watches the
    /// .git of the renamed repository afterwards.
    /// </summary>
    [AvaloniaFact]
    public async Task The_watch_on_a_repository_s_git_folder_lets_go()
    {
        Directory.CreateDirectory(At("project", ".git"));

        var window = await OpenAsync();
        PaneViewModel.Vcs = new Repositories();

        try
        {
            var shell = window.Shell;
            var here = shell.ActiveTab!;

            var repo = shell.Left.AddTab(At("project"), activate: false);
            await Until(() => repo.IsLoaded && RepoWatcher(repo) is not null, "the tab never watched the repository");

            Assert.True(await here.TryRenameAsync(Row(here, "project"), "renamed"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(repo.CurrentPath, At("renamed")) && repo.IsLoaded && RepoWatcher(repo) is not null,
                $"the tab is at {repo.CurrentPath}, repository watch {(RepoWatcher(repo) is null ? "gone" : "back")}");
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>A tab in another window holds the folder as much as one in
    /// this window does.</summary>
    [AvaloniaFact]
    public async Task A_tab_in_another_window_does_not_refuse_it_either()
    {
        Directory.CreateDirectory(At("photos", "2026"));

        var window = await OpenAsync();

        try
        {
            window.Shell.NewWindowCommand.Execute(null);
            Settle();

            var peer = Assert.Single(window.Services.Windows, w => !ReferenceEquals(w, window));
            var elsewhere = peer.Shell.ActiveTab!;

            await elsewhere.NavigateAsync(At("photos", "2026"));
            await Until(() => elsewhere.IsLoaded && PathRules.Same(elsewhere.CurrentPath, At("photos", "2026")),
                "the other window never loaded the subfolder");

            var here = window.Shell.ActiveTab!;

            Assert.True(await here.TryRenameAsync(Row(here, "photos"), "pictures"), $"the rename was refused: {here.Status}");

            await Until(() => PathRules.Same(elsewhere.CurrentPath, At("pictures", "2026")) && elsewhere.IsLoaded,
                $"the other window's tab is at {elsewhere.CurrentPath}");

            await Watching(elsewhere, "the other window's tab is not watching its new place");
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- the in-use prompt -----------------------------------------------------

    private static void Offer(MainWindow window, InUseOffer offer)
        => typeof(MainWindow)
            .GetMethod("OnInUseRequested", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [window.Shell.ActiveTab, offer]);

    private static Border Bar(MainWindow window) => window.FindControl<Border>("PromptBar")!;

    private static Button TryAgain(MainWindow window) => window.FindControl<Button>("PromptConfirm")!;

    /// <summary>
    /// The bar names the item and why, says where to look on Windows (and
    /// nowhere else), and starts with Try again holding the keyboard. Escape
    /// closes it from anywhere.
    /// </summary>
    [AvaloniaFact]
    public async Task The_in_use_bar_says_what_is_true_and_starts_on_Try_again()
    {
        var window = await OpenAsync();

        try
        {
            var tried = 0;
            Offer(window, new InUseOffer("could not rename “photos” — something has a file inside that folder open",
                () => { tried++; return Task.FromResult(true); }));
            Settle();

            Assert.True(Bar(window).IsVisible);
            Assert.Equal("could not rename “photos” — something has a file inside that folder open",
                         window.FindControl<TextBlock>("PromptLabel")!.Text);
            Assert.Equal("Try again", TryAgain(window).Content);
            Assert.True(TryAgain(window).IsVisible && window.FindControl<Button>("PromptCancel")!.IsVisible);
            Assert.True(TryAgain(window).IsFocused, "Try again does not have the keyboard");

            var hint = window.FindControl<TextBlock>("PromptHint")!.Text ?? "";

            if (OperatingSystem.IsWindows())
            {
                Assert.Contains("Resource Monitor", hint);
                Assert.Contains("File Locksmith", hint);
            }
            else
            {
                Assert.DoesNotContain("Resource Monitor", hint);
            }

            Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(Bar(window)));

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Settle();

            Assert.False(Bar(window).IsVisible);
            Assert.Equal(0, tried);
            Assert.NotEqual(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(Bar(window)));
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>
    /// **Enter from the listing must not try again** (review finding 13). The
    /// confirmations take Enter from anywhere; this bar sits under a live
    /// listing, where Enter opens a row, so only its own buttons answer it.
    /// </summary>
    [AvaloniaFact]
    public async Task Enter_in_the_listing_does_not_try_again_and_Enter_on_the_button_does()
    {
        Directory.CreateDirectory(At("photos"));

        var window = await OpenAsync();

        try
        {
            var pane = window.Shell.ActiveTab!;
            await pane.RefreshAsync();
            await Until(() => pane.Entries.Any(e => e.Name == "photos"), "the folder never listed");

            var tried = 0;
            Offer(window, new InUseOffer("could not rename “photos” — something has a file inside that folder open",
                () => { tried++; return Task.FromResult(true); }));
            Settle();

            window.UpdateLayout();
            Settle();

            // Selected as well as focused: Enter in the listing OPENS the
            // selection, so with the bar's guard gone this Enter would go into
            // the folder — which is what the test has to be able to see.
            pane.SelectedEntry = Row(pane, "photos");
            Settle();

            var row = window.GetVisualDescendants().OfType<ListBoxItem>()
                            .First(i => i.IsVisible && i.DataContext is FileEntry { Name: "photos" });

            row.Focus();
            Settle();

            Assert.True(row.IsFocused, "the row never took the keyboard");

            var at = pane.CurrentPath;

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();

            Assert.Equal(0, tried);
            Assert.True(Bar(window).IsVisible, "Enter in the listing closed the bar");
            Assert.True(PathRules.Same(at, pane.CurrentPath), "Enter in the listing went into the folder under the bar");

            TryAgain(window).Focus();
            Settle();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Settle();

            Assert.Equal(1, tried);
            Assert.False(Bar(window).IsVisible);
        }
        finally
        {
            CloseAll(window);
        }
    }

    /// <summary>
    /// One tenant: with another question in the bar, the offer does not take
    /// it over. The status line has already said what happened.
    /// </summary>
    [AvaloniaFact]
    public async Task An_offer_does_not_take_the_bar_from_another_question()
    {
        var window = await OpenAsync();

        try
        {
            window.Shell.ConnectCommand.Execute(null);
            Settle();

            var asking = window.FindControl<TextBlock>("PromptLabel")!.Text;

            Offer(window, new InUseOffer("could not rename “x” — something else has that file open", () => Task.FromResult(true)));
            Settle();

            Assert.Equal(asking, window.FindControl<TextBlock>("PromptLabel")!.Text);
        }
        finally
        {
            CloseAll(window);
        }
    }

    // ---- against another program, on Windows -----------------------------------

    private static TextBox Box(MainWindow window)
    {
        window.UpdateLayout();
        Settle();

        return window.GetVisualDescendants().OfType<TextBox>()
                     .Single(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);
    }

    private static bool Editing(MainWindow window)
    {
        window.UpdateLayout();
        Settle();

        return window.GetVisualDescendants().OfType<TextBox>()
                     .Any(t => t.Classes.Contains(MainWindow.RenameBoxClass) && t.IsVisible);
    }

    /// <summary>
    /// The whole gesture, against another program holding a file inside the
    /// folder: F2, a new name, Enter — refused, said in words, with Try again;
    /// the other program lets go; Enter on Try again renames it.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task F2_on_a_folder_another_program_has_open_offers_Try_again_and_it_works()
    {
        Directory.CreateDirectory(At("photos"));

        var window = await OpenAsync();
        var holder = AnotherProgram.HoldingFile(At("photos", "held.txt"));

        try
        {
            var pane = window.Shell.ActiveTab!;
            await pane.RefreshAsync();
            await Until(() => pane.Entries.Any(e => e.Name == "photos"), "the folder never listed");

            pane.SelectedEntry = Row(pane, "photos");
            pane.BeginRenameCommand.Execute(null);
            Settle();

            Box(window).Text = "pictures";
            Settle();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            await Until(() => Bar(window).IsVisible, $"no offer appeared; the status line says: {pane.Status}");

            Assert.Equal("could not rename “photos” — something has a file inside that folder open",
                         window.FindControl<TextBlock>("PromptLabel")!.Text);
            Assert.True(holder.IsRunning, "the other program was closed");

            holder.Dispose();

            Assert.True(TryAgain(window).IsFocused);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);

            await Until(() => Directory.Exists(At("pictures")), "Try again did not rename it");
            await Until(() => pane.Entries.Any(e => e.Name == "pictures"), "the listing never showed the new name");

            Assert.False(Bar(window).IsVisible);
        }
        finally
        {
            holder.Dispose();
            CloseAll(window);
        }
    }

    /// <summary>
    /// **A Tab-stepping run stops at a file something else has open**, with the
    /// offer up and no box opened on the next file.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_rename_run_stops_at_a_file_another_program_has_open()
    {
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" })
            File.WriteAllText(At(name), name);

        var window = await OpenAsync();
        using var holder = AnotherProgram.HoldingFile(At("b.txt"));

        try
        {
            var pane = window.Shell.ActiveTab!;
            await pane.RefreshAsync();
            await Until(() => pane.Entries.Count(e => e.Name.EndsWith(".txt", StringComparison.Ordinal)) == 3, "the files never listed");

            pane.SelectedEntry = Row(pane, "a.txt");
            pane.BeginRenameCommand.Execute(null);
            Settle();

            Box(window).Text = "a2.txt";
            Settle();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);

            await Until(() => Editing(window) && Box(window).Text == "b.txt", "the run never stepped to b.txt");

            Box(window).Text = "b2.txt";
            Settle();
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);

            await Until(() => Bar(window).IsVisible, $"no offer appeared; the status line says: {pane.Status}");

            Assert.Equal("could not rename “b.txt” — something else has that file open",
                         window.FindControl<TextBlock>("PromptLabel")!.Text);
            Assert.False(Editing(window), "the run stepped on past the refused file");
            Assert.True(File.Exists(At("b.txt")));
            Assert.True(File.Exists(At("c.txt")));
        }
        finally
        {
            CloseAll(window);
        }
    }
}
