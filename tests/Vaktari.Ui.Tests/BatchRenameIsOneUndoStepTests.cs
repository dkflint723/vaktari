using Avalonia.Headless.XUnit;
using Vaktari.Core.FileSystem;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The batch rename dialog is one press of Ctrl+Z.
///
/// **It was one press per file.** Apply looped over the plan calling the same
/// rename the inline F2 rename calls, and each of those pushed its own undo
/// entry — so taking back a renumbered folder of forty photographs meant forty
/// presses, each naming a single file. A swap was worse than that: breaking the
/// cycle costs a staging rename, and that landed on the stack as well, so the
/// history held more steps than there were files.
///
/// The dialog holds the engine's group open for the length of the apply, and
/// names it for what it has actually renamed.
/// </summary>
public sealed class BatchRenameIsOneUndoStepTests : OwnedViewModels
{
    /// <summary>
    /// A folder of names that refuses a collision, so an order that could not
    /// really be performed cannot pass — and optionally refuses one particular
    /// new name, which is how a batch is made to stop halfway.
    /// </summary>
    private sealed class Folder(params string[] names)
    {
        private readonly HashSet<string> _live = new(names, StringComparer.Ordinal);

        /// <summary>A name the "file system" will not accept.</summary>
        public string? Refuses { get; init; }

        public IReadOnlyList<FileEntry> Entries =>
            [.. names.Select(n => new FileEntry(
                n, Path.Combine(Path.GetTempPath(), n), 1,
                DateTimeOffset.UnixEpoch, EntryFlags.None))];

        public Task Rename(FileEntry entry, string newName)
        {
            if (newName == Refuses) throw new IOException($"'{newName}' is refused.");

            if (!_live.Contains(entry.Name))
                throw new FileNotFoundException($"'{entry.Name}' is not there.");

            if (_live.Contains(newName) && newName != entry.Name)
                throw new IOException($"'{newName}' already exists here.");

            _live.Remove(entry.Name);
            _live.Add(newName);

            return Task.CompletedTask;
        }
    }

    /// <summary>One group, remembering how it was named and when it closed.</summary>
    private sealed class Group : IUndoGroup
    {
        public string Description { get; set; } = "";

        public int Closed { get; private set; }

        public void Dispose() => Closed++;
    }

    /// <summary>Stands in for the engine's history.</summary>
    private sealed class Groups
    {
        public List<Group> Opened { get; } = [];

        public IUndoGroup? Open()
        {
            var group = new Group();
            Opened.Add(group);
            return group;
        }

        public Group Only => Assert.Single(Opened);
    }

    private static BatchRenameViewModel Renaming(
        Folder folder, Groups groups, string pattern, int startAt = 1)
        => new(folder.Entries, folder.Rename, folder.Entries, groups.Open)
        {
            Pattern = pattern,
            StartAt = startAt,
        };

    /// <summary>The finding itself, from the dialog's side.</summary>
    [AvaloniaFact]
    public async Task Applying_opens_one_group_and_closes_it()
    {
        var groups = new Groups();

        await Renaming(new Folder("img001.jpg", "img002.jpg", "img003.jpg"),
                       groups, "img###", startAt: 2)
              .ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, groups.Only.Closed);
    }

    /// <summary>And the menu row is named for the whole batch.</summary>
    [AvaloniaFact]
    public async Task The_group_is_named_for_every_file_it_renamed()
    {
        var groups = new Groups();

        await Renaming(new Folder("img001.jpg", "img002.jpg", "img003.jpg"),
                       groups, "img###", startAt: 2)
              .ApplyCommand.ExecuteAsync(null);

        Assert.Equal("rename of 3 items", groups.Only.Description);
    }

    /// <summary>
    /// **A swap costs three renames and renames two files.** The middle one
    /// parks a file under a staging name nobody asked for, and counting it
    /// would offer "rename of 3 items" for two files.
    ///
    /// The entries are in the order that makes the numbering a swap: img002
    /// takes the first number and img001 takes the second.
    /// </summary>
    [AvaloniaFact]
    public async Task The_staging_move_of_a_swap_is_not_counted()
    {
        var groups = new Groups();

        await Renaming(new Folder("img002.jpg", "img001.jpg"), groups, "img###")
              .ApplyCommand.ExecuteAsync(null);

        Assert.Equal("rename of 2 items", groups.Only.Description);
    }

    /// <summary>
    /// **Named for what it did, not for what it set out to do.** A batch that
    /// stops halfway leaves only the renames that went through in the group,
    /// and a name settled before the work would offer three files back when one
    /// of them moved.
    /// </summary>
    [AvaloniaFact]
    public async Task A_batch_that_stops_halfway_is_named_for_what_it_did()
    {
        var groups = new Groups();

        var model = Renaming(
            new Folder("a.txt", "b.txt", "c.txt") { Refuses = "n002.txt" },
            groups, "n###");

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("stopped after 1", model.Summary);
        Assert.Equal("rename of n001.txt", groups.Only.Description);
    }

    /// <summary>
    /// **A batch that stops on the staging move is named for the file that
    /// comes back.** The swap parks img002 under a name nobody asked for, the
    /// very next rename refuses, and the group is then holding that one move
    /// and nothing else — so the Undo row was the parked name itself,
    /// "rename of .vaktari-rename-0123456789abcdef", measured on the real
    /// engine. The file is still sitting under it, so the step has to stay;
    /// what has to change is what the row says it will bring back.
    /// </summary>
    [AvaloniaFact]
    public async Task A_batch_that_stops_on_its_staging_move_names_the_parked_file()
    {
        var groups = new Groups();

        // The order that makes the numbering a swap, refusing the rename that
        // follows the staging move.
        var model = Renaming(
            new Folder("img002.jpg", "img001.jpg") { Refuses = "img002.jpg" },
            groups, "img###");

        await model.ApplyCommand.ExecuteAsync(null);

        Assert.Contains("stopped after 0", model.Summary);
        Assert.Equal("rename of img002.jpg", groups.Only.Description);
        Assert.DoesNotContain(".vaktari-rename", groups.Only.Description,
                              StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is closed on the way out, or the engine would go on folding every
    /// later rename into a batch that ended long ago.
    /// </summary>
    [AvaloniaFact]
    public async Task The_group_is_closed_when_a_rename_fails()
    {
        var groups = new Groups();

        await Renaming(new Folder("a.txt", "b.txt", "c.txt") { Refuses = "n002.txt" },
                       groups, "n###")
              .ApplyCommand.ExecuteAsync(null);

        Assert.Equal(1, groups.Only.Closed);
    }

    /// <summary>
    /// The group comes from the engine, because the history is the engine's —
    /// one history shared by every pane and every tab.
    /// </summary>
    [AvaloniaFact]
    public void The_pane_hands_back_the_engines_group()
    {
        var ops = new Grouping();

        var pane = Own(new PaneViewModel(new Nothing(), ops));

        Assert.Same(ops.Handed, pane.BeginRenameGroup());
    }

    /// <summary>
    /// The shell is what puts the two together, and it is a plain argument at
    /// one call site — an optional one, so leaving it off still builds.
    /// </summary>
    [AvaloniaFact]
    public void The_dialog_is_given_the_panes_group()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"), "private void ShowBatchRename(");

        Assert.Contains("pane.BeginRenameGroup", body, StringComparison.Ordinal);
    }

    /// <summary>An engine that does nothing but hand out one group.</summary>
    private sealed class Grouping : IFileOperations
    {
        public IUndoGroup Handed { get; } = new Group();

        public IUndoGroup? BeginRenameGroup() => Handed;

        public IOperationHandle Copy(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Move(
            IReadOnlyList<string> sources, string destination,
            Func<FileConflict, ValueTask<ConflictResolution>> onConflict)
            => throw new NotSupportedException();

        public IOperationHandle Trash(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public IOperationHandle Delete(IReadOnlyList<string> paths)
            => throw new NotSupportedException();

        public ValueTask RenameAsync(string path, string newName, CancellationToken ct)
            => ValueTask.CompletedTask;

        public void RecordCreation(string path) { }

        public bool CanUndo => false;
        public bool CanRedo => false;
        public ValueTask UndoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RedoAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public string? UndoDescription => null;
        public string? RedoDescription => null;
    }

    /// <summary>An empty folder, so the pane can be built without listing
    /// anything.</summary>
    private sealed class Nothing : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
