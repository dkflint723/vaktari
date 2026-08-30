using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// "Add to places" is one visible row filled by one of two commands — pin the
/// selected folder when there is one, pin the folder being looked at
/// otherwise. The old menu showed both at once and left the reader to work
/// out which acted on what.
/// </summary>
public sealed class PlacesMenuRowTests
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

    private static ShellViewModel Shell()
    {
        var shell = new ShellViewModel(new Inert());
        shell.Start(null, Path.GetTempPath());
        return shell;
    }

    [AvaloniaFact]
    public void A_selected_folder_fills_the_row_with_the_selection_command()
    {
        var shell = Shell();

        shell.ActiveTab!.SelectedEntry = new FileEntry(
            "docs", Path.Combine(Path.GetTempPath(), "docs"),
            0, DateTimeOffset.UnixEpoch, EntryFlags.Directory);

        Assert.True(shell.ShowAddSelectionToPlaces);
        Assert.False(shell.ShowAddCurrentToPlaces);
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
