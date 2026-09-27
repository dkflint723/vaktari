using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The bin's item menu offers Delete permanently.
///
/// **Its only per-item route out was Restore.** Right-clicking something in the
/// bin offered Cut, Copy, Rename and Move to bin — all four hidden or refused
/// on a listing whose rows carry the path a file USED to occupy — and no way at
/// all to get rid of just that one thing. Both references put Restore and
/// Delete beside each other there.
///
/// Its own file, and parsed rather than grepped: a substring search for
/// "Delete permanently" matches the confirmation prompt's own wording several
/// hundred lines away, and would pass against a menu that never gained a row.
/// </summary>
public sealed class BinPurgeRowTests
{
    private static XDocument Markup()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    private static IEnumerable<XElement> MenuItems(XDocument markup)
        => markup.Descendants().Where(e => e.Name.LocalName == "MenuItem");

    /// <summary>The row exists, and it is the purge that it runs.</summary>
    [Fact]
    public void The_item_menu_offers_deleting_one_thing_for_good()
    {
        var row = Assert.Single(
            MenuItems(Markup()),
            e => (string?)e.Attribute("Command") is { } c && c.Contains("PurgeFromTrash"));

        Assert.Contains("permanently", ((string?)row.Attribute("Header") ?? "").ToLowerInvariant());
    }

    /// <summary>
    /// **Shown only where it can act**, or the bin's one working row would sit
    /// in every folder's menu doing nothing — which is the shape of the four
    /// entries that were already there.
    /// </summary>
    [Fact]
    public void And_only_where_there_is_something_binned_to_delete()
    {
        var row = Assert.Single(
            MenuItems(Markup()),
            e => (string?)e.Attribute("Command") is { } c && c.Contains("PurgeFromTrash"));

        Assert.Contains("CanPurgeFromBin", (string?)row.Attribute("IsVisible") ?? "");
    }

    /// <summary>
    /// And no rule is left behind around it. A rule that stays when everything
    /// around it is hidden is the stray line at the top of a menu that this
    /// codebase has collected several of — and in the bin almost everything is
    /// hidden.
    ///
    /// **This read the markup for a rule gated on CanPurgeFromBin**, which was
    /// how every rule in the one listing menu was kept honest: copied by hand
    /// from the rows it introduced. The two listing menus decide their rules as
    /// they open instead, from what is drawn (MainWindow.TidyRules), so there
    /// is no such gate to find. What the gate was for is asserted where it can
    /// be seen: the bin's item menu, opened on a real window, with a row
    /// selected, ends up as exactly the purge and the rows around it that
    /// apply, and no rule first, last or doubled —
    /// ListingMenusTests.The_bins_item_menu_is_the_purge_and_nothing_that_refuses.
    /// Here, only that the row opens a block of its own kind: it sits in the
    /// block that removes things, after a rule.
    /// </summary>
    [Fact]
    public void It_sits_in_the_block_that_removes_things()
    {
        var menu = ListingMenus.Markup(ListingMenus.Item);
        var children = menu.Elements().ToList();

        var purge = children.FindIndex(
            e => (string?)e.Attribute("Command") is { } c && c.Contains("PurgeFromTrash"));

        Assert.True(purge > 0, "the purge row is not a direct child of the item menu");

        // Back to the rule that opens its block: nothing between but the rows
        // that rename and bin, which are the other ways a row leaves a folder.
        var rule = children.FindLastIndex(purge, e => e.Name.LocalName == "Separator");

        Assert.True(rule >= 0, "no rule opens the purge row's block");

        var block = children.Skip(rule + 1).Take(purge - rule - 1)
            .Select(e => (string?)e.Attribute(
                XNamespace.Get("clr-namespace:Vaktari.Ui.Input") + "KeyHint.Command"))
            .ToList();

        Assert.Equal(["Rename", "BatchRename", "Trash"], block);
    }
}
