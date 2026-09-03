using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Which failures are worth starting again from.
///
/// **A folder that failed drags its contents down with it**, and every one of
/// those is recorded too — a folder that could not be created means every file
/// planned inside it also failed. Offering "retry 431" for one unreadable
/// folder is a number that tells the person nothing, and retrying each
/// descendant separately re-attempts work the folder's own retry does anyway.
/// </summary>
public sealed class RetryRootsTests
{
    private static RetryRoot Folder(string source, string target)
        => new(source, target, IsDirectory: true);

    private static RetryRoot File_(string source, string target)
        => new(source, target, IsDirectory: false);

    private static string P(params string[] parts) => Path.Combine([Path.GetTempPath(), .. parts]);

    [Fact]
    public void One_failed_file_is_offered_on_its_own()
    {
        var only = File_(P("a.txt"), P("dst", "a.txt"));

        Assert.Equal([only], RetryRoots.Outermost([only]));
    }

    /// <summary>The whole point: the folder is the offer, not its four hundred
    /// planned children.</summary>
    [Fact]
    public void A_failure_inside_a_failed_folder_is_not_offered_on_its_own()
    {
        var folder = Folder(P("A"), P("dst", "A"));
        var inside = File_(P("A", "b.txt"), P("dst", "A", "b.txt"));

        Assert.Equal([folder], RetryRoots.Outermost([folder, inside]));
    }

    /// <summary>And it does not exclude itself, which a plain prefix test
    /// would.</summary>
    [Fact]
    public void The_folder_itself_survives()
    {
        var folder = Folder(P("A"), P("dst", "A"));

        Assert.Equal([folder], RetryRoots.Outermost([folder]));
    }

    /// <summary>
    /// Deeply, not just one level: an unreadable folder's whole subtree is one
    /// offer.
    /// </summary>
    [Fact]
    public void Nor_is_anything_further_down()
    {
        var folder = Folder(P("A"), P("dst", "A"));
        var deep = File_(P("A", "b", "c", "d.txt"), P("dst", "A", "b", "c", "d.txt"));

        Assert.Equal([folder], RetryRoots.Outermost([folder, deep]));
    }

    /// <summary>
    /// **"/media/one" must not claim "/media/onetwo".** The rule compares path
    /// segments rather than text, which is the same trap the mount table and
    /// the search scope each carry a comment about.
    /// </summary>
    [Fact]
    public void A_neighbour_that_merely_starts_the_same_way_is_still_offered()
    {
        var folder = Folder(P("one"), P("dst", "one"));
        var neighbour = File_(P("onetwo", "b.txt"), P("dst", "onetwo", "b.txt"));

        Assert.Equal(2, RetryRoots.Outermost([folder, neighbour]).Count);
    }

    /// <summary>
    /// A FILE never swallows anything, however its path reads. Only a folder
    /// can contain another failure, and that is carried rather than probed on
    /// disk — by the time this runs the source may have moved or gone.
    /// </summary>
    [Fact]
    public void A_failed_file_never_swallows_anything()
    {
        // Deliberately shaped so a Directory.Exists probe would answer
        // differently from the carried flag: neither of these exists at all.
        var file = File_(P("a"), P("dst", "a"));
        var under = File_(P("a", "b.txt"), P("dst", "a", "b.txt"));

        Assert.Equal(2, RetryRoots.Outermost([file, under]).Count);
    }

    /// <summary>
    /// Two unrelated failures are two offers, which is the common case: a
    /// handful of files in one folder, each held open by something different.
    /// </summary>
    [Fact]
    public void Unrelated_failures_are_all_offered()
    {
        var one = File_(P("a.txt"), P("dst", "a.txt"));
        var two = File_(P("b.txt"), P("dst", "b.txt"));
        var three = Folder(P("C"), P("dst", "C"));

        Assert.Equal(3, RetryRoots.Outermost([one, two, three]).Count);
    }

    [Fact]
    public void Nothing_failed_is_nothing_to_offer()
        => Assert.Empty(RetryRoots.Outermost([]));

    /// <summary>
    /// The target travels with the source, because that is the whole reason
    /// this record exists — a Keep both has renamed the root it lives under and
    /// recomputing "source into destination" would put it back in the folder
    /// the user asked to keep separate.
    /// </summary>
    [Fact]
    public void The_place_it_was_going_survives_the_filter()
    {
        var kept = Folder(P("A"), P("dst", "A (2)"));

        Assert.Equal(P("dst", "A (2)"), RetryRoots.Outermost([kept]).Single().Target);
    }
}
