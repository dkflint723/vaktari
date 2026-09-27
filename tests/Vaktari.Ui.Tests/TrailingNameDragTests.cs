using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// **A drag or a copy out of a plainly opened pane carried the neighbour.**
/// From the seventh review round's second pass (HOLE 7-D): the drag's payload
/// was built through the storage provider, which on Windows folds
/// "…\report " to "…\report", so a Shift-drop moved "report" — which nobody
/// dragged — and a Ctrl-drop copied it, both reported Completed; the bin row
/// reads the same payload; and the clipboard's file list, which is what
/// Explorer pastes from, was built the same way.
///
/// Now a drag that would hand on any such name is refused whole, naming it,
/// and the clipboard keeps it out of the file list other programs read while
/// Vaktari's own list keeps it exactly. The first test is the round's repro,
/// with the one line that stood in for BeginDragAsync replaced by the real
/// builder (<see cref="DragPayload"/>), which the drag now calls. The engine
/// behind the bin drop is a recording; nothing reaches the real bin.
/// </summary>
public sealed class TrailingNameDragTests : OwnedViewModels
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "vaktari-hole7d-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Extended(string path) => @"\\?\" + path;

    private static void Clean(string root)
    {
        if (!Directory.Exists(Extended(root))) return;

        foreach (var f in Directory.GetFiles(Extended(root), "*", SearchOption.AllDirectories)) File.Delete(f);
        Directory.Delete(Extended(root), recursive: true);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Input);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task Dragging_a_trailing_named_row_never_carries_its_neighbour()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "vaktari-hole7d-" + Guid.NewGuid().ToString("N")[..8]);
        var src = Path.Combine(root, "src");
        var dst = Path.Combine(root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(@"\\?\" + Path.Combine(src, "report "), "the dragged file");
        File.WriteAllText(Path.Combine(src, "report"), "the neighbour nobody dragged");

        UseSearch(PaneViewModel.Search);
        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var shell = (ShellViewModel)typeof(MainWindow).GetProperty("Shell", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            var pane = Own(shell).ActiveTab!;
            await pane.NavigateAsync(dst);
            await pane.RefreshAsync();

            // What BeginDragAsync builds for the "report " row of a plainly opened pane.
            var row = Path.Combine(src, "report ");
            var (data, refusal) = await DragPayload.BuildAsync(window.StorageProvider, [row]);

            if (data is not null)
            {
                var listing = window.GetVisualDescendants().OfType<ListBox>().First(l => ReferenceEquals(l.DataContext, pane));
                var centre = new Point(listing.Bounds.Width / 2, listing.Bounds.Height / 2);
                listing.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, listing, listing.TranslatePoint(centre, window) ?? centre, KeyModifiers.Shift));
            }

            for (var i = 0; i < 100; i++) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }

            // RED: the payload names "…\report", and the neighbour has been moved.
            Assert.True(data is null, $"the drag carries {data?.Items.Count} item(s)");
            Assert.Contains("\"report \"", refusal ?? "", StringComparison.Ordinal);
            Assert.Equal("the neighbour nobody dragged", File.ReadAllText(Path.Combine(src, "report")));
            Assert.Equal("the dragged file", File.ReadAllText(Extended(row)));
        }
        finally
        {
            window.Close();
            foreach (var f in Directory.GetFiles(@"\\?\" + root, "*", SearchOption.AllDirectories)) File.Delete(f);
            Directory.Delete(@"\\?\" + root, recursive: true);
        }
    }

    /// <summary>
    /// **A drag of such a row, however it is spelled and whatever rides with
    /// it, is refused whole; an ordinary selection is carried as it was.**
    /// A drag that left one row out would move the rest and look as if it had
    /// moved them all. "\\?\" does not excuse it: what the receiver does with
    /// the prefix is its own business, and Explorer would open "report".
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task A_drag_holding_a_name_another_program_would_fold_is_refused_whole()
    {
        var src = Path.Combine(Root, "whole");
        Directory.CreateDirectory(src);
        var plain = Path.Combine(src, "plain.txt");
        File.WriteAllText(plain, "p");
        File.WriteAllText(Extended(Path.Combine(src, "report.")), "dot");
        Directory.CreateDirectory(Extended(Path.Combine(src, "album ")));

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            foreach (var refused in new[]
                     {
                         new[] { plain, Path.Combine(src, "report.") },
                         [Path.Combine(src, "album ")],
                         [Extended(Path.Combine(src, "report."))],
                     })
            {
                var (data, refusal) = await DragPayload.BuildAsync(window.StorageProvider, refused);

                Assert.Null(data);
                Assert.NotNull(refusal);
            }

            var (ordinary, none) = await DragPayload.BuildAsync(window.StorageProvider, [plain]);

            Assert.Null(none);
            Assert.NotNull(ordinary);
            Assert.Single(ordinary!.Items);
        }
        finally
        {
            window.Close();
            Clean(src);
        }
    }

    /// <summary>
    /// **The bin row is never handed the neighbour.** The drag of "report "
    /// builds no payload, so nothing is dropped on the bin; the ordinary file
    /// beside it, dragged alone, is — and the engine behind the bin, a
    /// recording, is asked for exactly that and nothing else.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task The_bin_row_is_never_handed_the_neighbour()
    {
        var src = Path.Combine(Root, "bin");
        Directory.CreateDirectory(src);
        var plain = Path.Combine(src, "plain.txt");
        File.WriteAllText(plain, "p");
        File.WriteAllText(Path.Combine(src, "report"), "the neighbour nobody dragged");
        File.WriteAllText(Extended(Path.Combine(src, "report ")), "the dragged file");

        var ops = new Recording();
        ShellViewModel.OperationsOverride = _ => ops;

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            var bin = window.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.DataContext is PlaceItemViewModel p && PathRules.Same(p.Path, VirtualPaths.Trash));

            Assert.True(bin is not null, "the sidebar has no bin row to drop on");

            foreach (var dragged in new[] { Path.Combine(src, "report "), plain })
            {
                var (data, _) = await DragPayload.BuildAsync(window.StorageProvider, [dragged]);

                if (data is null) continue;

                var centre = new Point(bin!.Bounds.Width / 2, bin.Bounds.Height / 2);
                bin.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, data, bin, bin.TranslatePoint(centre, window) ?? centre, KeyModifiers.None));
                Pump();
            }

            Assert.Equal(["trash " + plain], ops.Asked);
            Assert.Equal("the neighbour nobody dragged", File.ReadAllText(Path.Combine(src, "report")));
        }
        finally
        {
            ShellViewModel.OperationsOverride = null;
            window.Close();
            Clean(src);
        }
    }

    /// <summary>
    /// **The clipboard keeps the name out of the file list other programs
    /// paste from, and exactly in its own.** A Ctrl+C of "report " beside an
    /// ordinary file: the file list — CF_HDROP to Explorer — holds the
    /// ordinary file only, never "report"; Vaktari's own list holds both, so
    /// a paste back into Vaktari gets both as they are; and the status says
    /// which one only Vaktari can paste.
    /// </summary>
    [AvaloniaFact(Skip = OnlyOn.Windows, SkipUnless = nameof(OnlyOn.IsWindows), SkipType = typeof(OnlyOn))]
    public async Task The_clipboard_hands_other_programs_only_names_they_can_open()
    {
        var src = Path.Combine(Root, "clip");
        Directory.CreateDirectory(src);
        var plain = Path.Combine(src, "plain.txt");
        var trailing = Path.Combine(src, "report ");
        File.WriteAllText(plain, "p");
        File.WriteAllText(Path.Combine(src, "report"), "the neighbour nobody copied");
        File.WriteAllText(Extended(trailing), "the copied file");

        var window = new MainWindow { Width = 1200, Height = 1000 };

        try
        {
            window.Show();
            Pump();

            var clipboard = ClipboardService.ForWindow(window);
            var pane = Own(new PaneViewModel(new Silent(), clipboard: clipboard) { CurrentPath = src });

            var row = new FileEntry("report ", trailing, 0, DateTimeOffset.Now, EntryFlags.None);
            pane.SelectedEntry = row;
            pane.SelectedEntries.Add(row);
            pane.SelectedEntries.Add(new FileEntry("plain.txt", plain, 1, DateTimeOffset.Now, EntryFlags.None));

            await pane.CopySelectionToClipboardAsync();
            Pump();

            Assert.Contains("\"report \"", pane.Status, StringComparison.Ordinal);

            using (var data = await window.Clipboard!.TryGetDataAsync())
            {
                Assert.NotNull(data);

                var files = (await data!.TryGetFilesAsync() ?? []).Select(f => f.TryGetLocalPath()).ToList();

                Assert.Equal([plain], files);
            }

            var payload = await clipboard.GetFilesAsync();

            Assert.NotNull(payload);
            Assert.Equal(new[] { trailing, plain }.Order(StringComparer.Ordinal), payload!.Paths.Order(StringComparer.Ordinal));
            Assert.True(await clipboard.HasFilesAsync());

            // Only the trailing name: nothing for other programs, and still a
            // paste here — the Paste row must not grey out.
            pane.SelectedEntries.Clear();
            pane.SelectedEntries.Add(row);

            await pane.CopySelectionToClipboardAsync();
            Pump();

            Assert.True(await clipboard.HasFilesAsync(), "a copy only Vaktari can paste was not offered to Paste");
            Assert.Equal([trailing], (await clipboard.GetFilesAsync())!.Paths);
        }
        finally
        {
            window.Close();
            Clean(src);
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        Clean(Root);
        GC.SuppressFinalize(this);
    }

    /// <summary>A listing that lists nothing: the clipboard test names its
    /// rows by hand.</summary>
    private sealed class Silent : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct) => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct) => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);

        public string? GetParent(string path) => Path.GetDirectoryName(path);

        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>An engine that writes down what it is asked, and does none of it.</summary>
    private sealed class Recording : IFileOperations
    {
        public List<string> Asked { get; } = [];

        private IOperationHandle Done(string what, IEnumerable<string> paths)
        {
            Asked.Add(what + " " + string.Join(";", paths));

            var handle = new OperationHandle();
            handle.Complete();
            return handle;
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done("copy", sources);

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict) => Done("move", sources);

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Done("trash", paths);
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Done("delete", paths);

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct) => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }
        public IUndoGroup? BeginRenameGroup() => null;
        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
