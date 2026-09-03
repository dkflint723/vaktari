using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Settings;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// What the next undo is called.
///
/// **Ctrl+Z said nothing about what it would take back**, and there was no Undo
/// row in any menu to say it either — so after a copy, a rename and a delete in
/// quick succession the only way to find out which one came back was to press
/// it and look. Both references name the act in the menu row.
///
/// One test per kind, because each kind builds its own name and a name that is
/// silently empty is exactly as unhelpful as no name at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoIsNamedTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-undoname-" + Guid.NewGuid().ToString("N")[..8]);

    public UndoIsNamedTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception) { /* a temp dir is not worth failing over */ }

        GC.SuppressFinalize(this);
    }

    /// <summary>The bin is faked, so nothing here goes near the real one.</summary>
    private static WindowsFileOperations Ops()
        => new()
        {
            RecycleOverride = paths =>
            {
                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }

                return new RecycleResult(0, false);
            },
        };

    private string Dir(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File_(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Nothing done, nothing to name.</summary>
    [WindowsFact]
    public void A_fresh_history_names_nothing()
    {
        var ops = Ops();

        Assert.Null(ops.UndoDescription);
        Assert.Null(ops.RedoDescription);
    }

    /// <summary>
    /// The finding's own case: a paste into the wrong folder. One file is named,
    /// which is what a person wants when there is one thing.
    /// </summary>
    [WindowsFact]
    public async Task A_copy_of_one_file_is_named_after_the_file()
    {
        var note = File_(@"source\notes.txt");
        var wrong = Dir("wrong-folder");

        var ops = Ops();

        await ops.Copy([note], wrong, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        Assert.Equal("copy of notes.txt", ops.UndoDescription);
    }

    /// <summary>And several are counted, because a list is unreadable in a
    /// menu row.</summary>
    [WindowsFact]
    public async Task A_copy_of_several_is_counted()
    {
        var one = File_(@"source\one.txt");
        var two = File_(@"source\two.txt");
        var three = File_(@"source\three.txt");
        var wrong = Dir("wrong-folder");

        var ops = Ops();

        await ops.Copy([one, two, three], wrong,
                       _ => ValueTask.FromResult(ConflictResolution.KeepBoth)).Completion;

        Assert.Equal("copy of 3 items", ops.UndoDescription);
    }

    [WindowsFact]
    public async Task A_move_is_named_as_a_move()
    {
        var one = File_(@"source\one.txt");
        var two = File_(@"source\two.txt");
        var elsewhere = Dir("elsewhere");

        var ops = Ops();

        await ops.Move([one, two], elsewhere,
                       _ => ValueTask.FromResult(ConflictResolution.KeepBoth)).Completion;

        Assert.Equal("move of 2 items", ops.UndoDescription);
    }

    /// <summary>
    /// A new folder reads as the act rather than as a batch, because it always
    /// is exactly one.
    /// </summary>
    [WindowsFact]
    public void A_new_folder_is_named_after_itself()
    {
        var made = Dir("Reports");

        var ops = Ops();

        ops.RecordCreation(made);

        Assert.Equal("creating Reports", ops.UndoDescription);
    }

    /// <summary>
    /// A rename is named by where the file is NOW — the name on screen, which
    /// is the one the person is looking at when they wonder what Ctrl+Z does.
    /// </summary>
    [WindowsFact]
    public async Task A_rename_is_named_by_the_name_on_screen()
    {
        var note = File_("readme.txt");

        var ops = Ops();

        await ops.RenameAsync(note, "notes.txt", CancellationToken.None);

        Assert.Equal("rename of notes.txt", ops.UndoDescription);
    }

    /// <summary>
    /// **A delete is named by the file, not by its key in the bin.** The undo
    /// holds $I metadata paths, which are what a restore needs and are not
    /// something to put in a menu row, so the names the person used are carried
    /// alongside them.
    /// </summary>
    [WindowsFact]
    public async Task A_delete_is_named_by_the_file_and_not_by_its_bin_key()
    {
        var note = File_("notes.txt");

        var bin = new RememberingBin(note);

        var ops = new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                foreach (var path in paths) File.Delete(path);

                bin.Recycled();

                return new RecycleResult(0, false);
            },
            Bin = bin,
        };

        await ops.Trash([note]).Completion;

        Assert.Equal("delete of notes.txt", ops.UndoDescription);
    }

    /// <summary>
    /// **What a redo would put back has its own name.** Undoing a rename
    /// leaves a rename on the redo stack, and the two stacks name different
    /// things — an implementation that answered the undo stack for both would
    /// read correctly in every state except the one that matters.
    /// </summary>
    [WindowsFact]
    public async Task A_redo_is_named_after_what_it_would_put_back()
    {
        var note = File_("readme.txt");

        var ops = Ops();

        await ops.RenameAsync(note, "notes.txt", CancellationToken.None);
        await ops.UndoAsync(CancellationToken.None);

        Assert.True(ops.CanRedo, "the rename should be redoable");

        // The undo stack is empty now, so an implementation that answered it
        // for both would say null here.
        Assert.Equal("rename of readme.txt", ops.RedoDescription);
        Assert.Null(ops.UndoDescription);
    }

    /// <summary>
    /// A bin that reports one arrival, so the trash undo has something to
    /// record without the user's own Recycle Bin taking part.
    /// </summary>
    private sealed class RememberingBin(string original) : ITrashMaintenance
    {
        private readonly List<TrashedItem> _items = [];
        private bool _deleted;

        public IReadOnlyList<TrashedItem> List()
        {
            // The engine compares the bin before and after, so the item has to
            // appear only once the recycle has happened.
            if (_deleted && _items.Count == 0)
                _items.Add(new TrashedItem(
                    @"C:\$Recycle.Bin\$Iabc.txt", original, @"C:\$Recycle.Bin\$Rabc.txt",
                    DateTimeOffset.UnixEpoch, 1, false));

            return _items.ToList();
        }

        public bool HasAny() => List().Count > 0;

        public string Restore(string trashName) => original;

        public void Delete(string trashName) { }

        public ValueTask<TrashSweepResult> SweepAsync(TrashSettings policy, CancellationToken ct)
        {
            _deleted = true;
            return ValueTask.FromResult(TrashSweepResult.Nothing);
        }

        public ValueTask<TrashSweepResult> EmptyAsync(CancellationToken ct)
            => ValueTask.FromResult(TrashSweepResult.Nothing);

        /// <summary>Called by the test's recycle override, which is the only
        /// thing that knows the file really went.</summary>
        public void Recycled() => _deleted = true;
    }
}
