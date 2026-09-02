using Vaktari.Core.FileSystem;
using Vaktari.Linux;
using Xunit;

namespace Vaktari.Linux.Tests;

/// <summary>
/// The Linux twin of the Windows folder-copy tests, because the two engines
/// carried the identical pair of faults and were fixed together.
///
///  - Duplicating a folder deduplicated the ROOT without recording a redirect,
///    so every descendant was deduplicated where it stood: an empty "A - Copy"
///    beside an original full of " - Copy" twins.
///  - Skip on an existing folder skipped the folder entry only; its files still
///    merged into the existing folder, and on a move the emptied source was
///    then deleted.
///
/// Like <see cref="MoveConflictTests"/>, these run on any platform: the code
/// under test is path arithmetic and ordinary file I/O.
/// </summary>
public sealed class FolderCopyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-lin-foldercopy-" + Guid.NewGuid().ToString("N")[..8]);

    public FolderCopyTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private void File_(string relative, string content = "x")
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
    }

    private static string[] NamesIn(string path)
        => Directory.GetFileSystemEntries(path).Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Duplicating_a_folder_copies_the_tree_and_leaves_the_original_alone()
    {
        var alpha = Dir("Alpha");
        File_("Alpha/x.txt");
        File_("Alpha/sub/y.txt");

        var ops = new LinuxFileOperations();

        await ops.Copy([alpha], _root, _ => ValueTask.FromResult(ConflictResolution.KeepBoth))
                 .Completion;

        Assert.Equal(["sub", "x.txt"], NamesIn(alpha));
        Assert.Equal(["y.txt"], NamesIn(Path.Combine(alpha, "sub")));

        var copy = Directory.GetDirectories(_root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Single(n => n != "Alpha");

        Assert.Equal(["sub", "x.txt"], NamesIn(Path.Combine(_root, copy)));
        Assert.Equal(["y.txt"], NamesIn(Path.Combine(_root, copy, "sub")));
    }

    [Fact]
    public async Task Skipping_a_folder_leaves_the_existing_one_untouched()
    {
        var source = Dir("source");
        File_("source/photos/holiday.jpg");
        File_("source/photos/notes.txt");

        var destination = Dir("destination");
        File_("destination/photos/already-here.jpg");

        var ops = new LinuxFileOperations();

        await ops.Copy(
            [Path.Combine(source, "photos")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)).Completion;

        Assert.Equal(["already-here.jpg"], NamesIn(Path.Combine(destination, "photos")));
    }

    /// <summary>The destructive half: a skipped folder used to be consumed by
    /// the move it was supposed to sit out.</summary>
    [Fact]
    public async Task Skipping_a_folder_on_a_move_does_not_consume_the_source()
    {
        var source = Dir("source");
        File_("source/photos/holiday.jpg");
        File_("source/photos/notes.txt");

        var destination = Dir("destination");
        File_("destination/photos/already-here.jpg");

        var ops = new LinuxFileOperations();

        await ops.Move(
            [Path.Combine(source, "photos")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)).Completion;

        var kept = Path.Combine(source, "photos");

        Assert.True(Directory.Exists(kept), "THE SOURCE FOLDER WAS CONSUMED BY A SKIP");
        Assert.Equal(["holiday.jpg", "notes.txt"], NamesIn(kept));
        Assert.Equal(["already-here.jpg"], NamesIn(Path.Combine(destination, "photos")));
    }

    /// <summary>A folder whose name merely starts the same is not swept up with
    /// the skipped one.</summary>
    [Fact]
    public async Task A_folder_whose_name_merely_starts_the_same_is_not_skipped()
    {
        var source = Dir("source");
        File_("source/work/a.txt");
        File_("source/work 2/b.txt");

        var destination = Dir("destination");
        File_("destination/work/already-here.txt");

        var ops = new LinuxFileOperations();

        await ops.Copy(
            [Path.Combine(source, "work"), Path.Combine(source, "work 2")],
            destination,
            _ => ValueTask.FromResult(ConflictResolution.Skip)).Completion;

        Assert.Equal(["already-here.txt"], NamesIn(Path.Combine(destination, "work")));
        Assert.Equal(["b.txt"], NamesIn(Path.Combine(destination, "work 2")));
    }
}
