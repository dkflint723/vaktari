using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// What the "…" crumb does when you press it.
///
/// **It opened the path editor.** The bar drops ancestors from the middle when
/// the toolbar is too narrow for them and puts a "…" where they were — so the
/// one question that mark raises is "which folders are under there". Pressing
/// it replaced the whole bar with a text box holding the path as a string: the
/// crumbs you were reading disappeared, the folders it stood for were still
/// named nowhere, and getting back cost an Escape.
///
/// The list was never missing. <see cref="BreadcrumbPanel"/> works out which
/// crumbs come off on every arrange and parks them off-screen, so the elided
/// ancestors were known, computed afresh at every window width, and thrown
/// away. They go into <see cref="PathSegment.Hidden"/> now, and the crumb
/// template hangs a menu of them off the mark.
///
/// Not the chevron menu beside it, which is a different question with a
/// different cost: that one lists what is INSIDE a crumb and reads a disk to do
/// it. These are ancestors, already in the bar, and free.
///
/// Four separate things have to hold, and each is pinned differently — said
/// plainly here because one of the four is a GUARD that cannot go red for any
/// mistake in Vaktari, and reading that guard as coverage is how the fourth
/// went unwritten to begin with:
///
/// <list type="bullet">
/// <item>the panel publishes exactly what it dropped — hand-built panels
///   arranged at chosen widths, which is the only way to fix the arithmetic;</item>
/// <item>the shipped crumb template offers that list rather than the editor —
///   read as XML out of MainWindow.axaml, so it is that file under test;</item>
/// <item>a binding written inside an item template reaches the ITEM from inside
///   a flyout, which is its own popup root — a GUARD over reduced markup this
///   file owns, measuring Avalonia rather than Vaktari;</item>
/// <item>and the rows and the mark actually DRAW — a real MainWindow over a real
///   folder, arranged narrow enough to elide, with the flyout really open. The
///   XML reads above cannot see a blanked TextBlock inside the HeaderTemplate,
///   and neither can the guard, whose reduced markup has no HeaderTemplate at
///   all: measured, blanking the shipped one left every row of the real menu
///   drawing nothing, and of the 41 tests in this file and
///   <see cref="CrumbMenuTests"/> the only one that goes red is the
///   real-window one below.</item>
/// </list>
/// </summary>
public sealed class EllipsisCrumbTests : OwnedViewModels
{
    // ---- what the panel publishes --------------------------------------

    /// <summary>
    /// A crumb with no command, the way <see cref="BreadcrumbPanelTests"/>
    /// builds one: nothing here presses a crumb, and a real command would want
    /// a pane and a disk behind it.
    /// </summary>
    private static PathSegment Ancestor(string name, string path)
        => new(name, path, null!, IsLast: false);

    /// <summary>
    /// Widths are fixed on the children rather than measured from text, again
    /// the way the panel's own tests do it: the arithmetic that decides how
    /// many crumbs fit is then exact, and a font change cannot move the
    /// boundary this asserts on.
    /// </summary>
    private static Control Crumb(PathSegment segment, double width) => new Border
    {
        Width = width,
        Height = 20,
        DataContext = segment,
    };

    /// <summary>
    /// The five crumbs of <c>C:\ › … › users › flint › vaktari</c>, and the
    /// panel that lays them out. Returned together so a test can arrange the
    /// SAME panel at two widths, which is what a window resize is.
    /// </summary>
    private sealed record Bar(
        BreadcrumbPanel Panel, PathSegment Ellipsis, PathSegment Users, PathSegment Flint)
    {
        public void At(double width)
        {
            Panel.Measure(new Size(width, 20));
            Panel.Arrange(new Rect(0, 0, width, 20));
        }
    }

    private static Bar Deep()
    {
        var root = Ancestor("C:\\", "C:\\");
        var ellipsis = PathSegment.Ellipsis();
        var users = Ancestor("users", "C:\\users");
        var flint = Ancestor("flint", "C:\\users\\flint");
        var here = new PathSegment("vaktari", "C:\\users\\flint\\vaktari", null!, IsLast: true);

        var panel = new BreadcrumbPanel();

        panel.Children.Add(Crumb(root, 40));
        panel.Children.Add(Crumb(ellipsis, 20));
        panel.Children.Add(Crumb(users, 100));
        panel.Children.Add(Crumb(flint, 100));
        panel.Children.Add(Crumb(here, 60));

        return new Bar(panel, ellipsis, users, flint);
    }

    /// <summary>
    /// The finding itself. At 160px the root (40) and the mark (20) leave 100
    /// for the tail, which takes "vaktari" and neither of the two above it — so
    /// the menu must name both of them, nearest the root first.
    /// </summary>
    [AvaloniaFact]
    public void The_ellipsis_offers_the_ancestors_the_bar_had_no_room_for()
    {
        var bar = Deep();

        bar.At(160);

        Assert.Equal(
            new[] { "C:\\users", "C:\\users\\flint" },
            bar.Ellipsis.Hidden.Select(c => c.FullPath));
    }

    /// <summary>
    /// The rows ARE the crumbs the bar dropped, not copies of them — so each
    /// one carries the navigating command the bar built for that ancestor, and
    /// there is no second answer to keep in step with the first.
    /// </summary>
    [AvaloniaFact]
    public void The_rows_are_the_crumbs_themselves()
    {
        var bar = Deep();

        bar.At(160);

        Assert.Same(bar.Users, bar.Ellipsis.Hidden[0]);
        Assert.Same(bar.Flint, bar.Ellipsis.Hidden[1]);
    }

    /// <summary>
    /// **The answer changes without a navigation.** Which ancestors are missing
    /// is a function of the toolbar's width, so dragging the window or the
    /// split changes it while the path on screen stays the same — and a menu
    /// still offering the crumb now sitting visibly beside it is the stale half
    /// of the previous answer.
    /// </summary>
    [AvaloniaFact]
    public void A_wider_bar_takes_the_crumbs_it_reveals_off_the_menu()
    {
        var bar = Deep();

        bar.At(160);
        Assert.Equal(2, bar.Ellipsis.Hidden.Count);

        bar.At(400);

        Assert.Empty(bar.Ellipsis.Hidden);
    }

    /// <summary>
    /// And the middle case, which is the one a coarse "clear it when everything
    /// fits" would get wrong: at 280px there is room for "flint" but not for
    /// "users", so exactly one crumb is still missing and exactly one is
    /// offered.
    /// </summary>
    [AvaloniaFact]
    public void Only_the_crumbs_actually_missing_are_offered()
    {
        var bar = Deep();

        bar.At(160);
        bar.At(280);

        Assert.Equal(
            new[] { "C:\\users" },
            bar.Ellipsis.Hidden.Select(c => c.FullPath));
    }

    /// <summary>
    /// **This is written from arrange, which runs on every layout pass.** A
    /// pointer-driven splitter drag re-arranges the bar at every pixel it
    /// passes through, and the set of missing crumbs changes at a handful of
    /// those at most — so an ObservableCollection that reported a reset each
    /// time would tear down and rebuild an open menu under the hand of the
    /// person reading it. Re-stating the same set has to be silent.
    ///
    /// A pixel at a time rather than the same width twice, because arranging a
    /// control into the rectangle it already occupies does not reach
    /// ArrangeOverride at all — the panel would then be silent for a reason
    /// that has nothing to do with the guard this is about.
    /// </summary>
    [AvaloniaFact]
    public void A_drag_that_reveals_nothing_leaves_the_menu_alone()
    {
        var bar = Deep();

        bar.At(280);

        var changes = 0;

        bar.Ellipsis.Hidden.CollectionChanged += (_, _) => changes++;

        bar.At(281);
        bar.At(282);

        Assert.Equal(0, changes);
        Assert.Equal(
            new[] { "C:\\users" },
            bar.Ellipsis.Hidden.Select(c => c.FullPath));
    }

    // ---- what the crumb carries ----------------------------------------

    /// <summary>
    /// **The mark is not a place, so its face goes nowhere.** It used to carry
    /// a command, and that command was BeginEditPath — the whole fault, one
    /// argument wide.
    ///
    /// Refused rather than left inert, and the refusal is defensive: measured
    /// on the mark's crumb in a real MainWindow, three buttons are realized and
    /// exactly one is effectively visible — the one carrying the menu, whose
    /// Command is null. Nothing on screen fires this. What the refusal buys is
    /// that the next control to bind it cannot navigate with it either.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ellipsis_no_longer_carries_the_path_editor()
    {
        var folder = Path.Combine(
            Path.GetTempPath(), "vaktari-crumbs-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            var pane = Own(new PaneViewModel(new Silent()));

            await pane.NavigateAsync(folder);

            var ellipsis = Assert.Single(pane.Breadcrumbs, c => c.IsEllipsis);

            Assert.False(ellipsis.Open.CanExecute(null),
                         "the mark must not offer to take you anywhere");

            ellipsis.Open.Execute(null);

            Assert.False(pane.IsPathEditing,
                         "pressing the mark must not swap the bar for the address box");
        }
        finally
        {
            Directory.Delete(folder);
        }
    }

    // ---- what the markup offers ----------------------------------------

    private static readonly XNamespace Av = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The one item template the crumb strip draws.</summary>
    private static XElement CrumbTemplate()
        => XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
                    .Descendants(Av + "DataTemplate")
                    .Single(t => (string?)t.Attribute(X + "DataType") == "vm:PathSegment");

    private static XElement Button(XElement template, string isVisible)
        => template.Descendants(Av + "Button")
                   .Single(b => (string?)b.Attribute("IsVisible") == isVisible);

    private static XElement Menu(XElement button)
        => Assert.Single(button.Descendants(Av + "MenuFlyout"));

    /// <summary>
    /// The half of the fix that lives in the markup: the crumb the panel marks
    /// as the ellipsis is the one carrying the menu, and it is bound to the
    /// list the panel fills.
    ///
    /// Named for the screen reader as well, because this is the one crumb whose
    /// label is not a folder name: every other button here is announced as
    /// "{Binding Name}", and this one's Name is "…". For the pointer too, the
    /// same pair the chevron beside it carries — "…" is three dots, and a
    /// hover that explains nothing leaves the mark exactly as mute as it was.
    /// </summary>
    [Fact]
    public void The_ellipsis_crumb_opens_a_menu_of_what_the_bar_dropped()
    {
        var mark = Button(CrumbTemplate(), "{Binding IsEllipsis}");

        Assert.Equal("{Binding Hidden}", (string?)Menu(mark).Attribute("ItemsSource"));

        Assert.Equal("Folders in between",
                     (string?)mark.Attribute("AutomationProperties.Name"));

        Assert.Equal("Folders in between", (string?)mark.Attribute("ToolTip.Tip"));
    }

    /// <summary>
    /// Every row in the menu names an ancestor and goes there. The rows carry
    /// their OWN command, the way the history and chevron menus do: a flyout is
    /// its own popup root, so a reach-out through $parent would resolve against
    /// the shell rather than this pane.
    ///
    /// And the name is DRAWN rather than parsed, the rule every folder name in
    /// this window carries: a bare Header goes through the MenuItem template's
    /// ContentPresenter, which has RecognizesAccessKey="True", so an ancestor
    /// named "git_projects" would draw as "gitprojects" with an access key on
    /// "p".
    /// </summary>
    [Fact]
    public void Each_row_in_the_menu_names_and_opens_its_ancestor()
    {
        var menu = Menu(Button(CrumbTemplate(), "{Binding IsEllipsis}"));

        var setters = menu.Descendants(Av + "Setter")
                          .ToDictionary(s => (string)s.Attribute("Property")!,
                                        s => (string?)s.Attribute("Value"));

        Assert.Equal("{Binding Name}", setters["Header"]);
        Assert.Equal("{Binding Open}", setters["Command"]);

        // The leaf alone is ambiguous in exactly the case this menu exists for:
        // the rows are a RUN of ancestors, so "src" over "src" is what a deep
        // repository path offers, and only the full path tells them apart.
        Assert.Equal("{Binding FullPath}", setters["ToolTip.Tip"]);

        Assert.True(setters.ContainsKey("HeaderTemplate"),
                    "an ancestor with an underscore in its name would be drawn through AccessText");
    }

    /// <summary>
    /// And the ordinary crumb is untouched: one click still navigates, and it
    /// grows no menu of its own — every other crumb's Hidden is empty, so a
    /// menu on its face would open onto nothing. The chevron beside it is a
    /// different button and keeps its own.
    /// </summary>
    [Fact]
    public void An_ordinary_crumb_still_navigates_and_offers_no_menu()
    {
        var crumb = Button(CrumbTemplate(), "{Binding !IsEllipsis}");

        Assert.Equal("{Binding Open}", (string?)crumb.Attribute("Command"));
        Assert.Empty(crumb.Descendants(Av + "MenuFlyout"));
    }

    // ---- that the binding reaches the flyout ---------------------------

    /// <summary>
    /// The shape of the crumb template, reduced to the two buttons under test.
    /// The structure is the subject, not the file: a MenuFlyout declared inside
    /// an ItemTemplate has to find the ITEM's data, and a flyout is not in the
    /// visual tree the item sits in.
    /// </summary>
    private const string Markup = """
        <Window xmlns="https://github.com/avaloniaui"
                xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                xmlns:vm="clr-namespace:Vaktari.Ui.ViewModels;assembly=Vaktari.Ui">
          <ItemsControl x:Name="Crumbs">
            <ItemsControl.ItemTemplate>
              <DataTemplate x:DataType="vm:PathSegment">
                <StackPanel Orientation="Horizontal">
                  <Button x:Name="Place" IsVisible="{Binding !IsEllipsis}" Command="{Binding Open}">
                    <TextBlock Text="{Binding Name}"/>
                  </Button>
                  <Button x:Name="Mark" IsVisible="{Binding IsEllipsis}">
                    <Button.Flyout>
                      <MenuFlyout ItemsSource="{Binding Hidden}">
                        <MenuFlyout.ItemContainerTheme>
                          <ControlTheme TargetType="MenuItem" BasedOn="{StaticResource {x:Type MenuItem}}"
                                        x:DataType="vm:PathSegment">
                            <Setter Property="Header" Value="{Binding Name}"/>
                            <Setter Property="Command" Value="{Binding Open}"/>
                          </ControlTheme>
                        </MenuFlyout.ItemContainerTheme>
                      </MenuFlyout>
                    </Button.Flyout>
                    <TextBlock Text="{Binding Name}"/>
                  </Button>
                </StackPanel>
              </DataTemplate>
            </ItemsControl.ItemTemplate>
          </ItemsControl>
        </Window>
        """;

    /// <summary>Lays the reduced template out over the crumbs given, and hands
    /// back the window so a test can read what it drew.</summary>
    private static Window Drawn(params PathSegment[] items)
    {
        var window = (Window)AvaloniaRuntimeXamlLoader.Load(Markup);
        var crumbs = window.FindControl<ItemsControl>("Crumbs")!;

        crumbs.ItemsSource = items;

        window.Show();
        window.Measure(new Size(600, 200));
        window.Arrange(new Rect(0, 0, 600, 200));

        return window;
    }

    private static Button Drew(Window window, string name, PathSegment of)
        => window.GetVisualDescendants()
                 .OfType<Button>()
                 .Single(b => b.Name == name && ReferenceEquals(b.DataContext, of));

    /// <summary>
    /// **A flyout that cannot see the crumb's data shows an empty menu**, which
    /// is indistinguishable from "nothing was dropped" — the failure this fix
    /// is most likely to ship with, and one no build would report. A flyout is
    /// its own popup root and is not in the visual tree the item sits in, so
    /// that a binding written inside an ItemTemplate reaches the ITEM from
    /// there is a fact about Avalonia rather than about this application.
    ///
    /// The Assert.Same half is a GUARD and cannot go red for a mistake in
    /// Vaktari: it asserts the form, the way CutFadeBindingTests does, out of
    /// markup this test owns. The contents half is not a guard — it reads the
    /// list through the flyout, so it goes red the moment the panel stops
    /// publishing what it dropped.
    /// </summary>
    [AvaloniaFact]
    public void The_menu_reads_the_list_off_the_crumb_it_hangs_from()
    {
        var bar = Deep();

        bar.At(160);

        var window = Drawn(bar.Ellipsis);

        var menu = Assert.IsType<MenuFlyout>(Drew(window, "Mark", bar.Ellipsis).Flyout);

        Assert.Same(bar.Ellipsis.Hidden, menu.ItemsSource);

        Assert.Equal(
            new[] { "C:\\users", "C:\\users\\flint" },
            menu.ItemsSource!.Cast<PathSegment>().Select(c => c.FullPath));

        window.Close();
    }

    /// <summary>
    /// A GUARD, and it cannot go red for a mistake in Vaktari: it measures
    /// Avalonia, out of markup this test owns.
    ///
    /// **The claim it measures is what makes two buttons safe.** The crumb
    /// template now holds both halves and shows one, and the panel's whole job
    /// is arithmetic on crumb widths — so if a control with IsVisible false
    /// were measured, every ordinary crumb would carry the menu button's width
    /// as well as its own, and the bar would start eliding a path that fits.
    /// Measured here: the unused half desires nothing, and the crumb is exactly
    /// as wide as the half it shows.
    /// </summary>
    [AvaloniaFact]
    public void A_crumb_is_no_wider_for_the_half_of_the_template_it_does_not_use()
    {
        var place = Ancestor("flint", "C:\\users\\flint");

        var window = Drawn(place);

        var shown = Drew(window, "Place", place);
        var unused = Drew(window, "Mark", place);

        Assert.False(unused.IsVisible);
        Assert.Equal(0, unused.DesiredSize.Width);

        var crumb = Assert.IsType<StackPanel>(shown.Parent);

        Assert.True(shown.DesiredSize.Width > 0, "the crumb it does show has a width");
        Assert.Equal(shown.DesiredSize.Width, crumb.DesiredSize.Width);

        window.Close();
    }

    // ---- the real window -----------------------------------------------

    /// <summary>
    /// A real MainWindow showing <c>&lt;temp&gt;/vaktari-ellipsis-xxxx/git_projects/</c>
    /// plus a folder with a name long enough to fill the bar on its own, and
    /// the mark the bar built for it.
    ///
    /// The long leaf is deliberate: the panel keeps the folder you are in
    /// whatever it costs, so a leaf wider than the whole budget forces the tail
    /// to exactly one crumb and every ancestor above it into the menu. That is
    /// what makes "git_projects" — the name whose underscore an AccessText would
    /// eat — reliably one of the rows on any machine, however shallow its temp
    /// directory happens to be.
    /// </summary>
    private async Task<(MainWindow Window, PathSegment Mark)> RealBar()
    {
        UseSearch(PaneViewModel.Search);

        var places = PaneViewModel.Places;

        _restore.Add(() => PaneViewModel.Places = places);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-ellipsis-" + Guid.NewGuid().ToString("N")[..8]);

        var here = Path.Combine(root, "git_projects", "a-folder-with-a-really-long-name-here");

        Directory.CreateDirectory(here);

        var window = new MainWindow();

        window.Show();
        Dispatcher.UIThread.RunJobs();

        _restore.Add(() =>
        {
            window.Close();

            try { Directory.Delete(root, true); }
            catch (IOException ex) { Vaktari.Core.Quiet.Swallowed("test-teardown", ex); }
        });

        var shell = Assert.IsType<ShellViewModel>(window.DataContext);

        await shell.ActiveTab!.NavigateAsync(here);

        return (window, Assert.Single(shell.ActiveTab!.Breadcrumbs, c => c.IsEllipsis));
    }

    /// <summary>Runs the queue, then measures and arranges at the width given —
    /// which is what decides how much of the path is dropped, and a realized
    /// flyout has to be laid out before anything in it has a size.</summary>
    private static async Task Arrange(Window window, double width)
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }

        window.Measure(new Size(width, 900));
        window.Arrange(new Rect(0, 0, width, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// The one button carrying the ellipsis's menu.
    ///
    /// **The mark's button is in EVERY crumb**, with IsVisible false on all but
    /// one — so a search for "a button whose flyout lists a Hidden" found eleven
    /// on this path. Both halves are needed: the crumb has to be the mark, and
    /// the flyout has to be listing that mark's own list.
    /// </summary>
    private static Button MarkButton(Window window)
        => Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.DataContext is PathSegment { IsEllipsis: true } crumb
                 && b.Flyout is MenuFlyout menu
                 && ReferenceEquals(menu.ItemsSource, crumb.Hidden));

    /// <summary>Opens the mark's menu for real and hands back the MenuItems it
    /// realized.</summary>
    private static async Task<List<MenuItem>> Rows(Window window, Button mark, double width)
    {
        var flyout = (MenuFlyout)mark.Flyout!;

        flyout.ShowAt(mark);

        await Arrange(window, width);

        return [.. ((Visual)flyout.Popup.Child!).GetVisualDescendants().OfType<MenuItem>()];
    }

    /// <summary>
    /// Where eight Tab presses from the first crumb land. Real key presses
    /// rather than Focus() calls, because what is under test is whether the
    /// keyboard can arrive at the mark at all.
    /// </summary>
    private static List<object?> TabRing(Window window)
    {
        var first = window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => b.DataContext is PathSegment crumb
                        && AutomationProperties.GetName(b) == crumb.Name);

        Assert.True(first.Focus(), "the walk never started");

        var stops = new List<object?>();

        for (var i = 0; i < 8; i++)
        {
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, "\t");

            Dispatcher.UIThread.RunJobs();

            stops.Add(window.FocusManager?.GetFocusedElement());
        }

        return stops;
    }

    /// <summary>
    /// **New markup that only runs once somebody presses the mark is exactly
    /// the kind that ships broken**, the lesson
    /// <see cref="CrumbMenuTests.The_real_menu_shows_the_folder_names_as_they_are_written"/>
    /// was written for next door. Every other test in this file reads the view
    /// model's list or markup this file owns; a Header binding that resolves to
    /// nothing is a logged warning in Avalonia rather than an exception.
    ///
    /// Measured before this existed: blanking the shipped HeaderTemplate's
    /// TextBlock left every row of the real menu drawing nothing, and of the 41
    /// tests in this file and CrumbMenuTests not one else went red — this one
    /// is the whole of the coverage.
    ///
    /// The mark's own glyph is here for the same reason and measured the same
    /// way — pointing its TextBlock at FullPath, which the mark carries as "",
    /// drew a blank mark past all 40 of the others. It is not only cosmetic: that
    /// glyph's width is the `gap` BreadcrumbPanel reserves for the mark, so a
    /// blank one silently moves the boundary at which the bar starts eliding.
    /// </summary>
    [AvaloniaFact]
    public async Task The_real_menu_names_the_ancestors_the_bar_dropped()
    {
        var (window, mark) = await RealBar();

        await Arrange(window, 480);

        var button = MarkButton(window);

        var glyph = Assert.Single(button.GetVisualDescendants().OfType<TextBlock>());

        Assert.Equal("…", glyph.Text);
        Assert.True(glyph.DesiredSize.Width > 0,
                    "the mark the elision arithmetic measures draws nothing");

        // The arrangement this test is about: the leaf alone is wider than the
        // budget, so everything above it is in the menu — including the one
        // ancestor whose name an AccessText would take apart.
        Assert.Equal("git_projects", mark.Hidden[^1].Name);

        var rows = await Rows(window, button, 480);

        Assert.Equal(mark.Hidden.Select(c => c.Name), rows.Select(r => r.Header as string));
        Assert.Equal(mark.Hidden.Select(c => c.FullPath),
                     rows.Select(r => ToolTip.GetTip(r) as string));
        Assert.Equal(mark.Hidden.Select(c => c.Open), rows.Select(r => r.Command));

        // And DRAWN, not parsed: an AccessText here is the underscore eaten and
        // the "p" underlined.
        var underscored = rows[^1];

        Assert.Empty(underscored.GetVisualDescendants().OfType<AccessText>());

        var drawn = Assert.Single(
            underscored.GetVisualDescendants().OfType<TextBlock>(), t => t.Text is "git_projects");

        var asWritten = new TextBlock { Text = "git_projects", FontSize = drawn.FontSize };

        asWritten.Measure(new Size(400, 40));

        Assert.Equal(asWritten.DesiredSize.Width, drawn.DesiredSize.Width);
    }

    /// <summary>
    /// **The mark is in the bar even when nothing is elided.** The view model
    /// puts it there for any path deeper than two levels and the panel parks it
    /// off-screen when the whole path fits — so the button carrying the menu was
    /// still enabled and still a tab stop with an empty list behind it, and four
    /// Tab presses from the first crumb reached something announced "Folders in
    /// between" whose flyout laid out 2 by 32 with no rows in it. That popup is
    /// the shape <see cref="CrumbMenuTests.A_menu_with_no_rows_yet_opens_as_a_sliver"/>
    /// measured and <see cref="CrumbMenuTests.A_crumb_menu_never_opens_empty"/>
    /// forbids for the chevron beside it.
    ///
    /// The narrow half is the CONTROL, and it is what makes the wide half mean
    /// anything: the same eight presses from the same crumb do reach the mark
    /// when it has ancestors to offer, so the wide walk is going past the place
    /// it sits rather than stopping short of it.
    /// </summary>
    [AvaloniaFact]
    public async Task The_mark_that_stands_for_nothing_takes_neither_focus_nor_a_tab()
    {
        var (window, mark) = await RealBar();

        await Arrange(window, 480);

        Assert.NotEmpty(mark.Hidden);
        Assert.Contains(MarkButton(window), TabRing(window));

        await Arrange(window, 3200);

        Assert.Empty(mark.Hidden);

        Assert.DoesNotContain(MarkButton(window), TabRing(window));

        Assert.False(MarkButton(window).Focus(),
                     "a menu that stands for nothing still took the focus");
    }

    /// <summary>
    /// **A Button with both a Command and a Flyout does both**, so the crumb
    /// that offers the menu must not also be wired to a command — that is how
    /// the editor would come back alongside the menu rather than instead of it.
    ///
    /// On the REALIZED button, not on the markup's attribute list, which is
    /// where this used to look. A Command written as a property element —
    /// <c>&lt;Button.Command&gt;&lt;Binding Path="Open"/&gt;&lt;/Button.Command&gt;</c>,
    /// one line of valid XAML — leaves no Command ATTRIBUTE to find: measured,
    /// it put a RelayCommand on the mark and the attribute read went on
    /// passing.
    /// </summary>
    [AvaloniaFact]
    public async Task The_crumb_carrying_the_menu_fires_no_command_of_its_own()
    {
        var (window, _) = await RealBar();

        await Arrange(window, 480);

        Assert.Null(MarkButton(window).Command);
    }

    /// <summary>
    /// **The menu is written from ARRANGE, so the bar can empty it under the
    /// hand of somebody reading it.** Which ancestors are missing is a function
    /// of the toolbar's width, and widening past the point where the whole path
    /// fits takes every row out of a menu that is already on screen. Measured
    /// on a real temp path: the flyout shown at 480px listed eight ancestors,
    /// and arranging the same window at 3200 left it open with
    /// <c>rows=0 bounds=0, 0, 2, 32</c> — the sliver
    /// <see cref="CrumbMenuTests.A_menu_with_no_rows_yet_opens_as_a_sliver"/>
    /// records and <see cref="CrumbMenuTests.A_crumb_menu_never_opens_empty"/>
    /// forbids for the chevron beside it. Disabling the button does not take
    /// down a popup that is already up: that same measurement read
    /// <c>open=True enabled=False</c> together.
    ///
    /// The narrow half is the control. It proves the menu really opens over
    /// real rows first, so a close that came from its never having opened
    /// would not pass.
    /// </summary>
    [AvaloniaFact]
    public async Task Widening_the_bar_past_the_menu_closes_it_rather_than_emptying_it()
    {
        var (window, mark) = await RealBar();

        await Arrange(window, 480);

        var button = MarkButton(window);
        var flyout = (MenuFlyout)button.Flyout!;

        Assert.NotEmpty(await Rows(window, button, 480));
        Assert.True(flyout.IsOpen, "the menu never opened, so closing it would prove nothing");

        await Arrange(window, 3200);

        Assert.Empty(mark.Hidden);

        Assert.False(flyout.IsOpen,
                     "the emptied menu was left open, showing no rows at all");
    }

    private readonly List<Action> _restore = [];

    public override void Dispose()
    {
        foreach (var undo in _restore) undo();

        _restore.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A file system that answers nothing, so a pane can be built
    /// without touching a disk.</summary>
    private sealed class Silent : IFileSystemProvider
    {
        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield return [];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Idle();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => Path.GetDirectoryName(path);
        public bool IsCaseSensitive => false;

        private sealed class Idle : IDisposable { public void Dispose() { } }
    }
}
