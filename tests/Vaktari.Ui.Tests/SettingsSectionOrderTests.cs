using System.Xml.Linq;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// Where the confirmations live, and where the open-ended section does.
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
///
/// **Rethought when the pages were regrouped.** The catalogue moved to
/// Appearance with the rest of the icons, so "confirmations above Icons" stopped
/// being a question the General page could ask. What the finding needs is two
/// facts that are still true whatever order anybody likes: the confirmations
/// are on the page a fresh window opens on, which holds nothing open-ended; and
/// the catalogue is the last section of whatever page it is on, so nothing
/// anywhere sits below it.
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
            .Single(t => MenuLabels.Plain((string?)t.Attribute("Header")) == page);

        // The page's own StackPanel, inside its ScrollViewer. Its direct
        // TextBlock children are the headings; everything deeper belongs to a
        // section rather than naming one.
        var panel = tab.Descendants(ns + "StackPanel").First();

        return [.. panel.Elements(ns + "TextBlock")
                        .Select(t => (string?)t.Attribute("Text") ?? "")
                        .Where(t => t.Length > 0)];
    }

    /// <summary>
    /// The page the catalogue is on: the one holding the list bound to
    /// AvailableThemes. Found, not named, so moving the catalogue again cannot
    /// leave the next test asking about a page it has left.
    /// </summary>
    private static XElement CataloguePage(XDocument markup)
    {
        var ns = markup.Root!.GetDefaultNamespace();

        return markup.Descendants(ns + "ItemsControl")
            .Single(i => (string?)i.Attribute("ItemsSource") == "{Binding AvailableThemes}")
            .Ancestors(ns + "TabItem")
            .Single();
    }

    /// <summary>
    /// The whole finding, as it stands now: the confirmations are on General,
    /// the page a fresh window opens on — and the catalogue is not.
    /// </summary>
    [Fact]
    public void Confirmations_are_not_below_the_icon_theme_catalogue()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        Assert.Contains("Ask for confirmation before", Headings("General"));

        Assert.NotEqual("General", MenuLabels.Plain((string?)CataloguePage(markup).Attribute("Header")));
    }

    /// <summary>
    /// **And nothing is below the catalogue, on any page.** The four sections
    /// that used to follow it could each be scrolled out of reach by a theme
    /// installed; its own section is now the last one on its page.
    /// </summary>
    [Fact]
    public void And_the_catalogue_is_the_last_thing_on_its_page()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var headings = Headings(MenuLabels.Plain((string?)CataloguePage(markup).Attribute("Header")));

        Assert.Equal("File icons", headings[^1]);
    }

    /// <summary>
    /// **And the version number is a way in, not a dead end.** The commands
    /// and their tests would all pass with nothing on the window binding them.
    /// </summary>
    [Fact]
    public void The_version_offers_what_is_new_and_the_project()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var bound = markup.Descendants(ns + "MenuItem")
            .Select(m => (string?)m.Attribute("Command"))
            .ToList();

        Assert.Contains("{Binding OpenReleasesCommand}", bound);
        Assert.Contains("{Binding OpenProjectCommand}", bound);
    }

    /// <summary>
    /// **And the startup folder can be browsed for.** Before this the only way
    /// to set it was to type a path correctly from memory, into a box whose
    /// placeholder suggested a Linux one.
    /// </summary>
    [Fact]
    public void The_startup_box_offers_a_browse_button()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var button = markup.Descendants(ns + "Button").SingleOrDefault(
            b => (string?)b.Attribute("Command") == "{Binding BrowseForStartupFolderCommand}");

        Assert.NotNull(button);
        Assert.Equal("Browse…", (string?)button.Attribute("Content"));
    }

    /// <summary>
    /// **And the way to empty the recent lists.** They were recorded with no
    /// setting consulted and no route out but a per-row Forget.
    /// </summary>
    [Fact]
    public void The_behaviour_section_offers_a_way_to_forget_what_was_opened()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var button = markup.Descendants(ns + "Button").SingleOrDefault(
            b => (string?)b.Attribute("Command") == "{Binding ForgetRecentCommand}");

        Assert.NotNull(button);
        Assert.Equal("Forget what was opened", (string?)button.Attribute("Content"));
    }

    /// <summary>
    /// **And the way to empty the folder-view store is too.** A command nothing
    /// binds is a feature nobody can reach, and this store is otherwise
    /// invisible: it fills up by itself and its file is not named anywhere in
    /// the application.
    /// </summary>
    [Fact]
    public void The_behaviour_section_offers_a_way_to_forget_the_views()
    {
        var markup = XDocument.Parse(RepoSource.Ui("SettingsWindow.axaml"));

        var ns = markup.Root!.GetDefaultNamespace();

        var button = markup.Descendants(ns + "Button").SingleOrDefault(
            b => (string?)b.Attribute("Command") == "{Binding ForgetRememberedViewsCommand}");

        Assert.NotNull(button);
        Assert.Equal("Forget remembered views", (string?)button.Attribute("Content"));
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
