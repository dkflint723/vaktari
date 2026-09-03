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
    /// And its separator goes with it. A rule that stays behind when everything
    /// around it is hidden is the stray line at the top of a menu that this
    /// codebase has collected several of.
    /// </summary>
    [Fact]
    public void Its_separator_hides_with_it()
    {
        var separators = Markup().Descendants()
            .Where(e => e.Name.LocalName == "Separator")
            .Select(e => (string?)e.Attribute("IsVisible") ?? "")
            .ToList();

        Assert.Contains(separators, v => v.Contains("CanPurgeFromBin"));
    }
}
