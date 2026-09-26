using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Finding the listing's two menus, in the markup and on a built window.
///
/// **There was one, and every test found it as "the ContextMenu whose DataType
/// is the pane group" or "the first menu above the listing".** Both answers
/// became ambiguous the moment it was split into the item menu and the
/// background menu — the first matches both, and the second always finds the
/// item menu, which hangs lower. Each test now names the menu it means, which
/// is the whole reason the markup gives them names.
/// </summary>
internal static class ListingMenus
{
    private static readonly XNamespace Avalonia = "https://github.com/avaloniaui";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The menu for a row or tile, on the ItemsControl that holds the
    /// tabs.</summary>
    internal const string Item = "ItemMenu";

    /// <summary>The menu for empty space, on the Panel around it.</summary>
    internal const string Background = "BackgroundMenu";

    /// <summary>One of the two menus as the markup declares it.</summary>
    internal static XElement Markup(string name)
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Avalonia + "ContextMenu")
            .Single(m => (string?)m.Attribute(Xaml + "Name") == name);

    /// <summary>A direct row of a menu in the markup, by the words it shows.</summary>
    internal static XElement MarkupRow(XElement menu, string words)
        => menu.Elements(Avalonia + "MenuItem")
            .Single(m => MenuLabels.Plain((string?)m.Attribute("Header")) == words);

    /// <summary>
    /// One of the two menus on a built window, walked up to from a listing the
    /// way MainWindow.OpenListingMenu walks.
    /// </summary>
    internal static ContextMenu Above(Visual listing, string name)
    {
        for (var visual = (Visual?)listing; visual is not null; visual = visual.GetVisualParent())
            if (visual is Control { ContextMenu: { } menu } && menu.Name == name) return menu;

        throw new InvalidOperationException($"nothing above the listing carries the {name}");
    }

    /// <summary>
    /// What a person sees when the menu is open, in order: the direct children
    /// the bindings and the tidying left visible. Measured on Avalonia 12.1:
    /// with the menu CLOSED every child reads IsVisible true, so this means
    /// something only while it is open.
    /// </summary>
    internal static List<Control> Seen(ItemsControl menu)
        => [.. menu.Items.OfType<Control>().Where(c => c.IsVisible)];

    /// <summary>
    /// The words a row shows — a literal header without its access-key marker,
    /// a bound one as the binding left it, a header built from Runs as the
    /// Runs read — and "—" for a rule, so a whole menu reads as one list.
    /// </summary>
    internal static string Words(Control row) => row switch
    {
        Separator => "—",
        MenuItem { Header: string text } => MenuLabels.Plain(text),
        MenuItem { Header: TextBlock block } => block.Inlines is { Count: > 0 } inlines
            ? string.Concat(inlines.OfType<Run>().Select(r => r.Text))
            : block.Text ?? "",
        MenuItem { Header: var other } => other?.ToString() ?? "",
        _ => row.GetType().Name,
    };

    /// <summary>The visible rows as words, for an assertion or a message.</summary>
    internal static List<string> Read(ItemsControl menu) => [.. Seen(menu).Select(Words)];

    /// <summary>A row by the words it shows, visible or not, among a menu's own
    /// children.</summary>
    internal static MenuItem Row(ItemsControl menu, string words)
        => menu.Items.OfType<MenuItem>().Single(i => Words(i) == words);

    /// <summary>
    /// Every place in a menu, its submenus included, where the rules are
    /// wrong: a rule first or last, or two rules with nothing drawn between.
    /// Avalonia collapses none, so each of these would be a line on screen.
    /// </summary>
    internal static List<string> StrayRules(ItemsControl menu, string where = "")
    {
        var faults = new List<string>();
        var seen = Seen(menu);

        for (var i = 0; i < seen.Count; i++)
        {
            if (seen[i] is not Separator) continue;

            if (i == 0) faults.Add($"{where}: a rule is the first row");
            else if (i == seen.Count - 1) faults.Add($"{where}: a rule is the last row");
            else if (seen[i - 1] is Separator) faults.Add($"{where}: two rules meet at row {i}");
        }

        foreach (var sub in seen.OfType<MenuItem>().Where(m => m.ItemsSource is null && m.Items.Count > 0))
            faults.AddRange(StrayRules(sub, $"{where} > {Words(sub)}"));

        return faults;
    }
}
