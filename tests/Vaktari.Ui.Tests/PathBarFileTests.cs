using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Pasting a FILE path into the location bar.
///
/// **It used to fail as a missing directory.** The typed path went straight to
/// the directory enumerator, so the pane showed the operating system's own "The
/// directory name is invalid." over an empty listing — while the route that
/// receives paths from the desktop had resolved a file to its folder all along.
/// </summary>
public sealed class PathBarFileTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-pathbar").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Lists what is really on disk, so navigation lands somewhere
    /// real.</summary>
    private sealed class RealFolder : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;

            if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);

            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(p => new FileEntry(
                    Path.GetFileName(p), p, 0, DateTimeOffset.UnixEpoch,
                    Directory.Exists(p) ? EntryFlags.Directory : EntryFlags.None))
                .ToList();

            yield return entries;
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

    private PaneViewModel Pane()
        => new(new RealFolder(), null, null) { CurrentPath = _root };

    [AvaloniaFact]
    public async Task A_typed_file_path_opens_the_folder_that_holds_it()
    {
        var folder = Path.Combine(_root, "documents");
        Directory.CreateDirectory(folder);

        var file = Path.Combine(folder, "report.pdf");
        await File.WriteAllTextAsync(file, "x");

        var pane = Pane();
        pane.PathText = file;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(folder, pane.CurrentPath);
    }

    /// <summary>
    /// And the file is highlighted rather than launched: somebody pasting a
    /// path is asking to be shown it, and opening it would be a side effect
    /// nobody requested.
    /// </summary>
    [AvaloniaFact]
    public async Task The_file_is_selected_rather_than_opened()
    {
        var file = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(file, "x");

        var pane = Pane();
        pane.PathText = file;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(_root, pane.CurrentPath);
        Assert.Equal(file, pane.SelectedEntry?.FullPath);
    }

    /// <summary>A folder still navigates exactly as it always did.</summary>
    [AvaloniaFact]
    public async Task A_typed_folder_path_still_navigates()
    {
        var folder = Path.Combine(_root, "pictures");
        Directory.CreateDirectory(folder);

        var pane = Pane();
        pane.PathText = folder;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(folder, pane.CurrentPath);
    }
}
