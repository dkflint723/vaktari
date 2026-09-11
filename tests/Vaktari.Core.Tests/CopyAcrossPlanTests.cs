using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// Copying what is newer or missing on one side of a comparison to the other:
/// what the plan takes and leaves out, and what the copy does about a file
/// already there when it arrives.
///
/// The clashes are decided against real files, because the point of deciding
/// at the last moment is to see what is there at that moment.
/// </summary>
public sealed class CopyAcrossPlanTests : IDisposable
{
    private static readonly DateTime Noon = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Directory.CreateTempSubdirectory("vaktari-across").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
    }

    private string Side(string side) => Directory.CreateDirectory(Path.Combine(_root, side)).FullName;

    private string Write(string side, string name, string content, DateTime written)
    {
        var path = Path.Combine(Side(side), name);

        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, written);

        return path;
    }

    private CopyAcrossPlan Replacing(string here) => new(Side("right"), [], [here]);

    /// <summary>A path for the plan alone: nothing is made on disk.</summary>
    private static string Listed(params string[] names)
        => Path.Combine(new[] { Path.GetTempPath(), "vaktari-left" }.Concat(names).ToArray());

    // ---- the plan ------------------------------------------------------------

    [Fact]
    public void It_takes_what_the_other_side_lacks_and_what_is_newer_here()
    {
        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            [Listed("only.txt")] = CompareMark.OnlyHere,
            [Listed("newer.txt")] = CompareMark.NewerHere,
            [Listed("older.txt")] = CompareMark.OlderHere,
            [Listed("build")] = CompareMark.Differs,
        };

        var plan = CopyAcrossPlan.From(marks, Side("right"));

        Assert.Equal([Listed("only.txt")], plan.Missing);
        Assert.Equal([Listed("newer.txt")], plan.Replacing);
        Assert.Equal([Listed("only.txt"), Listed("newer.txt")], plan.Sources);
        Assert.Empty(plan.Withheld);
        Assert.Equal(Side("right"), plan.Destination);
    }

    /// <summary>
    /// **A folder the other side is inside is left out**, and nothing else is:
    /// one side showing a folder within the other marks that folder "only
    /// here", and both engines refuse the whole copy over a folder sent into
    /// itself.
    /// </summary>
    [Fact]
    public void A_folder_the_other_side_is_inside_is_left_out()
    {
        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            [Listed("inner")] = CompareMark.OnlyHere,
            [Listed("a.txt")] = CompareMark.OnlyHere,
        };

        var plan = CopyAcrossPlan.From(marks, Listed("inner", "deeper"));

        Assert.Equal([new Withheld(Listed("inner"), WithheldBecause.HoldsTheOtherSide)], plan.Withheld);
        Assert.Equal([Listed("a.txt")], plan.Missing);
    }

    /// <summary>A name ending in a space is one the engine refuses the whole
    /// copy over on Windows, so it is left out and the rest can go.</summary>
    [WindowsFact]
    public void On_windows_a_name_it_cannot_open_is_left_out()
    {
        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            [Listed("report ")] = CompareMark.OnlyHere,
            [Listed("report")] = CompareMark.NewerHere,
        };

        var plan = CopyAcrossPlan.From(marks, Side("right"));

        Assert.Equal([new Withheld(Listed("report "), WithheldBecause.NameWindowsCannotOpen)], plan.Withheld);
        Assert.Equal([Listed("report")], plan.Replacing);
    }

    // ---- a clash, when the copy reaches it -----------------------------------

    [Fact]
    public void A_file_named_as_newer_replaces_the_older_one_there()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Write("right", "notes.txt", "old", Noon);
        var plan = Replacing(here);

        Assert.Equal(ConflictResolution.Overwrite, plan.Decide(new FileConflict(here, there)));
        Assert.Empty(plan.LeftAlone);
    }

    /// <summary>**A file saved there after the prompt is the newer one now**,
    /// and replacing it would lose the very change that made it so.</summary>
    [Fact]
    public void A_file_changed_there_since_is_left_alone()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Write("right", "notes.txt", "newest", Noon.AddDays(2));
        var plan = Replacing(here);

        Assert.Equal(ConflictResolution.Skip, plan.Decide(new FileConflict(here, there)));
        Assert.Equal([here], plan.LeftAlone);
    }

    /// <summary>The same file there now: somebody got to it first, and there
    /// is nothing left to replace.</summary>
    [Fact]
    public void The_same_file_there_now_is_left_alone()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Write("right", "notes.txt", "new", Noon.AddDays(1).AddSeconds(1));

        Assert.Equal(ConflictResolution.Skip, Replacing(here).Decide(new FileConflict(here, there)));
    }

    /// <summary>Changed at the same moment at a different size: nothing says
    /// which of the two should win, so neither replaces the other.</summary>
    [Fact]
    public void Two_changed_at_one_moment_at_different_sizes_are_left_alone()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Write("right", "notes.txt", "longer", Noon.AddDays(1).AddSeconds(1));

        Assert.Equal(ConflictResolution.Skip, Replacing(here).Decide(new FileConflict(here, there)));
    }

    /// <summary>A name the other side lacked when the plan was made and has
    /// since: the prompt never offered to replace it.</summary>
    [Fact]
    public void Something_that_turned_up_there_since_is_left_alone()
    {
        var here = Write("left", "report.pdf", "mine", Noon.AddDays(1));
        var there = Write("right", "report.pdf", "theirs", Noon);

        var plan = new CopyAcrossPlan(Side("right"), [here], []);

        Assert.Equal(ConflictResolution.Skip, plan.Decide(new FileConflict(here, there)));
        Assert.Equal([here], plan.LeftAlone);
    }

    /// <summary>Where the older file was there is now a folder, which no file
    /// replaces. Asked about, rather than throwing at a length a folder does
    /// not have.</summary>
    [Fact]
    public void A_folder_where_the_older_file_was_is_left_alone()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Directory.CreateDirectory(Path.Combine(Side("right"), "notes.txt")).FullName;

        Assert.Equal(ConflictResolution.Skip, Replacing(here).Decide(new FileConflict(here, there)));
    }

    [Fact]
    public void A_file_gone_from_here_since_is_left_alone()
    {
        var here = Path.Combine(Side("left"), "gone.txt");
        var there = Write("right", "gone.txt", "old", Noon);

        Assert.Equal(ConflictResolution.Skip, Replacing(here).Decide(new FileConflict(here, there)));
    }

    /// <summary>
    /// A path spelled otherwise is not the file the prompt named, even where it
    /// names the same file: the engine hands back the path it was given, so a
    /// different spelling means something other than the plan asked for. On
    /// Windows the capitals below name the same file; on Linux, none.
    /// </summary>
    [Fact]
    public void A_path_spelled_otherwise_is_not_the_file_the_prompt_named()
    {
        var here = Write("left", "notes.txt", "new", Noon.AddDays(1));
        var there = Write("right", "notes.txt", "old", Noon);

        Assert.Equal(ConflictResolution.Skip, Replacing(here).Decide(new FileConflict(here.ToUpperInvariant(), there)));
    }
}
