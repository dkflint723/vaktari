using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Closing a tab in the half of a split that is not active.
///
/// **A middle click there disposed the tab and left it on screen.** The press
/// names its tab without activating the half it sits in, and the shell's
/// CloseTab asked the ACTIVE group every time — so the tab fell through to the
/// other side's CloseTab, which filed it under ITS reopen list, removed nothing
/// and disposed it. The tab stayed drawn in its own half with no watcher, no
/// settings subscription and no timers behind it, and Ctrl+Shift+T on the
/// other side put back a tab that had never been there. With one tab on the
/// active side the group's own guard returned first and the click did nothing.
///
/// The × and the tab's menu were never affected: a left press reaches the
/// window's ActivateGroupAt before the button sees it.
/// </summary>
public sealed class SplitTabCloseTests : OwnedViewModels
{
    private MainWindow? _real;

    public override void Dispose()
    {
        // A shown window flushes the session on close, and one left open is
        // torn down later on whatever thread xunit is on.
        _real?.Close();

        base.Dispose();
    }

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

    private static string Folder(string name) => Path.Combine(Path.GetTempPath(), "vaktari-split-" + name);

    /// <summary>
    /// A split with <paramref name="left"/> tabs on the left, which is active,
    /// and <paramref name="right"/> on the right.
    /// </summary>
    private ShellViewModel Split(int left, int right)
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Folder("left0"));

        for (var i = 1; i < left; i++) shell.Left.AddTab(Folder("left" + i));

        shell.ToggleSplit();

        var side = Assert.IsType<PaneGroupViewModel>(shell.Right);

        for (var i = 1; i < right; i++) side.AddTab(Folder("right" + i));

        shell.ActivateGroup(shell.Left);

        Assert.Equal(left, shell.Left.Tabs.Count);
        Assert.Equal(right, side.Tabs.Count);
        Assert.Same(shell.Left, shell.ActiveGroup);

        return shell;
    }

    /// <summary>The finding: the tab goes from its own half, and the active
    /// half is left exactly as it was — no tab lost, nothing filed to reopen.</summary>
    [AvaloniaFact]
    public void A_tab_closed_in_the_inactive_half_goes_from_that_half()
    {
        var shell = Split(left: 3, right: 2);
        var right = shell.Right!;
        var victim = right.Tabs[1];

        shell.CloseTabCommand.Execute(victim);

        Assert.DoesNotContain(victim, right.Tabs);
        Assert.Single(right.Tabs);

        Assert.Equal(3, shell.Left.Tabs.Count);
        Assert.False(shell.Left.CanReopenTab, "the other half filed the tab under its own reopen list");
        Assert.True(right.CanReopenTab, "the half the tab was closed from cannot put it back");
    }

    /// <summary>
    /// And the last tab of the inactive half collapses the split, which is
    /// what closing it from its own half has always done. Before the fix the
    /// active side's CloseTab refused a tab it did not hold, so this was a
    /// click that did nothing.
    /// </summary>
    [AvaloniaFact]
    public void The_last_tab_of_the_inactive_half_collapses_the_split()
    {
        var shell = Split(left: 2, right: 1);

        shell.CloseTabCommand.Execute(shell.Right!.Tabs[0]);

        Assert.False(shell.IsSplit);
        Assert.Equal(2, shell.Left.Tabs.Count);
        Assert.False(shell.Left.CanReopenTab);
    }

    /// <summary>
    /// **A group asked to close a tab it does not hold leaves it alone.** It
    /// used to file the tab under its own reopen list and dispose it, with the
    /// Remove quietly doing nothing — which is how the tab above came to stand
    /// in its half with nothing behind it. The shell names the right group now;
    /// this is the guard for the next caller that does not.
    /// </summary>
    [AvaloniaFact]
    public void A_group_leaves_alone_a_tab_it_does_not_hold()
    {
        var shell = Split(left: 2, right: 1);
        var foreign = shell.Right!.Tabs[0];
        var active = shell.Left.ActiveTab;

        shell.Left.CloseTab(foreign);

        Assert.Contains(foreign, shell.Right!.Tabs);
        Assert.Equal(2, shell.Left.Tabs.Count);
        Assert.Same(active, shell.Left.ActiveTab);
        Assert.False(shell.Left.CanReopenTab, "a tab that was never here was filed to reopen here");
    }

    // ---- the press itself --------------------------------------------------

    private static async Task Layout(MainWindow window)
    {
        for (var i = 0; i < 20; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// **The middle button activates the half it was pressed in, as the ×
    /// does.** Its branch returned before the ActivateGroupAt at the end of the
    /// press handler, so the shell was asked to close a tab with the other half
    /// active — which is how it came to use the wrong group — and the keyboard
    /// stayed over there afterwards.
    ///
    /// Pressed on a real window rather than called, because the branch being
    /// pinned is in the window's press handler and nowhere else.
    /// </summary>
    [AvaloniaFact]
    public async Task A_middle_click_on_a_tab_in_the_inactive_half_activates_that_half()
    {
        UseSearch(PaneViewModel.Search);

        var root = Directory.CreateTempSubdirectory("vaktari-split-press-").FullName;

        try
        {
            var window = _real = new MainWindow();

            window.Show();
            Dispatcher.UIThread.RunJobs();

            var shell = Assert.IsType<ShellViewModel>(window.DataContext);

            await shell.ActiveTab!.NavigateAsync(root);

            if (!shell.IsSplit) shell.ToggleSplit();

            var right = shell.Right!;

            right.AddTab(root);
            shell.ActivateGroup(shell.Left);

            await Layout(window);

            var victim = right.Tabs[^1];

            Assert.True(right.Tabs.Count >= 2, "the right half needs a tab to keep");
            Assert.Same(shell.Left, shell.ActiveGroup);

            var header = window.GetVisualDescendants()
                .OfType<TabStripItem>()
                .Single(item => ReferenceEquals(item.DataContext, victim) && item.IsVisible);

            // Near the left edge, clear of the close button on the right.
            var point = header.TranslatePoint(new Point(8, header.Bounds.Height / 2), window)
                        ?? throw new InvalidOperationException("the tab is not in the window");

            window.MouseDown(point, MouseButton.Middle);
            window.MouseUp(point, MouseButton.Middle);
            Dispatcher.UIThread.RunJobs();

            Assert.DoesNotContain(victim, right.Tabs);
            Assert.Same(right, shell.ActiveGroup);

            // Back to one side, so the session this window flushes on close
            // does not open the next test in this class split.
            shell.ToggleSplit();
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            _real?.Close();
            _real = null;

            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { /* a temp dir is not worth failing over */ }
        }
    }
}
