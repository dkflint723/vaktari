using System.Runtime.Versioning;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Tests;
using Xunit;

namespace Vaktari.Windows.Tests;

/// <summary>
/// The attribute panel, for one file and for a whole selection.
///
/// **A selection's properties window stopped at the size line.** One item fills
/// the lower half from the platform's own groups; a selection asked for none,
/// so twenty files selected gave a count, a total, and then empty space. On
/// Windows that is the whole of the answer — the shell's own sheet is declined
/// for more than one path, because SHMultiFileProperties wants an ITEMIDLIST
/// array and shows less than this window does.
///
/// Both panels are held here because both read one flag table. A flag dropped
/// from it, or a value read the wrong way round, changes the window every file
/// opens as well as the selection's.
/// </summary>
[SupportedOSPlatform("windows")]
public class AttributeSummaryTests
{
    private static string Row(PropertyGroup group, string label)
        => group.Rows.Single(r => r.Label == label).Value;

    /// <summary>
    /// The panel ONE file gets, out of the same table.
    ///
    /// **Nothing watched the single-item panel at all.** Sharing one flag table
    /// between the two windows is only worth anything if both sides are pinned:
    /// a flag dropped from the table, or a value read the wrong way round,
    /// changes the window every file opens as well as the selection's, and only
    /// the selection's was under test.
    /// </summary>
    [WindowsFact]
    public async Task One_file_still_lists_every_ordinary_flag()
    {
        using var tree = new TempTree();

        var details = await new WindowsPropertiesProvider().GetAsync(
            tree.WriteReadOnly("a.txt"), CancellationToken.None);

        var attributes = Assert.Single(details.Groups);

        Assert.Equal("attributes", attributes.Label);
        Assert.Equal("yes", Row(attributes, "Read-only"));
        Assert.Equal("no", Row(attributes, "Hidden"));
        Assert.Equal("no", Row(attributes, "System"));
        Assert.Equal(4, attributes.Rows.Count);
    }

    /// <summary>
    /// "Mixed" is the answer a single-item sheet never has to give, and the
    /// reason a selection's window is worth opening.
    /// </summary>
    [WindowsFact]
    public async Task A_flag_only_some_of_them_carry_reads_mixed()
    {
        using var tree = new TempTree();

        tree.Write("a.txt");
        tree.WriteReadOnly("b.txt");

        var groups = await new WindowsPropertiesProvider().GetSharedAsync(
            [tree.At("a.txt"), tree.At("b.txt")], CancellationToken.None);

        var attributes = Assert.Single(groups);

        Assert.Equal("attributes", attributes.Label);
        Assert.Equal("mixed", Row(attributes, "Read-only"));

        // What stops "mixed" from being a blanket answer.
        Assert.Equal("no", Row(attributes, "Hidden"));

        // And the unusual flags stay out while nothing carries them.
        Assert.Equal(4, attributes.Rows.Count);
    }

    [WindowsFact]
    public async Task A_flag_every_one_of_them_carries_is_stated_outright()
    {
        using var tree = new TempTree();

        tree.WriteReadOnly("a.txt");
        tree.WriteReadOnly("b.txt");

        var groups = await new WindowsPropertiesProvider().GetSharedAsync(
            [tree.At("a.txt"), tree.At("b.txt")], CancellationToken.None);

        Assert.Equal("yes", Row(Assert.Single(groups), "Read-only"));
    }

    /// <summary>
    /// A selection that has gone gets no panel rather than a fabricated one.
    /// GetAttributes throws for a path that is not there; FileInfo.Attributes
    /// would have answered -1, which is every flag set.
    /// </summary>
    [WindowsFact]
    public async Task A_selection_that_is_no_longer_there_describes_nothing()
    {
        using var tree = new TempTree();

        var groups = await new WindowsPropertiesProvider().GetSharedAsync(
            [tree.At("gone.txt"), tree.At("also-gone.txt")], CancellationToken.None);

        Assert.Empty(groups);
    }

    /// <summary>And the when-set flags carry their own three states.</summary>
    [WindowsFact]
    public async Task A_reparse_point_among_ordinary_folders_is_not_claimed_for_all_of_them()
    {
        using var tree = new TempTree();

        var plain = tree.Dir("plain");
        var link = tree.Junction("link", plain);

        var groups = await new WindowsPropertiesProvider().GetSharedAsync(
            [plain, link], CancellationToken.None);

        Assert.Equal("mixed", Row(Assert.Single(groups), "Reparse point"));
    }
}
