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
///
/// **Then it landed on the folder and did nothing else.** The fix stopped at
/// highlighting the row, and this file pinned that as deliberate: the argument
/// was that launching something because a path was pasted is a side effect
/// nobody asked for. It is the wrong reading of the gesture. Nobody types a
/// full path to a file and presses Enter in order to see its name in a list;
/// Explorer and Dolphin both open it; and Enter means "open this" everywhere
/// else in Vaktari. The file is now opened AND shown where it lives, and the
/// test that argued otherwise is below, rewritten rather than deleted so the
/// reason the answer changed is in the same place the old answer was.
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
                    Path.GetFileName(p), p, 0, DateTimeOffset.UnixEpoch, FlagsFor(p)))

                // Honoured here so a hidden file is genuinely ABSENT from the
                // listing, which is the only way to exercise a typed path with
                // no row behind it. The real providers filter in the same
                // place; a fake that returned everything would have made the
                // no-row test unwritable.
                .Where(e => options.IncludeHidden || !e.IsConcealed)
                .ToList();

            yield return entries;
        }

        /// <summary>
        /// Concealed by either platform's rule: the leading dot Linux goes by,
        /// and the attribute Windows goes by. Both, so one test file named with
        /// a dot is absent from this listing wherever the suite runs. More
        /// generous than the real Windows provider on purpose — Windows itself
        /// does not conceal a dot-file, and all the no-row test needs is a
        /// listing that does not hold the row.
        /// </summary>
        private static EntryFlags FlagsFor(string p)
        {
            var flags = Directory.Exists(p) ? EntryFlags.Directory : EntryFlags.None;

            if (Path.GetFileName(p).StartsWith('.')
                || File.GetAttributes(p).HasFlag(FileAttributes.Hidden))
                flags |= EntryFlags.Hidden;

            return flags;
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

    /// <summary>Records what was handed to the desktop, so "it opened" is an
    /// assertion rather than a hope.</summary>
    private sealed class RecordingLauncher : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public Exception? Open(string path)
        {
            Opened.Add(path);
            return null;
        }
        public void OpenTerminal(string directory) { }
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
        public void OpenWith(string path, LaunchOption option) { }
    }

    private PaneViewModel Pane(RecordingLauncher? launcher = null)
        => new(new RealFolder(), null, launcher) { CurrentPath = _root };

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
    /// **This test used to be called The_file_is_selected_rather_than_opened**,
    /// and it asserted the opposite of what it asserts now. Selecting was the
    /// whole of the answer; it is now half of it. Enter on a path is the same
    /// request as Enter on a row, and both references answer it by opening.
    /// </summary>
    [AvaloniaFact]
    public async Task The_file_is_opened_rather_than_only_shown()
    {
        var file = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(file, "x");

        var launcher = new RecordingLauncher();
        var pane = Pane(launcher);
        pane.PathText = file;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(file, Assert.Single(launcher.Opened));
    }

    /// <summary>
    /// And it is still shown where it lives. Opening replaced the old answer
    /// rather than being added beside it would have lost the useful half: the
    /// pane has to end up on the folder with the row lit, so the next gesture
    /// has somewhere to happen.
    /// </summary>
    [AvaloniaFact]
    public async Task The_folder_is_shown_with_the_row_lit_as_well()
    {
        var file = Path.Combine(_root, "notes.txt");
        await File.WriteAllTextAsync(file, "x");

        var pane = Pane(new RecordingLauncher());
        pane.PathText = file;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(_root, pane.CurrentPath);
        Assert.Equal(file, pane.SelectedEntry?.FullPath);
    }

    /// <summary>
    /// A concealed file has no row while hidden files are off, and whether a
    /// row is drawn must not decide whether a path somebody typed in full
    /// opens — that would be a silent nothing for a deliberate gesture.
    ///
    /// The selection is null rather than a blank row: FileEntry is a record
    /// struct, so the FirstOrDefault this method used handed back a
    /// default(FileEntry) that is not null and has a null FullPath, lighting
    /// nothing while HasSelection went on saying there was a selection.
    /// </summary>
    [AvaloniaFact]
    public async Task A_file_the_listing_does_not_show_still_opens()
    {
        var file = Path.Combine(_root, ".secret.txt");
        await File.WriteAllTextAsync(file, "x");

        var launcher = new RecordingLauncher();
        var pane = Pane(launcher);

        Assert.False(pane.ShowHidden);

        pane.PathText = file;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.DoesNotContain(pane.Entries, e => e.FullPath == file);
        Assert.Equal(file, Assert.Single(launcher.Opened));
        Assert.Null(pane.SelectedEntry);
    }

    /// <summary>
    /// A folder still navigates exactly as it always did — and is navigated
    /// rather than handed to the desktop, which would open a second file
    /// manager window over the one you are looking at.
    /// </summary>
    [AvaloniaFact]
    public async Task A_typed_folder_path_still_navigates()
    {
        var folder = Path.Combine(_root, "pictures");
        Directory.CreateDirectory(folder);

        var launcher = new RecordingLauncher();
        var pane = Pane(launcher);
        pane.PathText = folder;

        await pane.NavigateToPathTextCommand.ExecuteAsync(null);

        Assert.Equal(folder, pane.CurrentPath);
        Assert.Empty(launcher.Opened);
    }
}
