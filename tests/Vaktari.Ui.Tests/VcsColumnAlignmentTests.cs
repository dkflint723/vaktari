using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The version-control slot in the details name column, and the empty one the
/// heading has to reserve to match it.
///
/// **Inside a repository the "name" heading sat 16px left of the names.** The
/// name cell docks a fixed slot for the mark ahead of every filename, so names
/// stay aligned marked or not — and every name therefore starts 16px further
/// right. The heading has no mark and so never moved.
///
/// The heading and the rows are two grids kept in step by hand. The column
/// definitions were already checked against each other; the contents of a cell
/// were not.
/// </summary>
public sealed class VcsColumnAlignmentTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Thumbnails = "clr-namespace:Vaktari.Ui.Thumbnails";

    private static XDocument Markup() => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    /// <summary>
    /// The details row's mark, told apart from the compact and tile ones by the
    /// cell it lives in rather than by an attribute the others merely happen to
    /// omit. The compact mark docks left too — implicitly, by being its
    /// DockPanel's first child — so selecting on DockPanel.Dock would make this
    /// break the day somebody wrote that default out for clarity.
    /// </summary>
    private static XElement Mark(XDocument doc)
        => doc.Descendants(Avalonia + "TextBlock")
              .Single(t => t.Attribute(Thumbnails + "RowVcs.Entry") is not null
                        && t.Parent is { } cell
                        && cell.Name == Avalonia + "DockPanel"
                        && (string?)cell.Attribute("Grid.Column") == "1");

    private static XElement Slot(XDocument doc)
        => doc.Descendants().Single(e => (string?)e.Attribute(X + "Name") == "NameHeadingVcsSlot");

    [Fact]
    public void The_name_heading_reserves_the_same_slot_the_names_do()
    {
        var doc = Markup();
        var mark = Mark(doc);
        var slot = Slot(doc);

        // Both halves must carry the numbers, or the equality below would be
        // satisfied by two absent attributes.
        Assert.NotNull((string?)mark.Attribute("Width"));
        Assert.NotNull((string?)mark.Attribute("Margin"));

        // Checked rather than relied on to find the subject: the row's mark
        // has to be docked first for the slot it reserves to be a left offset.
        Assert.Equal("Left", (string?)mark.Attribute("DockPanel.Dock"));

        Assert.Equal((string?)mark.Attribute("Width"), (string?)slot.Attribute("Width"));
        Assert.Equal((string?)mark.Attribute("Margin"), (string?)slot.Attribute("Margin"));

        // The same condition, so the two appear and disappear together.
        Assert.Contains("IsRepository", (string?)mark.Attribute("IsVisible") ?? "");
    }

    [Fact]
    public void The_reserved_slot_sits_ahead_of_the_heading_and_only_in_a_repository()
    {
        var doc = Markup();
        var slot = Slot(doc);
        var cell = slot.Parent;

        Assert.NotNull(cell);
        Assert.Equal("DockPanel", cell!.Name.LocalName);
        Assert.Equal("1", (string?)cell.Attribute("Grid.Column"));

        // A DockPanel lays out in document order and its last child fills, so a
        // slot written after the button would swallow the heading rather than
        // offset it.
        Assert.Same(slot, cell.Elements().First());
        Assert.Equal("Left", (string?)slot.Attribute("DockPanel.Dock"));

        // It is the name heading it offsets, not some other cell's.
        Assert.Contains(cell.Elements(Avalonia + "Button"),
                        b => (string?)b.Attribute("CommandParameter") == "name");

        // And it collapses outside a repository, or the heading would sit 16px
        // RIGHT of names that never moved.
        Assert.Equal("{Binding IsRepository}", (string?)slot.Attribute("IsVisible"));
    }
}
