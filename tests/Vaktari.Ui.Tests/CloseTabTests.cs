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
