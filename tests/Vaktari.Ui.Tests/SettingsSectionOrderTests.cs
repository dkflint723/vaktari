using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the confirmations live on the General page.
///
/// **They were ninth of nine, under the icon-theme catalogue.** Those three
/// check boxes are the only settings on that page that decide whether a
/// keystroke can lose a file, and they sat below a section a hundred and fifty
/// lines long that grows with every theme installed — so on a 560px window
/// somebody looking for "stop asking me before the bin" scrolled past sorting,
/// splits, default-file-manager, a theme catalogue, terminals, Proton Drive,
/// the status bar and preview limits to reach them.
///
/// **This pins the ONE thing about the order that is not a taste call.** Which
/// of nine sections comes third rather than fourth is a judgement nobody should
/// be asked to defend in a test. That the settings which guard against losing a
/// file are not underneath the longest and most open-ended section on the page
/// is not a judgement — the catalogue's length is decided by what the person
/// has installed, so anything below it has no knowable position at all.
/// </summary>
public class SettingsSectionOrderTests
{
    /// <summary>
    /// The section headings of one page, in document order.
    ///
    /// Read as XML rather than scanned line by line, the same way LabelCasing
    /// and MarkupRules read this markup: a heading is a direct child TextBlock
    /// of the page's own StackPanel, and the check boxes inside each section
    /// carry TextBlocks too.
    /// </summary>
    private static List<string> Headings(string page)
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var tab = markup.Descendants(ns + "TabItem")
            .Single(t => (string?)t.Attribute("Header") == page);

        // The page's own StackPanel, inside its ScrollViewer. Its direct
        // TextBlock children are the headings; everything deeper belongs to a
        // section rather than naming one.
        var panel = tab.Descendants(ns + "StackPanel").First();

        return [.. panel.Elements(ns + "TextBlock")
                        .Select(t => (string?)t.Attribute("Text") ?? "")
                        .Where(t => t.Length > 0)];
    }

    /// <summary>The whole finding.</summary>
    [Fact]
    public void Confirmations_are_not_below_the_icon_theme_catalogue()
    {
        var headings = Headings("General");

        var confirmations = headings.IndexOf("Ask for confirmation before");
        var icons = headings.IndexOf("Icons");

        Assert.True(confirmations >= 0, "the confirmations heading is not a section of the General page");
        Assert.True(icons >= 0, "the icons heading is not a section of the General page");

        Assert.True(
            confirmations < icons,
            $"confirmations sit at {confirmations} of {headings.Count}, below Icons at {icons}: "
            + string.Join(" | ", headings));
    }

    /// <summary>
    /// And not last, which is the other half of how they were unreachable —
    /// the four sections that used to follow Icons could each grow without
    /// anything moving the confirmations back up.
    /// </summary>
    [Fact]
    public void And_are_not_the_last_thing_on_the_page()
    {
        var headings = Headings("General");

        Assert.NotEqual(headings.Count - 1, headings.IndexOf("Ask for confirmation before"));
    }

    /// <summary>
    /// **The way back to the defaults is on the window**, not only on the view
    /// model. A command nothing binds is a feature nobody can reach, and this
    /// dialog has six pages of controls with no other route to one.
    ///
    /// Read out of the markup because that is where the gap was: the command
    /// and its tests would all pass with no button anywhere.
    /// </summary>
    [Fact]
    public void The_footer_offers_a_way_back_to_the_defaults()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var button = markup.Descendants(ns + "Button").SingleOrDefault(
            b => (string?)b.Attribute("Command") == "{Binding RestoreDefaultsCommand}");

        Assert.NotNull(button);
        Assert.Equal("Restore defaults", (string?)button.Attribute("Content"));
    }
}
