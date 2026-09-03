using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Every sidebar section has a fold, and its rows answer to the same one.
///
/// A heading that folds nothing, or a body bound to a different section's
/// state, is invisible to a view-model test: both halves are markup, and the
/// only thing that connects them is the name written twice.
///
/// Parsed rather than grepped. "IsCollapsed" appears in several bindings and a
/// substring search cannot tell the heading's ToggleButton from the ItemsControl
/// underneath it.
/// </summary>
public sealed class SidebarFoldMarkupTests
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static XDocument Markup() => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"));

    private static List<XElement> Folds(XDocument markup)
        => markup.Descendants(Avalonia + "ToggleButton")
                 .Where(e => (string?)e.Attribute("Classes") == "section")
                 .ToList();

    /// <summary>
    /// The five headings: the provider group template, and the four written
    /// into the markup by hand.
    /// </summary>
    [Fact]
    public void Every_section_heading_is_a_fold()
    {
        var bound = Folds(Markup())
            .Select(e => (string?)e.Attribute("IsChecked") ?? "")
            .ToList();

        Assert.Equal(5, bound.Count);

        Assert.Contains(bound, b => b.Contains("IsCollapsed") && !b.Contains("Sidebar."));

        foreach (var section in new[] { "Network", "Remote", "Sharing", "Recent" })
            Assert.Contains(bound, b => b.Contains($"Sidebar.Is{section}Collapsed"));
    }

    /// <summary>
    /// **Two-way, or the fold is a button that lights up and does nothing.**
    /// A ToggleButton keeps its own checked state, so a one-way binding would
    /// still animate on the click and leave the section on screen.
    /// </summary>
    [Fact]
    public void Each_one_writes_its_answer_back()
    {
        foreach (var fold in Folds(Markup()))
            Assert.Contains("Mode=TwoWay", (string?)fold.Attribute("IsChecked") ?? "");
    }

    /// <summary>
    /// And something under each heading is actually hidden by it. Counted
    /// against the headings so a section added later without a body to fold
    /// fails here rather than shipping a heading that does nothing.
    /// </summary>
    [Fact]
    public void Every_fold_hides_something()
    {
        var markup = Markup();

        var hidden = markup.Descendants()
            .Select(e => (string?)e.Attribute("IsVisible") ?? "")
            .Where(v => v.Contains("Collapsed"))
            .ToList();

        foreach (var section in new[] { "Network", "Remote", "Sharing", "Recent" })
            Assert.Contains(hidden, v => v.Contains($"!Sidebar.Is{section}Collapsed"));

        // The provider groups: one body, bound without the Sidebar prefix
        // because the group is the DataContext there.
        Assert.Contains(hidden, v => v == "{Binding !IsCollapsed}");
    }

    /// <summary>
    /// **The keys are namespaced away from provider labels.** Folding is one
    /// set of strings matched without case, and a places provider is free to
    /// call a group NETWORK — two of the repository's own fakes do.
    /// </summary>
    [Fact]
    public void The_fixed_keys_cannot_collide_with_a_group_label()
    {
        var source = RepoSource.Ui("ViewModels", "SidebarSections.cs");

        foreach (var key in new[] { "network", "remote", "sharing", "recent" })
            Assert.Contains($"\"section:{key}\"", source);
    }

    /// <summary>
    /// F6 lands on a place row rather than on a heading. Pinned in the window
    /// too, on the real focused element; this says the rule is written where a
    /// reader will find it, and that deleting it is a visible act.
    /// </summary>
    [Fact]
    public void The_keyboard_skips_the_headings_on_its_way_in()
    {
        var body = RepoSource.Body(
            RepoSource.Ui("MainWindow.axaml.cs"),
            "private Control? FirstSidebarRow()");

        Assert.Contains("is not Avalonia.Controls.Primitives.ToggleButton", body);
    }
}
