using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Session;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// A tab's layout and grouping reach the session store when they change.
///
/// **Switching a tab to tiles, or grouping it, was saved only by luck.** Both
/// are written by ToTabState, but the shell's list of pane properties worth
/// saving named neither — so the change was carried only by whatever marked the
/// session next, and a crash or a kill before that restored the old layout. The
/// scale that moves with the view was no help: it is equal in every layout by
/// default, and an equal assignment raises nothing.
///
/// Through the shell rather than the pane, and asked of the store, for the
/// reason ColumnChooserTests gives: the gap is in who listens, not in the pane.
/// </summary>
public sealed class TabLayoutIsWorthSavingTests : OwnedViewModels
{
    [AvaloniaFact]
    public void Switching_the_layout_is_worth_saving()
    {
        var (shell, store) = Started();
        var tab = shell.ActiveTab!;

        Assert.NotEqual(ViewMode.Grid, tab.View);

        var heard = store.Heard;

        tab.View = ViewMode.Grid;

        Assert.True(store.Heard > heard, "the session store was not told the layout changed");
        Assert.Equal(ViewMode.Grid, store.Last!.Windows[0].Panes[0].Tabs[0].View);
    }

    [AvaloniaFact]
    public void Grouping_is_worth_saving()
    {
        var (shell, store) = Started();
        var tab = shell.ActiveTab!;

        Assert.NotEqual(GroupMode.Kind, tab.GroupBy);

        var heard = store.Heard;

        tab.GroupBy = GroupMode.Kind;

        Assert.True(store.Heard > heard, "the session store was not told the grouping changed");
        Assert.Equal(GroupMode.Kind, store.Last!.Windows[0].Panes[0].Tabs[0].GroupBy);
    }

    private (ShellViewModel Shell, Listening Store) Started()
    {
        var store = new Listening();
        var shell = Own(new ShellViewModel(new Inert(), store: store));

        shell.Start(null, Path.GetTempPath());

        return (shell, store);
    }

    private sealed class Listening : ISessionStore
    {
        public int Heard { get; private set; }
        public SessionState? Last { get; private set; }

        public SessionState? Load() => null;

        public void NotifyChanged(SessionState state)
        {
            Heard++;
            Last = state;
        }

        public ValueTask FlushAsync(CancellationToken ct) => ValueTask.CompletedTask;
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
