using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Add to places" is two rows in two menus now: the item menu's pins the
/// selected folder, the background menu's pins the folder being looked at.
/// The oldest menu showed both at once, side by side, and left the reader to
/// work out which acted on what; then one menu showed one of them, filled by
/// whichever command the selection called for. With the menu split, each row
/// is in the menu whose subject it names, and neither has to give way to the
/// other.
/// </summary>
public sealed class PlacesMenuRowTests : OwnedViewModels
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

    /// <summary>
    /// A selected folder puts the selection's row on the item menu — and
    /// **leaves the background menu's row where it is.** This asserted the
    /// opposite while the two shared one slot of one menu. A right-click on
    /// empty space keeps the selection, so hiding the folder's row for a
    /// selected folder would take "Add this folder to places" off the one menu
    /// that is about the folder, beside exactly the selection that made it go.
    /// </summary>
    [AvaloniaFact]
    public void A_selected_folder_fills_the_row_with_the_selection_command()
    {
        var shell = Shell();

        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "docs", Path.Combine(Path.GetTempPath(), "docs"),
            0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

        Assert.True(shell.ShowAddSelectionToPlaces);
        Assert.True(shell.ShowAddCurrentToPlaces);
    }

    [AvaloniaFact]
    public void Anything_else_fills_it_with_the_current_folder_command()
    {
        var shell = Shell();

        // A file selected — the selection command would fall back to the
        // current folder anyway, so the row that names the folder wins.
        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
            1, DateTimeOffset.UnixEpoch, EntryFlags.None);

        Assert.False(shell.ShowAddSelectionToPlaces);
        Assert.True(shell.ShowAddCurrentToPlaces);

        // And with no selection at all.
        shell.ActiveTab.SelectedEntry = null;

        Assert.False(shell.ShowAddSelectionToPlaces);
        Assert.True(shell.ShowAddCurrentToPlaces);
    }
}
