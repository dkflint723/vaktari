using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// Moving something into the folder it is already in, reached by another name.
///
/// **The file was deleted.** A mapped drive and its share, a subst drive, a
/// path with the \\?\ prefix: none is a link, so the engine's check for "this
/// is where it already is" — which resolves links along the path — saw two
/// different folders. The move took the copy route across what looked like two
/// volumes, landed the copy over the file (which was itself), and then deleted
/// the source, which was what had just landed. Nothing was left under either
/// name, in no bin, with nothing to undo.
///
/// The \\?\ spelling stands in for the others: it needs nothing installed or
/// mapped, and the text of it differs from the plain path exactly the way a
/// mapped drive's does from its share's.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SameFolderOtherNameTests
{
    private static Func<FileConflict, ValueTask<ConflictResolution>> Always(ConflictResolution r)
        => _ => ValueTask.FromResult(r);

    [WindowsFact]
    public async Task Moving_a_file_into_its_own_folder_by_another_name_keeps_it()
    {
        using var tree = new TempTree();

        var dir = tree.Dir("docs");
        var file = tree.Write("docs/a.txt", "the only copy");

        var handle = new WindowsFileOperations().Move([file], @"\\?\" + dir, Always(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.True(File.Exists(file), "the file is gone");
        Assert.Equal("the only copy", File.ReadAllText(file));
    }

    /// <summary>The same for a folder, whose files each took the same route.</summary>
    [WindowsFact]
    public async Task Moving_a_folder_into_its_own_parent_by_another_name_keeps_it()
    {
        using var tree = new TempTree();

        var parent = tree.Dir("docs");
        var inside = tree.Write("docs/project/notes.txt", "kept");

        var handle = new WindowsFileOperations().Move(
            [Path.Combine(parent, "project")], @"\\?\" + parent, Always(ConflictResolution.Overwrite));
        await handle.Completion;

        Assert.True(File.Exists(inside), "the folder's contents are gone");
        Assert.Equal("kept", File.ReadAllText(inside));
    }

    [WindowsFact]
    public void Two_spellings_of_one_folder_are_one_entry_and_two_folders_are_not()
    {
        using var tree = new TempTree();

        var a = tree.Dir("a");
        var b = tree.Dir("b");

        Assert.True(FileIdentity.Same(a, @"\\?\" + a));
        Assert.False(FileIdentity.Same(a, b));
        Assert.False(FileIdentity.Same(a, Path.Combine(a, "not-there")));
    }
}
