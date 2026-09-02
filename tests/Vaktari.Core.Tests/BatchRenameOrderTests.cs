using Vaktari.Core;
using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The order a batch rename has to be performed in.
///
/// **Renumbering stopped after zero files.** img001, img002, img003 renumbered
/// to start at 2 asks for img001 to become img002 while img002 is still called
/// img002, and both executors refuse that with "already exists here" — so the
/// commonest batch rename there is failed on its very first row and the dialog
/// said "stopped after 0". The preview was right, and correctly allowed the
/// name because the file holding it was also being renamed. Nothing had an
/// order at all: the rows were applied in the order they were shown.
///
/// The simulation is the test that matters. Asserting a particular sequence
/// would pin one implementation; running the steps against a set of names and
/// checking that nothing ever lands on an occupied one proves the property for
/// whatever order the sequencer chooses.
/// </summary>
public sealed class BatchRenameOrderTests
{
    private static RenamePreview Row(string oldName, string newName)
        => new(Path.Combine(Path.GetTempPath(), oldName), oldName, newName, null);

    /// <summary>
    /// Runs the steps against a set of names, refusing any move onto a name
    /// that is still taken — which is exactly what the file system does.
    /// </summary>
    private static (List<string> Ended, int Renames, int Temporaries) Run(
        IReadOnlyList<RenameStep> steps, params string[] start)
    {
        var live = new HashSet<string>(start, StringComparer.Ordinal);

        // Where each row's file currently is, by leaf name.
        var where = start.ToDictionary(n => Path.Combine(Path.GetTempPath(), n),
                                       n => n, StringComparer.Ordinal);

        var renames = 0;
        var temporaries = 0;

        foreach (var step in steps)
        {
            var from = where[step.FullPath];

            Assert.True(live.Contains(from),
                $"step moves '{from}', which is not there — the order is wrong");

            Assert.True(!live.Contains(step.NewName) || string.Equals(from, step.NewName, StringComparison.Ordinal),
                $"step renames '{from}' onto '{step.NewName}', which is still taken — "
                + "this is the failure the whole sequencer exists to prevent");

            live.Remove(from);
            live.Add(step.NewName);
            where[step.FullPath] = step.NewName;

            if (step.IsTemporary) temporaries++; else renames++;
        }

        return ([.. live.Order(StringComparer.Ordinal)], renames, temporaries);
    }

    /// <summary>
    /// The reported case. Every row wants the name of the row below it, so
    /// applied in preview order the first one fails immediately.
    /// </summary>
    [Fact]
    public void Renumbering_upwards_needs_no_temporary_and_never_collides()
    {
        var plan = new[]
        {
            Row("img001.jpg", "img002.jpg"),
            Row("img002.jpg", "img003.jpg"),
            Row("img003.jpg", "img004.jpg"),
        };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "img001.jpg", "img002.jpg", "img003.jpg");

        Assert.Equal(["img002.jpg", "img003.jpg", "img004.jpg"], ended);
        Assert.Equal(3, renames);

        // A chain drains from the far end. Staging here would be three extra
        // filesystem operations for a case that needs none.
        Assert.Equal(0, temporaries);
    }

    [Fact]
    public void Renumbering_downwards_works_the_same_way()
    {
        var plan = new[]
        {
            Row("img002.jpg", "img001.jpg"),
            Row("img003.jpg", "img002.jpg"),
            Row("img004.jpg", "img003.jpg"),
        };

        var (ended, _, temporaries) = Run(
            BatchRename.Sequence(plan), "img002.jpg", "img003.jpg", "img004.jpg");

        Assert.Equal(["img001.jpg", "img002.jpg", "img003.jpg"], ended);
        Assert.Equal(0, temporaries);
    }

    /// <summary>A genuine swap is the one case that cannot be ordered, and it
    /// needs exactly one staging move.</summary>
    [Fact]
    public void Swapping_two_names_uses_one_temporary()
    {
        var plan = new[] { Row("a.txt", "b.txt"), Row("b.txt", "a.txt") };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "a.txt", "b.txt");

        Assert.Equal(["a.txt", "b.txt"], ended);
        Assert.Equal(2, renames);
        Assert.Equal(1, temporaries);
    }

    /// <summary>A three-way rotation is still one cycle and still one
    /// temporary.</summary>
    [Fact]
    public void A_rotation_uses_one_temporary_however_long_it_is()
    {
        var plan = new[]
        {
            Row("a.txt", "b.txt"),
            Row("b.txt", "c.txt"),
            Row("c.txt", "a.txt"),
        };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "a.txt", "b.txt", "c.txt");

        Assert.Equal(["a.txt", "b.txt", "c.txt"], ended);
        Assert.Equal(3, renames);
        Assert.Equal(1, temporaries);
    }

    /// <summary>Two cycles need one temporary each, and no more.</summary>
    [Fact]
    public void Two_swaps_at_once_use_one_temporary_each()
    {
        var plan = new[]
        {
            Row("a.txt", "b.txt"), Row("b.txt", "a.txt"),
            Row("x.txt", "y.txt"), Row("y.txt", "x.txt"),
        };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "a.txt", "b.txt", "x.txt", "y.txt");

        Assert.Equal(["a.txt", "b.txt", "x.txt", "y.txt"], ended);
        Assert.Equal(4, renames);
        Assert.Equal(2, temporaries);
    }

    /// <summary>A chain feeding into a cycle: the chain drains first and only
    /// the cycle pays for a temporary.</summary>
    [Fact]
    public void A_chain_and_a_cycle_together_cost_one_temporary()
    {
        var plan = new[]
        {
            Row("a.txt", "b.txt"),
            Row("b.txt", "a.txt"),
            Row("p.txt", "q.txt"),
            Row("q.txt", "r.txt"),
        };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "a.txt", "b.txt", "p.txt", "q.txt");

        Assert.Equal(["a.txt", "b.txt", "q.txt", "r.txt"], ended);
        Assert.Equal(4, renames);
        Assert.Equal(1, temporaries);
    }

    /// <summary>Renames that have nothing to do with one another need no
    /// ordering and no staging.</summary>
    [Fact]
    public void Independent_renames_are_left_alone()
    {
        var plan = new[] { Row("a.txt", "one.txt"), Row("b.txt", "two.txt") };

        var (ended, renames, temporaries) = Run(
            BatchRename.Sequence(plan), "a.txt", "b.txt");

        Assert.Equal(["one.txt", "two.txt"], ended);
        Assert.Equal(2, renames);
        Assert.Equal(0, temporaries);
    }

    /// <summary>Rows that are not changing, or that the preview refused, are
    /// not filesystem work.</summary>
    [Fact]
    public void Unchanged_and_refused_rows_are_not_steps()
    {
        var plan = new[]
        {
            Row("same.txt", "same.txt"),
            new RenamePreview(Path.Combine(Path.GetTempPath(), "bad.txt"),
                              "bad.txt", "b/ad.txt", "that name has a slash in it"),
            Row("a.txt", "one.txt"),
        };

        var steps = BatchRename.Sequence(plan);

        Assert.Single(steps);
        Assert.Equal("one.txt", steps[0].NewName);
    }

    /// <summary>
    /// The staging name is somewhere the cycle can park without colliding with
    /// anything the batch is about to claim.
    /// </summary>
    [Fact]
    public void The_staging_name_is_only_ever_passed_through()
    {
        var plan = new[] { Row("a.txt", "b.txt"), Row("b.txt", "a.txt") };

        var steps = BatchRename.Sequence(plan, staging: () => "parked.tmp");

        Assert.Contains(steps, s => s.IsTemporary && s.NewName == "parked.tmp");

        var (ended, _, _) = Run(steps, "a.txt", "b.txt");

        Assert.DoesNotContain("parked.tmp", ended);
    }

    /// <summary>Nothing to do is not an error.</summary>
    [Fact]
    public void An_empty_plan_is_no_steps()
        => Assert.Empty(BatchRename.Sequence([]));
}
