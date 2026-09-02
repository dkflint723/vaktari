using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Windows;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Names Windows cannot be asked about, and the file that used to be destroyed
/// instead.
///
/// **This is a wrong-file deletion, not an inconvenience.** A name ending in a
/// space or a dot is legal on NTFS and arrives routinely from WSL, from a Linux
/// SMB client and from git. The .NET path layer strips the trailing character
/// before the call, so every question about "report " is answered about
/// "report" — it exists, it opens, it reads, and it is a different file.
/// Measured here, and the reason this file exists:
///
///     File.Delete(@"…\report ")  ->  "report" is gone, "report " still there.
///
/// The listing shows the true name, so the row a person clicks and the file the
/// operation destroys were two different files, with nothing on screen to say
/// so. Explorer refuses these outright and that is what Vaktari does now.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrailingSpaceNameTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vaktari-trailing-" + Guid.NewGuid().ToString("N")[..8]);

    // The extended prefix is the only way to CREATE the trap, which is itself
    // the proof that the ordinary path cannot address it.
    private static string Extended(string path) => @"\\?\" + path;

    public TrailingSpaceNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;

        foreach (var file in Directory.GetFiles(Extended(_root)))
            File.Delete(Extended(file[4..]));

        Directory.Delete(Extended(_root), recursive: true);
    }

    private (string Plain, string Trailing) Trap()
    {
        var plain = Path.Combine(_root, "report");
        var trailing = plain + " ";

        File.WriteAllText(plain, "the innocent one");
        File.WriteAllText(Extended(trailing), "the one that was asked for");

        Assert.Equal(2, Directory.GetFiles(Extended(_root)).Length);

        return (plain, trailing);
    }

    /// <summary>
    /// The measurement the guard exists for. If this ever fails, .NET has
    /// started honouring these names and the refusal can become a reach.
    /// </summary>
    [Fact]
    public void The_path_layer_really_does_answer_for_the_wrong_file()
    {
        var (plain, trailing) = Trap();

        Assert.True(File.Exists(trailing), "the BCL says it exists");
        Assert.Equal("the innocent one", File.ReadAllText(trailing));
        Assert.Equal(plain, Path.GetFullPath(trailing));
    }

    [Fact]
    public async Task Deleting_such_a_name_is_refused_rather_than_hitting_its_neighbour()
    {
        var (plain, trailing) = Trap();

        var ops = new WindowsFileOperations();
        var handle = ops.Delete([trailing]);

        await handle.Completion;

        Assert.True(File.Exists(Extended(trailing)), "the file the user named was deleted");
        Assert.True(File.Exists(plain), "THE WRONG FILE WAS DELETED");

        var problem = Assert.Single(handle.Problems);
        Assert.Contains("space", problem.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// One bad name does not cancel the rest of the selection. Somebody who
    /// selected twenty files and one of them came off a Samba share still
    /// wanted the other nineteen gone.
    /// </summary>
    [Fact]
    public async Task The_rest_of_the_selection_still_goes()
    {
        var (_, trailing) = Trap();

        var ordinary = Path.Combine(_root, "ordinary.txt");
        File.WriteAllText(ordinary, "x");

        var ops = new WindowsFileOperations();
        var handle = ops.Delete([trailing, ordinary]);

        await handle.Completion;

        Assert.False(File.Exists(ordinary), "the good file was not deleted");
        Assert.Single(handle.Problems);
    }

    [Fact]
    public async Task Renaming_such_a_name_is_refused()
    {
        var (plain, trailing) = Trap();

        var ops = new WindowsFileOperations();

        var thrown = await Assert.ThrowsAsync<IOException>(
            async () => await ops.RenameAsync(trailing, "renamed.txt", CancellationToken.None));

        Assert.Contains("space", thrown.Message, StringComparison.OrdinalIgnoreCase);

        Assert.True(File.Exists(plain), "THE WRONG FILE WAS RENAMED");
        Assert.False(File.Exists(Path.Combine(_root, "renamed.txt")));
    }

    [Fact]
    public async Task Copying_such_a_name_is_refused()
    {
        var (_, trailing) = Trap();

        var into = Path.Combine(_root, "into");
        Directory.CreateDirectory(into);

        var ops = new WindowsFileOperations();
        var handle = ops.Copy([trailing], into, _ => ValueTask.FromResult(ConflictResolution.Skip));

        await handle.Completion;

        // Nothing was copied, and in particular the neighbouring file was not
        // copied under the name of the one that was asked for.
        Assert.Empty(Directory.GetFiles(into));
    }

    /// <summary>A name ending in a dot is the same fault; Windows strips those
    /// too, and a file called "notes." is not a file called "notes".</summary>
    [Fact]
    public async Task A_trailing_dot_is_refused_the_same_way()
    {
        var plain = Path.Combine(_root, "notes");
        var dotted = plain + ".";

        File.WriteAllText(plain, "the innocent one");
        File.WriteAllText(Extended(dotted), "the one that was asked for");

        var ops = new WindowsFileOperations();
        var handle = ops.Delete([dotted]);

        await handle.Completion;

        Assert.True(File.Exists(plain), "THE WRONG FILE WAS DELETED");
        Assert.Contains("dot", Assert.Single(handle.Problems).Error.Message,
                        StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// And ordinary names are untouched by all of this — a guard that refused
    /// anything else would be worse than the bug.
    /// </summary>
    [Fact]
    public void An_ordinary_name_is_not_refused()
    {
        Assert.Null(ReachablePath.Refuse(Path.Combine(_root, "perfectly.normal.txt")));
        Assert.Null(ReachablePath.Refuse(@"C:\Users\someone\Documents"));
        Assert.Null(ReachablePath.Refuse(@"C:\"));
        Assert.Null(ReachablePath.Refuse(@"\\server\share\file.txt"));

        // "." and ".." end in a dot and are path syntax, not names.
        Assert.Null(ReachablePath.Refuse(@"C:\a\.\b"));
        Assert.Null(ReachablePath.Refuse(@"C:\a\..\b"));
    }
}
