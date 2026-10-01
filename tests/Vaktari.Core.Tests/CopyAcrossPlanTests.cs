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
        // Through "\\?\" on Windows, which is the only spelling that reaches
        // the names ending in a space or a dot that some tests make.
        var root = OperatingSystem.IsWindows() ? @"\\?\" + _root : _root;

        try { Directory.Delete(root, recursive: true); } catch { /* a temp dir is not worth failing over */ }
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

    /// <summary>
    /// **From a side opened through "\\?\", the name opens — and the other
    /// side may still not hold it.** "report " is listed as itself there, so
    /// the rule above let it through, and the engine then refused the WHOLE
    /// copy into a plainly opened side, where the name would land as "report"
    /// (0.11.1 path-safety check). It is left out, with its own reason, and
    /// the rest goes; into a side opened through "\\?\" as well, it goes too.
    /// </summary>
    [WindowsFact]
    public void On_windows_a_name_the_other_side_cannot_take_is_left_out()
    {
        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            [@"\\?\" + Listed("report ")] = CompareMark.OnlyHere,
            [@"\\?\" + Listed("report.")] = CompareMark.NewerHere,
            [@"\\?\" + Listed("ok.txt")] = CompareMark.OnlyHere,
        };

        var plain = CopyAcrossPlan.From(marks, Side("right"));

        Assert.Equal(
            [new Withheld(@"\\?\" + Listed("report "), WithheldBecause.NameTheOtherSideCannotTake),
             new Withheld(@"\\?\" + Listed("report."), WithheldBecause.NameTheOtherSideCannotTake)],
            plain.Withheld);
        Assert.Equal([@"\\?\" + Listed("ok.txt")], plain.Missing);
        Assert.Empty(plain.Replacing);

        var extended = CopyAcrossPlan.From(marks, @"\\?\" + Side("right"));

        Assert.Empty(extended.Withheld);
        Assert.Equal([@"\\?\" + Listed("ok.txt"), @"\\?\" + Listed("report ")], extended.Missing);
        Assert.Equal([@"\\?\" + Listed("report.")], extended.Replacing);
    }

    // ---- a name further down a marked folder ---------------------------------

    /// <summary>Makes <paramref name="names"/> under <paramref name="folder"/>
    /// through "\\?\", which keeps a trailing space or dot; a folder is a name
    /// ending in a separator. Answers the folder.</summary>
    private static string Tree(string folder, params string[] names)
    {
        foreach (var name in names)
        {
            var path = @"\\?\" + Path.Combine(folder, name);

            if (name.EndsWith('\\')) Directory.CreateDirectory(path);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, name);
            }
        }

        return folder;
    }

    private static Dictionary<string, CompareMark> OnlyHere(params string[] paths)
        => paths.ToDictionary(p => p, _ => CompareMark.OnlyHere, StringComparer.Ordinal);

    /// <summary>
    /// **A marked folder holding "report " is left out, and the file beside
    /// it still goes.** The engine asks every item down a folder whether it
    /// can be read and written, and refused the WHOLE copy over "report "
    /// inside "docs": "plain.txt" beside it was not copied either, and the
    /// person was told only afterwards (batch-0.11.2 QA,
    /// probe-copy-across-nested). From a side opened through "\\?\" the name
    /// opens, so the reason is the other side's; the row says which name.
    /// </summary>
    [WindowsFact]
    public void On_windows_a_folder_holding_a_name_the_other_side_cannot_take_is_left_out()
    {
        var left = Side("left");
        Tree(left, @"docs\report ", "plain.txt");

        var docs = @"\\?\" + Path.Combine(left, "docs");
        var plain = @"\\?\" + Path.Combine(left, "plain.txt");

        var plan = CopyAcrossPlan.From(OnlyHere(docs, plain), Side("right"));

        Assert.Equal([new Withheld(docs, WithheldBecause.NameTheOtherSideCannotTake, "report ")], plan.Withheld);
        Assert.Equal([plain], plan.Missing);
    }

    /// <summary>From a side opened by its ordinary name, a name inside a
    /// folder is read through its plain spelling — "report", the file beside
    /// it — so the engine refuses to read it, and the folder is left out for
    /// that reason.</summary>
    [WindowsFact]
    public void On_windows_a_plainly_opened_folder_holding_a_name_windows_cannot_open_is_left_out()
    {
        var left = Side("left");
        Tree(left, @"docs\report", @"docs\report ", "plain.txt");

        var docs = Path.Combine(left, "docs");
        var plain = Path.Combine(left, "plain.txt");

        var plan = CopyAcrossPlan.From(OnlyHere(docs, plain), Side("right"));

        Assert.Equal([new Withheld(docs, WithheldBecause.NameWindowsCannotOpen, "report ")], plan.Withheld);
        Assert.Equal([plain], plan.Missing);
    }

    /// <summary>However deep it is: the row names the path below the marked
    /// folder, so the person can find it.</summary>
    [WindowsFact]
    public void On_windows_a_name_deep_down_a_folder_is_found_and_named()
    {
        var left = Side("left");
        Tree(left, @"docs\a\b\c\d\keep.txt", @"docs\a\b\c\d\report.", @"docs\z.txt");

        var docs = @"\\?\" + Path.Combine(left, "docs");

        var plan = CopyAcrossPlan.From(OnlyHere(docs), Side("right"));

        Assert.Equal([new Withheld(docs, WithheldBecause.NameTheOtherSideCannotTake, @"a\b\c\d\report.")], plan.Withheld);
        Assert.Empty(plan.Sources);
    }

    /// <summary>
    /// Each row for its own reason, and the rest goes: a row's own name, a
    /// folder over a name inside it, a folder with nothing wrong in it (a
    /// name with a trailing space in the MIDDLE of an ordinary name is
    /// fine), a newer file, and a folder holding only a folder ending in a
    /// dot.
    /// </summary>
    [WindowsFact]
    public void On_windows_withheld_and_copied_rows_are_told_apart()
    {
        var left = Side("left");
        Tree(left,
             "own ",
             @"bad\fine.txt", @"bad\deeper\x ",
             @"good\a b.txt", @"good\sub\c.txt",
             "newer.txt",
             @"dotted\inner.\");

        string At(string name) => @"\\?\" + Path.Combine(left, name);

        var marks = new Dictionary<string, CompareMark>(StringComparer.Ordinal)
        {
            [At("own ")] = CompareMark.OnlyHere,
            [At("bad")] = CompareMark.OnlyHere,
            [At("good")] = CompareMark.OnlyHere,
            [At("newer.txt")] = CompareMark.NewerHere,
            [At("dotted")] = CompareMark.NewerHere,
        };

        var plan = CopyAcrossPlan.From(marks, Side("right"));

        Assert.Equal(
            [new Withheld(At("bad"), WithheldBecause.NameTheOtherSideCannotTake, @"deeper\x "),
             new Withheld(At("dotted"), WithheldBecause.NameTheOtherSideCannotTake, "inner."),
             new Withheld(At("own "), WithheldBecause.NameTheOtherSideCannotTake)],
            plan.Withheld);
        Assert.Equal([At("good")], plan.Missing);
        Assert.Equal([At("newer.txt")], plan.Replacing);
    }

    /// <summary>
    /// **Into a side opened through "\\?\", the name lands as itself**, so
    /// nothing is left out — and nothing is walked, since nothing down the
    /// folder could be refused there.
    /// </summary>
    [WindowsFact]
    public void On_windows_into_a_side_opened_through_the_prefix_the_folder_goes()
    {
        var left = Side("left");
        Tree(left, @"docs\report ", @"docs\a\report.", "plain.txt");

        var docs = @"\\?\" + Path.Combine(left, "docs");
        var plain = @"\\?\" + Path.Combine(left, "plain.txt");
        var right = @"\\?\" + Side("right");

        Assert.False(CopyAcrossPlan.MustLookInside(docs, right));

        var plan = CopyAcrossPlan.From(OnlyHere(docs, plain), right);

        Assert.Empty(plan.Withheld);
        Assert.Equal([docs, plain], plan.Missing);

        // From a plainly opened side into it, the name is still read through
        // its plain spelling, so that folder is still looked inside.
        Assert.True(CopyAcrossPlan.MustLookInside(Path.Combine(left, "docs"), right));
    }

    /// <summary>A walk the asker has given up on stops, rather than reading
    /// the rest of the tree for a prompt nobody will see.</summary>
    [WindowsFact]
    public void On_windows_a_cancelled_walk_stops()
    {
        var left = Side("left");
        Tree(left, @"docs\a.txt");

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => CopyAcrossPlan.From(OnlyHere(Path.Combine(left, "docs")), Side("right"), cancel.Token));
    }

    /// <summary>On Linux a trailing space is an ordinary character, nothing
    /// is refused, and no folder is walked.</summary>
    [PosixFact]
    public void On_linux_a_folder_holding_report_with_a_space_goes()
    {
        var left = Side("left");
        Directory.CreateDirectory(Path.Combine(left, "docs"));
        File.WriteAllText(Path.Combine(left, "docs", "report "), "x");

        var docs = Path.Combine(left, "docs");

        Assert.False(CopyAcrossPlan.MustLookInside(docs, Side("right")));

        var plan = CopyAcrossPlan.From(OnlyHere(docs), Side("right"));

        Assert.Empty(plan.Withheld);
        Assert.Equal([docs], plan.Missing);
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
