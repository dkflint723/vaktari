using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The bin is a view of things that are gone, and the two facts that make it
/// useful are the same two that made it dangerous.
///
/// **A bin row carries the item's ORIGINAL path.** That is what fills the Path
/// column and what lets Restore put a file back where it came from — and it
/// means a command reading the selection is aimed at a location the item no
/// longer occupies. Trash <c>notes.txt</c>, write a new <c>notes.txt</c>, then
/// press Shift+Delete on the bin row: the destroyed file was the NEW one. The
/// confirmation could not help, because it counts items rather than naming
/// them, and the row was still sitting there afterwards.
///
/// **CurrentPath in the bin is the literal string "vaktari:trash".** Every
/// create-and-paste path used it as the destination unchecked. On Linux that is
/// a legal relative name, so pasting made a folder called <c>vaktari:trash</c>
/// in the process's working directory, moved the files into it, deleted the
/// originals, and reported success. Windows escaped only because a colon is
/// illegal in a path — luck, not a guard, and not a thing to rely on.
///
/// Both are asserted against a fake that records rather than acts, because the
/// only honest test of "this must never be called" is one where calling it
/// would be observable.
/// </summary>
public sealed class BinIsNotAFolderTests : OwnedViewModels
{
    /// <summary>
    /// Records what it was asked to do and does none of it. Every method that
    /// could touch a disk throws instead: a guard that leaks is then a failed
    /// test rather than a deleted file on the machine running the suite.
    /// </summary>
    private sealed class RecordingOperations : IFileOperations
    {
        public List<string> Calls { get; } = [];

        private IOperationHandle Record(string what, IReadOnlyList<string> paths)
        {
            Calls.Add($"{what}: {string.Join(", ", paths)}");
            throw new InvalidOperationException(
                $"{what} reached the file system with {paths.Count} path(s)");
        }

        public IOperationHandle Copy(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Record($"copy -> {destination}", sources);

        public IOperationHandle Move(IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => Record($"move -> {destination}", sources);

        public IOperationHandle Trash(IReadOnlyList<string> paths) => Record("trash", paths);
        public IOperationHandle Delete(IReadOnlyList<string> paths) => Record("delete", paths);

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
        {
            Calls.Add($"rename: {path} -> {newName}");
            throw new InvalidOperationException("rename reached the file system");
        }

        public void RecordCreation(string path) => Calls.Add($"record: {path}");

        public bool CanUndo => false;
        public bool CanRedo => false;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    /// <summary>Answers structurally and reads nothing.</summary>
    private sealed class InertFileSystem : IFileSystemProvider
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

        public IDisposable Watch(string path, Action<FileSystemChange> onChange)
            => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>
    /// Records what it was asked to launch instead of launching it. Unlike the
    /// operations fake this does NOT throw: several of these tests want to see
    /// the launcher used, as the control that proves an empty list means the
    /// guard fired rather than the fake being inert.
    /// </summary>
    private sealed class RecordingLauncher : IApplicationLauncher
    {
        public List<string> Opened { get; } = [];

        public Exception? Open(string path)
        {
            Opened.Add(path);
            return null;
        }
        public void OpenTerminal(string directory) { }
        public void OpenWith(string path, LaunchOption option) => Opened.Add(path);
        public IReadOnlyList<LaunchOption> GetOpenWithOptions(string path) => [];
    }

    /// <summary>A bin pane with a launcher wired in, so "nothing was opened" is
    /// something this fake could have contradicted.</summary>
    private (PaneViewModel Pane, RecordingLauncher Launcher) InTheBinWithALauncher()
    {
        var launcher = new RecordingLauncher();
        var pane = Own(new PaneViewModel(new InertFileSystem(), new RecordingOperations(), launcher)
        {
            CurrentPath = VirtualPaths.Trash,
        });

        return (pane, launcher);
    }

    private static FileEntry Row(string name, bool directory = false)
        => new(name, Path.Combine(Path.GetTempPath(), name), 0, DateTimeOffset.Now,
               directory ? EntryFlags.Directory : EntryFlags.None);

    private (PaneViewModel Pane, RecordingOperations Ops) InTheBin()
    {
        var ops = new RecordingOperations();
        var pane = Own(new PaneViewModel(new InertFileSystem(), ops)
        {
            CurrentPath = VirtualPaths.Trash,
        });

        // The row's path is where the file USED to live — the detail that makes
        // this dangerous. A file of that name may well exist there again.
        pane.SelectedEntry = new FileEntry(
            "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
            0, DateTimeOffset.Now, EntryFlags.None);

        return (pane, ops);
    }

    /// <summary>
    /// **A bin row could be picked up and dragged, and the drag did nothing.**
    /// The payload is the path the item used to occupy, which is gone, so every
    /// target read an empty set: the cursor showed a drag, the button came up,
    /// and there was no effect and no message. A gesture that completes and
    /// achieves nothing cannot be told apart from a broken application.
    /// </summary>
    [AvaloniaFact]
    public void Rows_in_the_bin_cannot_be_dragged_out()
    {
        var (pane, _) = InTheBin();

        Assert.False(pane.CanDragOut());
        Assert.Contains("Restore", pane.Status);
    }

    /// <summary>Not a blanket refusal: an ordinary folder drags, and says
    /// nothing while doing it.</summary>
    [AvaloniaFact]
    public void Rows_anywhere_else_still_drag()
    {
        var pane = Own(new PaneViewModel(new InertFileSystem(), new RecordingOperations())
        {
            CurrentPath = Path.GetTempPath(),
        });

        var before = pane.Status;

        Assert.True(pane.CanDragOut());
        Assert.Equal(before, pane.Status);
    }

    /// <summary>
    /// **Opening a bin row opened the wrong file, or none at all.** The row
    /// carries the path the item USED to occupy. Trash notes.txt, write a new
    /// notes.txt, then press Enter on the bin row: the file that opens is the
    /// NEW one, with nothing to say so. The same shape as the delete that took
    /// the wrong file, and it reached the launcher rather than the file system,
    /// so none of the guards there could catch it.
    /// </summary>
    [AvaloniaFact]
    public async Task Enter_on_a_bin_row_opens_nothing()
    {
        var (pane, _) = InTheBin();

        pane.Status = "";

        await pane.OpenSelectedAsync();

        // The refusal is the observable part. This pane has no launcher, so
        // "nothing was opened" would hold with or without the guard and would
        // prove nothing; the status line only appears when the guard fires.
        Assert.Contains(Vaktari.Core.Naming.TheBin, pane.Status);
    }

    /// <summary>
    /// **The guard was on OpenSelectedAsync, and the pointer never goes
    /// through it.** MainWindow's TryOpen — the single place the tap and the
    /// double-tap both end up — calls OpenAsync on the pane directly, so a
    /// double-click on a bin row handed the launcher the path the item USED to
    /// occupy. Whatever had since been written there opened, and nothing said
    /// anything had happened out of the ordinary.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_bin_row_directly_reaches_no_launcher()
    {
        var (pane, launcher) = InTheBinWithALauncher();

        await pane.OpenAsync(Row("notes.txt"));

        Assert.Empty(launcher.Opened);
        Assert.Contains(Vaktari.Core.Naming.TheBin, pane.Status);
    }

    /// <summary>
    /// The worse half, and the reason the refusal sits ABOVE the directory
    /// branch: a binned FOLDER never reaches the launcher at all, it navigates
    /// the pane to the path the folder used to occupy. Arriving somewhere that
    /// looks plausible is harder to notice than a file opening.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_binned_folder_navigates_nowhere()
    {
        var (pane, _) = InTheBinWithALauncher();

        await pane.OpenAsync(Row("old-folder", directory: true));

        Assert.Equal(VirtualPaths.Trash, pane.CurrentPath);
        Assert.False(pane.CanGoBack);
    }

    /// <summary>
    /// The control. A refusal that fired everywhere would pass both tests above
    /// while breaking opening altogether, and this fake records rather than
    /// throws precisely so that it can say the launcher was reached.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_a_row_anywhere_else_still_reaches_the_launcher()
    {
        var launcher = new RecordingLauncher();
        var pane = Own(new PaneViewModel(new InertFileSystem(), new RecordingOperations(), launcher)
        {
            CurrentPath = Path.GetTempPath(),
        });

        await pane.OpenAsync(Row("notes.txt"));

        Assert.Equal([Path.Combine(Path.GetTempPath(), "notes.txt")], launcher.Opened);
    }

    /// <summary>
    /// The guard in OpenAsync does not reach this. "Open with" is offered in
    /// the bin — its visibility reads HasOpenWithOptions, which is filled for
    /// any file selection and never asks about the listing — and it calls the
    /// launcher itself. Picking an application for a binned row opened whatever
    /// now occupies the path the item used to have.
    /// </summary>
    [AvaloniaFact]
    public void Open_with_on_a_bin_row_reaches_no_launcher()
    {
        var (pane, launcher) = InTheBinWithALauncher();
        pane.SelectedEntry = Row("notes.txt");

        pane.OpenWithApp(new LaunchOption("Paint", "paint", null));

        Assert.Empty(launcher.Opened);
        Assert.Contains(Vaktari.Core.Naming.TheBin, pane.Status);
    }

    /// <summary>
    /// The pointer's half of the fault, pinned where it actually lives: the
    /// guard only covers the double-click because TryOpen goes through the
    /// pane's OpenAsync. A TryOpen rewritten to reach the launcher or the
    /// navigation itself would be outside every view-model guard again, and no
    /// view-model test could see it.
    /// </summary>
    [AvaloniaFact]
    public void The_pointer_route_opens_through_the_guarded_method()
    {
        var body = RepoSource.Body(RepoSource.Ui("MainWindow.axaml.cs"),
                                   "private void TryOpen(FileEntry entry)");

        Assert.Contains("OpenAsync(entry)", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// The bin refusal outranks the count refusal, which is why the check is
    /// still at the top of OpenSelectedAsync as well as inside OpenAsync.
    /// Selecting more than the open limit of bin rows and pressing Enter would
    /// otherwise be told to select fewer — an answer to a question nobody
    /// asked, about an action that was never going to happen.
    /// </summary>
    [AvaloniaFact]
    public async Task A_large_bin_selection_is_told_it_is_in_the_bin_not_to_select_fewer()
    {
        var (pane, launcher) = InTheBinWithALauncher();

        for (var i = 0; i <= PaneViewModel.OpenLimit; i++)
            pane.SelectedEntries.Add(Row($"file{i}.txt"));

        await pane.OpenSelectedAsync();

        Assert.Empty(launcher.Opened);
        Assert.Contains(Vaktari.Core.Naming.TheBin, pane.Status);
    }

    /// <summary>And the menu row goes with it, or the entry is drawn, enabled,
    /// and refuses when pressed.</summary>
    [AvaloniaFact]
    public void The_open_row_is_not_offered_in_the_bin()
    {
        var (pane, _) = InTheBin();

        Assert.True(pane.HasSelection);
        Assert.False(pane.CanActOnSelection);

        // And the row actually reads that, rather than HasSelection, which is
        // true in the bin and was what it used to bind.
        var markup = RepoSource.Ui("MainWindow.axaml");

        var at = markup.IndexOf("Command=\"{Binding ActiveTab.OpenSelectedCommand}\"",
                                StringComparison.Ordinal);

        Assert.True(at > 0, "the Open row is not written the way this test looks for it");

        Assert.Contains("CanActOnSelection", markup[at..markup.IndexOf("/>", at, StringComparison.Ordinal)]);
    }

    [AvaloniaFact]
    public void Shift_delete_on_a_bin_row_destroys_nothing()
    {
        var (pane, ops) = InTheBin();

        pane.DeleteSelected();

        Assert.Empty(ops.Calls);
    }

    [AvaloniaFact]
    public void Delete_on_a_bin_row_bins_nothing_a_second_time()
    {
        var (pane, ops) = InTheBin();

        pane.TrashSelected();

        Assert.Empty(ops.Calls);
    }

    /// <summary>
    /// Renaming a bin row would rename whatever now holds the original path —
    /// the same hazard as the delete, and just as invisible, since the row goes
    /// on showing the old name either way.
    /// </summary>
    [AvaloniaFact]
    public void Rename_is_not_offered_on_a_bin_row()
    {
        var (pane, _) = InTheBin();
        var asked = false;
        pane.RenameRequested += (_, _) => asked = true;

        pane.BeginRename();

        Assert.False(asked);
    }

    [AvaloniaFact]
    public void Pasting_into_the_bin_does_not_create_a_folder_called_vaktari_trash()
    {
        var (pane, ops) = InTheBin();

        pane.PasteInto([Path.Combine(Path.GetTempPath(), "something.txt")], move: true);

        Assert.Empty(ops.Calls);
    }

    /// <summary>
    /// The refusal is said out loud. A command that silently does nothing is
    /// indistinguishable from one that is broken, and the reason this reads as
    /// a suggestion is that Restore and Empty are the actions that do work here.
    /// </summary>
    /// <summary>
    /// **Duplicate was the one write in the file with no guard**, missed when
    /// the menu consolidation moved it. Its destination is CurrentPath, which
    /// in these listings is the literal string "vaktari:trash" — so on Linux
    /// duplicating from Recent created a folder of that name in the working
    /// directory and copied into it, the exact failure the paste guard exists
    /// to stop.
    /// </summary>
    [AvaloniaFact]
    public void Duplicating_a_binned_row_copies_nothing()
    {
        var (pane, ops) = InTheBin();

        pane.DuplicateSelected();

        Assert.Empty(ops.Calls);
    }

    [AvaloniaFact]
    public void Duplicating_from_recent_copies_nothing()
    {
        var ops = new RecordingOperations();
        var pane = Own(new PaneViewModel(new InertFileSystem(), ops)
        {
            CurrentPath = VirtualPaths.Files,
            SelectedEntry = new FileEntry(
                "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
                0, DateTimeOffset.Now, EntryFlags.None),
        });

        pane.DuplicateSelected();

        Assert.Empty(ops.Calls);
    }

    [AvaloniaFact]
    public void The_refusal_names_what_to_do_instead()
    {
        var (pane, _) = InTheBin();

        pane.DeleteSelected();

        Assert.Contains("Restore", pane.Status);
    }

    /// <summary>
    /// **The other half of the guard, and the half that keeps it honest.** A
    /// test that only asserts "nothing happened" passes just as well when the
    /// command is broken outright, so the same call in a real folder must still
    /// reach the file system — the fake throws to prove it got there.
    /// </summary>
    [AvaloniaFact]
    public void In_a_real_folder_the_same_commands_still_act()
    {
        var ops = new RecordingOperations();
        var pane = Own(new PaneViewModel(new InertFileSystem(), ops)
        {
            CurrentPath = Path.GetTempPath(),
            SelectedEntry = new FileEntry(
                "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
                0, DateTimeOffset.Now, EntryFlags.None),
        });

        Assert.Throws<InvalidOperationException>(() => pane.DeleteSelected());
        Assert.Throws<InvalidOperationException>(() => pane.TrashSelected());

        Assert.Equal(2, ops.Calls.Count);
    }

    /// <summary>
    /// Recent listings share the hazard — their rows carry real paths, and
    /// their CurrentPath is another <c>vaktari:</c> string that is not a folder.
    /// Deleting from Recent is legitimate, though: the file is really there.
    /// Only the DESTINATION is refused.
    /// </summary>
    [AvaloniaFact]
    public void Recent_refuses_the_destination_but_not_the_selection()
    {
        var ops = new RecordingOperations();
        var pane = Own(new PaneViewModel(new InertFileSystem(), ops)
        {
            CurrentPath = VirtualPaths.Files,
            SelectedEntry = new FileEntry(
                "notes.txt", Path.Combine(Path.GetTempPath(), "notes.txt"),
                0, DateTimeOffset.Now, EntryFlags.None),
        });

        pane.PasteInto([Path.Combine(Path.GetTempPath(), "other.txt")], move: true);
        Assert.Empty(ops.Calls);

        Assert.Throws<InvalidOperationException>(() => pane.TrashSelected());
    }

    /// <summary>
    /// Dropping onto a folder ROW while a virtual listing is showing is a real
    /// destination and must keep working — which is why that guard reads its
    /// argument rather than CurrentPath. Guarding the wrong one here would have
    /// broken Copy-to and folder drops from Recent while looking correct.
    /// </summary>
    [AvaloniaFact]
    public void A_real_folder_row_inside_a_virtual_listing_is_still_a_destination()
    {
        var ops = new RecordingOperations();
        var pane = Own(new PaneViewModel(new InertFileSystem(), ops)
        {
            CurrentPath = VirtualPaths.Files,
        });

        Assert.Throws<InvalidOperationException>(() => pane.PasteIntoFolder(
            Path.GetTempPath(),
            [Path.Combine(Path.GetTempPath(), "other.txt")],
            move: true));
    }
}
