using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Closing the last tab.
///
/// **Ctrl+W and the tab's × were dead with one tab open.** The group refuses to
/// leave a side with no tabs, so with a single tab and no split both routes hit
/// that guard and returned — the × stayed drawn, clickable and tooltipped, and
/// did nothing. There was no Ctrl+Q either, so the keyboard could not close the
/// window at all. Explorer and every browser close the window instead.
/// </summary>
public sealed class CloseTabTests : OwnedViewModels
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

    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    [AvaloniaFact]
    public void Closing_the_only_tab_asks_the_window_to_close()
    {
        var shell = Shell();
        var asked = 0;

        shell.CloseRequested += (_, _) => asked++;

        Assert.Single(shell.Left.Tabs);

        shell.CloseTabCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    /// <summary>With more than one tab it still just closes the tab — the
    /// window is not going anywhere.</summary>
    [AvaloniaFact]
    public void Closing_one_of_several_tabs_leaves_the_window_alone()
    {
        var shell = Shell();
        var asked = 0;

        shell.CloseRequested += (_, _) => asked++;

        shell.NewTabCommand.Execute(null);
        Assert.Equal(2, shell.Left.Tabs.Count);

        shell.CloseTabCommand.Execute(null);

        Assert.Single(shell.Left.Tabs);
        Assert.Equal(0, asked);
    }

    /// <summary>
    /// **The bounded reopen history threw away the wrong end.** It was a Stack
    /// trimmed with Pop, and Pop takes the TOP — the tab just closed. So after
    /// ten closes the eleventh was pushed and discarded in the same breath, and
    /// every close after that was forgotten the instant it happened. Ctrl+Shift+T
    /// then put back the tenth-oldest closed tab, so the one case the feature
    /// exists for, the tab you have just shut by accident, was the one case it
    /// could not do.
    ///
    /// Twelve closes, because the bound is ten and the fault only shows past it.
    /// </summary>
    [AvaloniaFact]
    public void Reopen_puts_back_the_last_tab_closed_even_past_the_bound()
    {
        var shell = Shell();
        var group = shell.Left;

        // Twelve tabs with names of their own, closed newest-last, so the one
        // reopened says which it was.
        for (var i = 0; i < 12; i++)
            group.AddTab(Path.Combine(Path.GetTempPath(), "folder" + i));

        for (var i = 0; i < 12; i++)
            group.CloseTab(group.Tabs.First(t => t.CurrentPath.EndsWith("folder" + i, StringComparison.Ordinal)));

        var back = group.ReopenClosedTab();

        Assert.NotNull(back);
        Assert.EndsWith("folder11", back!.CurrentPath);
    }

    /// <summary>Still bounded: a history that grew without limit would hold a
    /// pane's whole state for every tab ever closed.</summary>
    [AvaloniaFact]
    public void The_reopen_history_stays_bounded()
    {
        var shell = Shell();
        var group = shell.Left;

        for (var i = 0; i < 14; i++)
            group.AddTab(Path.Combine(Path.GetTempPath(), "folder" + i));

        for (var i = 0; i < 14; i++)
            group.CloseTab(group.Tabs.First(t => t.CurrentPath.EndsWith("folder" + i, StringComparison.Ordinal)));

        var reopened = 0;
        while (group.CanReopenTab && reopened < 40)
        {
            group.ReopenClosedTab();
            reopened++;
        }

        Assert.Equal(10, reopened);
    }

    [AvaloniaFact]
    public void Quitting_asks_the_window_to_close()
    {
        var shell = Shell();
        var asked = 0;

        shell.CloseRequested += (_, _) => asked++;

        shell.QuitCommand.Execute(null);

        Assert.Equal(1, asked);
    }

    /// <summary>
    /// A split still collapses rather than closing the window: there is another
    /// side to fall back to, which is what the user means by closing this one.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_last_tab_of_a_split_side_collapses_the_split()
    {
        var shell = Shell();
        var asked = 0;

        shell.CloseRequested += (_, _) => asked++;

        shell.ToggleSplitCommand.Execute(null);
        Assert.True(shell.IsSplit);

        shell.CloseTabCommand.Execute(null);

        Assert.False(shell.IsSplit);
        Assert.Equal(0, asked);
    }
}
