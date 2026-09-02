using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Taking back a copy, and taking back a new folder.
///
/// **Neither could be undone.** Pasting into the wrong folder is one of the
/// easiest mistakes a file manager lets you make, and Ctrl+Z did nothing at
/// all: the engine deliberately refused, on the grounds that undoing a copy
/// means removing files and an undo that deletes is not a safe default.
///
/// That reasoning was right about the danger and wrong about the conclusion —
/// the files stay where they should not be and the person has to find and
/// remove them by hand. The bin settles it. What an undo takes away is sitting
/// in the bin, recoverable, exactly like anything else deleted from a listing,
/// which is also how Explorer undoes a copy.
///
/// New folder, new file and new-from-template had a different reason for the
/// same symptom: they write straight to the filesystem, so the undo history
/// never heard about them at all.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UndoCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-undocopy-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly List<IReadOnlyList<string>> _trashed = [];

    public UndoCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// The bin is faked, so the test says what the undo ASKED FOR rather than
    /// putting the user's real Recycle Bin to work — which a test has no
    /// business doing.
    /// </summary>
    private WindowsFileOperations Ops()
    {
        return new WindowsFileOperations
        {
            RecycleOverride = paths =>
            {
                _trashed.Add(paths.ToList());

                foreach (var path in paths)
                {
                    if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                    else if (File.Exists(path)) File.Delete(path);
                }

                return new RecycleResult(0, false);
            },
        };
    }

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

    [Fact]
    public async Task Undoing_a_copy_sends_what_arrived_to_the_bin()
    {
        var source = Dir("source");
        var note = File_(@"source\notes.txt");
        var wrong = Dir("wrong-folder");

        var ops = Ops();

        await ops.Copy([note], wrong, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        var landed = Path.Combine(wrong, "notes.txt");
        Assert.True(File.Exists(landed), "the copy never happened");
        Assert.True(ops.CanUndo, "a copy is supposed to be undoable now");

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(File.Exists(landed), "the copy was left where it should not be");
        Assert.True(File.Exists(note), "THE ORIGINAL WAS REMOVED");

        // Into the bin, not destroyed.
        Assert.Contains(_trashed, batch => batch.Contains(landed));
    }

    /// <summary>
    /// A copy that landed on top of something is not this operation's to
    /// remove — the undo takes back what it created, and nothing else.
    /// </summary>
    [Fact]
    public async Task Undoing_a_copy_does_not_touch_what_it_never_made()
    {
        var note = File_(@"source\notes.txt", "mine");
        var wrong = Dir("wrong-folder");

        var ops = Ops();

        await ops.Copy([note], wrong, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        // Something else appears beside it afterwards.
        var bystander = File_(@"wrong-folder\unrelated.txt", "theirs");

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(File.Exists(bystander), "the undo removed a file it never copied");
    }

    [Fact]
    public async Task Undoing_a_new_folder_sends_it_to_the_bin()
    {
        var ops = Ops();
        var made = Path.Combine(_root, "New folder");

        Directory.CreateDirectory(made);
        ops.RecordCreation(made);

        Assert.True(ops.CanUndo);

        await ops.UndoAsync(CancellationToken.None);

        Assert.False(Directory.Exists(made), "the new folder stayed");
        Assert.Contains(_trashed, batch => batch.Contains(made));
    }

    /// <summary>
    /// And undoing something already gone is quiet rather than a failure: the
    /// user may well have deleted it themselves in between.
    /// </summary>
    [Fact]
    public async Task Undoing_something_that_has_already_gone_is_quiet()
    {
        var ops = Ops();
        var made = Path.Combine(_root, "New folder");

        Directory.CreateDirectory(made);
        ops.RecordCreation(made);

        Directory.Delete(made);

        await ops.UndoAsync(CancellationToken.None);

        Assert.Empty(_trashed);
    }

    /// <summary>A move still undoes as a move — putting the files back where
    /// they came from, not into the bin.</summary>
    [Fact]
    public async Task Undoing_a_move_still_puts_the_files_back()
    {
        var note = File_(@"source\notes.txt");
        var elsewhere = Dir("elsewhere");

        var ops = Ops();

        await ops.Move([note], elsewhere, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        Assert.False(File.Exists(note));

        await ops.UndoAsync(CancellationToken.None);

        Assert.True(File.Exists(note), "the move was not put back");
        Assert.Empty(_trashed);
    }
}
