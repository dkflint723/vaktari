using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What a tab does, and the five things it did not.
///
///  - Ctrl+T reset hidden files, the layout, the sort, the grouping and the
///    zoom, so a new tab was a tab you had to set up again.
///  - "Open in new tab" jumped to the new tab, which is the opposite of what
///    the phrase means.
///  - Closing a tab threw its state away, so Ctrl+Shift+T had nothing to put
///    back and a tab closed by accident was gone.
///  - There was no Close others and no Close to the right, so with a dozen open
///    the only route was one at a time.
///  - A tab had no right-click menu at all.
/// </summary>
public sealed class TabBehaviourTests : OwnedViewModels
{
    private ShellViewModel Shell()
    {
        var shell = Own(new ShellViewModel(new Inert()));
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    // ---- a new tab looks like the one you were in ---------------------------

    [AvaloniaFact]
    public void A_new_tab_keeps_the_view_you_were_using()
    {
        var shell = Shell();
        var first = shell.ActiveTab!;

        first.ShowHidden = true;
        first.View = ViewMode.Grid;
        first.Sort = SortField.Size;
        first.SortDescending = true;
        first.GroupBy = GroupMode.Kind;
        first.ShowTypeColumn = true;

        shell.NewTabCommand.Execute(null);

        var second = shell.ActiveTab!;

        Assert.NotSame(first, second);
        Assert.True(second.ShowHidden, "hidden files were switched back off");
        Assert.Equal(ViewMode.Grid, second.View);
        Assert.Equal(SortField.Size, second.Sort);
        Assert.True(second.SortDescending);
        Assert.Equal(GroupMode.Kind, second.GroupBy);
        Assert.True(second.ShowTypeColumn);
    }

    // ---- opening in the background ------------------------------------------

    /// <summary>
    /// You ask for a new tab rather than opening the folder precisely so you
    /// can carry on where you are.
    /// </summary>
    [AvaloniaFact]
    public void Open_in_new_tab_does_not_take_you_there()
    {
        var shell = Shell();
        var here = shell.ActiveTab!;

        shell.OpenInNewTabCommand.Execute(new FileEntry(
            "sub", Path.Combine(Path.GetTempPath(), "sub"), 0,
            DateTimeOffset.UnixEpoch, EntryFlags.Directory));

        Assert.Same(here, shell.ActiveTab);
        Assert.Equal(2, shell.ActiveGroup.Tabs.Count);
    }

    /// <summary>Ctrl+T is the other way round: you asked for a tab to work
    /// in.</summary>
    [AvaloniaFact]
    public void Ctrl_T_does_take_you_there()
    {
        var shell = Shell();
        var here = shell.ActiveTab!;

        shell.NewTabCommand.Execute(null);

        Assert.NotSame(here, shell.ActiveTab);
    }

    // ---- reopening ----------------------------------------------------------

    [AvaloniaFact]
    public async Task A_closed_tab_can_be_reopened_where_it_was()
    {
        var shell = Shell();

        shell.NewTabCommand.Execute(null);

        var second = shell.ActiveTab!;
        await second.NavigateAsync(Path.GetTempPath());

        var was = second.CurrentPath;

        shell.CloseTabCommand.Execute(second);
        Assert.Single(shell.ActiveGroup.Tabs);

        shell.ReopenClosedTabCommand.Execute(null);

        Assert.Equal(2, shell.ActiveGroup.Tabs.Count);
        Assert.Equal(was, shell.ActiveTab!.CurrentPath);
    }

    /// <summary>And with nothing closed it does nothing rather than
    /// throwing.</summary>
    [AvaloniaFact]
    public void Reopening_with_nothing_closed_is_quiet()
    {
        var shell = Shell();

        shell.ReopenClosedTabCommand.Execute(null);

        Assert.Single(shell.ActiveGroup.Tabs);
    }

    // ---- closing several ----------------------------------------------------

    [AvaloniaFact]
    public void Close_other_tabs_keeps_the_one_you_asked_from()
    {
        var shell = Shell();

        shell.NewTabCommand.Execute(null);
        shell.NewTabCommand.Execute(null);

        var keep = shell.ActiveGroup.Tabs[1];

        shell.CloseOtherTabsCommand.Execute(keep);

        Assert.Same(keep, Assert.Single(shell.ActiveGroup.Tabs));
    }

    [AvaloniaFact]
    public void Close_tabs_to_the_right_leaves_the_ones_to_the_left()
    {
        var shell = Shell();

        shell.NewTabCommand.Execute(null);
        shell.NewTabCommand.Execute(null);
        shell.NewTabCommand.Execute(null);

        Assert.Equal(4, shell.ActiveGroup.Tabs.Count);

        var from = shell.ActiveGroup.Tabs[1];

        shell.CloseTabsToTheRightCommand.Execute(from);

        Assert.Equal(2, shell.ActiveGroup.Tabs.Count);
        Assert.Same(from, shell.ActiveGroup.Tabs[1]);
    }

    /// <summary>
    /// A side is never left with no tabs — an empty column with no way back is
    /// a dead end, which CloseTab has always refused and these must not get
    /// around.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_others_never_empties_the_side()
    {
        var shell = Shell();

        shell.CloseOtherTabsCommand.Execute(shell.ActiveTab);

        Assert.Single(shell.ActiveGroup.Tabs);
    }

    [AvaloniaFact]
    public void Duplicating_a_tab_opens_the_same_folder_again()
    {
        var shell = Shell();
        var here = shell.ActiveTab!;

        shell.DuplicateTabCommand.Execute(here);

        Assert.Equal(2, shell.ActiveGroup.Tabs.Count);
        Assert.Equal(here.CurrentPath, shell.ActiveTab!.CurrentPath);
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
}
