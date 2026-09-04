using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Counting what a folder merge is actually going to argue about.
///
/// **The prompt had no number and the wrong word.** Answering Overwrite to a
/// folder that is already there merges the two trees — the engine's Overwrite
/// arm for a directory does nothing but continue into CreateDirectory on a
/// directory that exists — so the destination keeps everything it had and only
/// the colliding names are decided. The dialog said "Overwrite" and offered no
/// count at all, which is the one fact that tells somebody whether the answer
/// matters.
///
/// This is the count, in Core rather than in the view model, so the engine's
/// own tests can check it against the number of conflicts a real merge raises.
/// </summary>
public sealed class FolderMergeTests : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("vaktari-merge").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private string File_(string relative)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");

        return path;
    }

    private string Dir(string relative)
    {
        var path = Path.Combine(_root, relative);

        Directory.CreateDirectory(path);

        return path;
    }

    /// <summary>
    /// **Every depth, not the top one.** A merge is recursive: the engine plans
    /// each descendant against its own place in the destination, so a name that
    /// collides three folders down is as much of a decision as one at the top,
    /// and a count that stopped at the first level would understate it.
    /// </summary>
    [Fact]
    public void It_counts_a_name_that_collides_at_any_depth()
    {
        var arriving = Dir("arriving");
        File_(Path.Combine("arriving", "notes.txt"));
        File_(Path.Combine("arriving", "raw", "one.cr2"));
        File_(Path.Combine("arriving", "raw", "two.cr2"));
        File_(Path.Combine("arriving", "fresh.txt"));

        var there = Dir("there");
        File_(Path.Combine("there", "notes.txt"));
        File_(Path.Combine("there", "raw", "one.cr2"));
        File_(Path.Combine("there", "of-its-own.txt"));

        // notes.txt, the raw folder itself, and raw\one.cr2. fresh.txt and
        // raw\two.cr2 collide with nothing; of-its-own.txt is not arriving.
        var merge = FolderMerge.Between(arriving, there);

        Assert.Equal(3, merge.Clashes);
        Assert.False(merge.Partial);
    }

    /// <summary>Two folders of the same name holding nothing in common: a merge
    /// that costs nothing, and worth saying so.</summary>
    [Fact]
    public void Nothing_in_common_is_no_clashes()
    {
        var arriving = Dir("arriving");
        File_(Path.Combine("arriving", "a.txt"));
        File_(Path.Combine("arriving", "deep", "b.txt"));

        var there = Dir("there");
        File_(Path.Combine("there", "c.txt"));

        Assert.Equal(new FolderMerge(0, Partial: false), FolderMerge.Between(arriving, there));
    }

    /// <summary>
    /// **The prompt is built on the UI thread while somebody waits for it**, and
    /// each entry costs two stats, so a tree of a hundred thousand files is
    /// answered with a floor rather than with a dialog that takes a visible
    /// moment to open. Partial is what makes the sentence say "at least".
    /// </summary>
    [Fact]
    public void A_tree_past_the_ceiling_reports_a_floor()
    {
        var arriving = Dir("arriving");
        var there = Dir("there");

        for (var i = 0; i < 10; i++)
        {
            File_(Path.Combine("arriving", $"f{i}.txt"));
            File_(Path.Combine("there", $"f{i}.txt"));
        }

        var merge = FolderMerge.Between(arriving, there, ceiling: 4);

        Assert.True(merge.Partial, "a walk that stopped early must say so");
        Assert.Equal(4, merge.Clashes);

        // And the same tree read whole is the total the floor was under.
        Assert.Equal(new FolderMerge(10, Partial: false), FolderMerge.Between(arriving, there));
    }

    /// <summary>
    /// **A walk that cannot be made must not take the prompt down with it, and
    /// must not answer differently per platform.** A root that is not a folder
    /// throws in two different places: on Windows out of FindNextEntry on the
    /// first MoveNext, past SafeWalk's guard, which is how this test threw
    /// before there was anything to stop it; on Linux out of
    /// CreateDirectoryHandle at the call, which SafeWalk swallows, so the walk
    /// yields nothing and the count would have claimed a clean merge. Both
    /// measured. FolderMerge asks Directory.Exists outright rather than
    /// inheriting either answer.
    ///
    /// Nothing counted, and said to be a floor rather than a total: the
    /// sentence must not claim nothing collides on the strength of a walk that
    /// never happened.
    /// </summary>
    [Fact]
    public void A_walk_that_cannot_be_finished_counts_no_further()
    {
        var file = File_("lonely.txt");

        Assert.Equal(new FolderMerge(0, Partial: true), FolderMerge.Between(file, _root));
    }
}
