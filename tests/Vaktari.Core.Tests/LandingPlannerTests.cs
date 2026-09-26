using Vaktari.Core.FileSystem;
using Xunit;

namespace Vaktari.Core.Tests;

/// <summary>
/// The names entries land under, in archive order: the first keeps its name,
/// later ones are numbered, and a folder is known by its raw prefix.
/// </summary>
public sealed class LandingPlannerTests
{
    private static string Place(LandingPlanner planner, string key, bool folder = false)
    {
        var segments = key.Split('/');

        return string.Join('/', (folder ? planner.PlaceFolder(segments) : planner.PlaceFile(segments))!);
    }

    [Fact]
    public void Duplicate_files_are_numbered_in_order()
    {
        var planner = new LandingPlanner(windowsRules: false);

        Assert.Equal("notes.txt", Place(planner, "notes.txt"));
        Assert.Equal("notes (2).txt", Place(planner, "notes.txt"));
        Assert.Equal("notes (3).txt", Place(planner, "notes.txt"));
        Assert.Equal(2, planner.Renamed);
    }

    /// <summary>Two folders to the archive, one folder to Windows: without
    /// numbering, two trees would merge into one.</summary>
    [Fact]
    public void Folders_differing_only_in_case_are_numbered_under_Windows_rules()
    {
        var windows = new LandingPlanner(windowsRules: true);

        Assert.Equal("Docs/a.txt", Place(windows, "Docs/a.txt"));
        Assert.Equal("docs (2)/b.txt", Place(windows, "docs/b.txt"));

        var posix = new LandingPlanner(windowsRules: false);

        Assert.Equal("Docs/a.txt", Place(posix, "Docs/a.txt"));
        Assert.Equal("docs/b.txt", Place(posix, "docs/b.txt"));
    }

    [Fact]
    public void A_file_then_a_folder_of_the_same_name_numbers_the_folder()
    {
        var planner = new LandingPlanner(windowsRules: false);

        Assert.Equal("a", Place(planner, "a"));
        Assert.Equal("a (2)/b.txt", Place(planner, "a/b.txt"));
    }

    [Fact]
    public void A_folder_named_again_is_the_same_folder()
    {
        var planner = new LandingPlanner(windowsRules: false);

        Assert.Equal("docs", Place(planner, "docs", folder: true));
        Assert.Equal("docs/a.txt", Place(planner, "docs/a.txt"));
        Assert.Equal("docs", Place(planner, "docs", folder: true));
        Assert.Equal("docs/b.txt", Place(planner, "docs/b.txt"));
        Assert.Equal(0, planner.Renamed);
    }

    [Fact]
    public void Numbering_keeps_the_length_inside_the_limit()
    {
        var planner = new LandingPlanner(windowsRules: false);
        var name = new string('n', 251) + ".txt";

        Place(planner, name);

        var second = Place(planner, name);

        Assert.EndsWith(" (2).txt", second);
        Assert.True(second.Length <= 255);
    }

    [Fact]
    public void A_replaced_name_counts_as_renamed()
    {
        var planner = new LandingPlanner(windowsRules: true);

        Assert.Equal("a_b.txt", Place(planner, "a:b.txt"));
        Assert.Equal(1, planner.Renamed);
    }

    /// <summary>Names that land the same after replacement are still two
    /// entries: the second is numbered, so the listing and the disk agree.</summary>
    [Fact]
    public void Two_names_that_land_alike_are_both_kept()
    {
        var planner = new LandingPlanner(windowsRules: true);

        Assert.Equal("a_b", Place(planner, "a:b"));
        Assert.Equal("a_b (2)", Place(planner, "a?b"));
    }
}
