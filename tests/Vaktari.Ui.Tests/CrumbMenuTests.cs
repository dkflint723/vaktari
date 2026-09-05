using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Vaktari.Core.FileSystem;
using Vaktari.Core.Places;
using Vaktari.Ui;
using Vaktari.Ui.ViewModels;
using Xunit;

namespace Vaktari.Ui.Tests;

/// <summary>
/// The menu behind a crumb — what is inside that ancestor.
///
/// **The path bar only went UP.** Every crumb was one command, navigate to this
/// ancestor, and the separator beside it was a TextBlock; nothing anywhere in
/// the application enumerated a crumb. So the commonest move in a file manager
/// — from a folder to the one beside it — cost a click on the parent, a full
/// listing of it, and a hunt down the rows for a name the bar was already
/// showing. Explorer hangs that folder's subfolders off the chevron after each
/// crumb; Dolphin off the same spot.
/// </summary>
public sealed class CrumbMenuTests : OwnedViewModels
{
    private static readonly XNamespace Xaml = "https://github.com/avaloniaui";

    private static string Root => Path.Combine(Path.GetTempPath(), "vaktari-crumb-menu");

    private static string In(params string[] parts) => Path.Combine([Root, .. parts]);

    private readonly List<Action> _restore = [];

    public override void Dispose()
    {
        foreach (var undo in _restore) undo();

        _restore.Clear();
        base.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>Pumps the dispatcher and gives a continuation that answered off
    /// the pane's own await real time to arrive — the menu fill is started by a
    /// command and finished by a continuation, the way the button drives
    /// it.</summary>
    private static async Task Drain()
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }
    }

    /// <summary>
    ///   vaktari-crumb-menu/  docs/    inner/   deep/
    ///                                 note.txt
    ///                        pics/
    ///                        top.txt
    /// </summary>
    private static Tree Sample()
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("docs"), Tree.Dir("pics"), Tree.File("top.txt"));
        tree.Put(In("docs"), Tree.Dir("inner"), Tree.File("note.txt"));
        tree.Put(In("docs", "inner"), Tree.Dir("deep"));

        return tree;
    }

    private async Task<PaneViewModel> Pane(Tree? tree = null, string? at = null)
    {
        var pane = Own(new PaneViewModel(tree ?? Sample(), null, null) { ViewportWidth = 1400 });

        await pane.NavigateAsync(at ?? In("docs", "inner"));
        await Drain();

        return pane;
    }

    private static PathSegment CrumbFor(PaneViewModel pane, string path)
        => Assert.Single(pane.Breadcrumbs, c => c.FullPath == path);

    /// <summary>Presses the crumb's separator, the way the button does: run the
    /// command it is bound to, then let the read land.</summary>
    private static async Task Press(PathSegment crumb)
    {
        Assert.True(crumb.HasMenu, "this crumb carries no menu command at all");

        crumb.Menu!.Execute(null);

        await Drain();
    }

    private static List<string> Rows(PathSegment crumb)
        => crumb.Children.Select(c => c.Name).ToList();

    // ---- what the menu holds -----------------------------------------------

    /// <summary>
    /// The whole finding: an ancestor's own subfolders, from the bar, without
    /// going there.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_lists_the_folders_inside_it()
    {
        var pane = await Pane();

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(["docs", "pics"], Rows(crumb));
    }

    /// <summary>
    /// **Folders only.** The menu is how you go somewhere from the bar; a file
    /// in it would either do nothing or launch something, and a crumb menu that
    /// can launch a program is not what pressing a separator asks for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_menu_holds_folders_only()
    {
        var pane = await Pane();

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.DoesNotContain("top.txt", Rows(crumb));
    }

    /// <summary>
    /// The other half of the gesture, and the half a menu that only DRAWS the
    /// folders would fail silently: choosing one has to go there.
    /// </summary>
    [AvaloniaFact]
    public async Task Choosing_a_folder_from_a_crumb_menu_goes_there()
    {
        var pane = await Pane();

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        var pics = Assert.Single(crumb.Children, c => c.Name == "pics");

        pics.Open.Execute(null);

        await Drain();

        Assert.Equal(In("pics"), pane.CurrentPath);
    }

    /// <summary>
    /// **A directory read comes back in whatever order the filesystem kept it
    /// in**, which on ext4 is a hash order — so an unsorted menu would list a
    /// folder's children in an order that changes when a file is created and
    /// means nothing to anyone. The pane's own within-folder comparer, so the
    /// menu agrees with the listing it is standing in for.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_menu_is_in_the_order_the_listing_would_use()
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("zeta"), Tree.Dir("alpha"), Tree.Dir("mid"));
        tree.Put(In("zeta"));

        var pane = await Pane(tree, In("zeta"));

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(["alpha", "mid", "zeta"], Rows(crumb));
    }

    /// <summary>
    /// **The menu must conceal what the listing conceals.** It is the same
    /// question asked from a different control, and a bar that offers AppData
    /// and .git while the folder underneath is hiding them contradicts the
    /// window it is part of.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_crumb_menu_conceals_what_the_listing_conceals(bool shown)
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("open"), Tree.Hidden("secret"));
        tree.Put(In("open"));

        var pane = await Pane(tree, In("open"));

        pane.ShowHidden = shown;

        await Drain();

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(shown, Rows(crumb).Contains("secret"));
    }

    /// <summary>
    /// **A row per subfolder of WinSxS is not a menu.** It is a listing drawn in
    /// a popup with none of the listing's columns, sorting or selection, and it
    /// is built one MenuItem at a time.
    ///
    /// Capped AFTER the sort, which is the part worth pinning: capping the read
    /// would hand back whichever hundred the filesystem returned first, so the
    /// menu would be missing names from the middle of the alphabet with nothing
    /// to say it had.
    ///
    /// **And it must say that it stopped.** A menu that ended at a hundred read
    /// exactly like a folder that held a hundred — the same fault the search
    /// band already carries a line for, and worse here, because these rows are
    /// alphabetical: everything past the hundredth name was gone in silence.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_menu_stops_at_a_length_a_menu_can_be()
    {
        var tree = new Tree();

        // Put in backwards, so a cap applied to the READ would keep f149..f050
        // and this would see the wrong end of the alphabet rather than a count.
        tree.Put(Root, [.. Enumerable.Range(0, 150)
            .Reverse()
            .Select(i => Tree.Dir($"f{i:D3}"))]);

        tree.Put(In("f000"));

        var pane = await Pane(tree, In("f000"));

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(101, crumb.Children.Count);
        Assert.Equal("f000", crumb.Children[0].Name);
        Assert.Equal("f099", crumb.Children[99].Name);

        var told = crumb.Children[^1];

        Assert.Equal("showing the first 100 of 150", told.Name);
        Assert.False(told.Open.CanExecute(null), "a row that says something must not be pressable");
    }

    /// <summary>
    /// The other side of the notice, and the side a `&gt;=` would break: a
    /// folder whose subfolders all fit says nothing about a cap, because there
    /// was none.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_fits_says_nothing_about_a_cap()
    {
        var tree = new Tree();

        tree.Put(Root, [.. Enumerable.Range(0, 100).Select(i => Tree.Dir($"f{i:D3}"))]);
        tree.Put(In("f000"));

        var pane = await Pane(tree, In("f000"));

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(100, crumb.Children.Count);
        Assert.Equal("f099", crumb.Children[^1].Name);
    }

    /// <summary>
    /// **Opening it a second time must not list the folder twice.** The rows go
    /// into a collection the open flyout is bound to, so a fill that appended
    /// would show every folder once more on each press.
    /// </summary>
    [AvaloniaFact]
    public async Task Pressing_a_crumb_menu_twice_does_not_list_everything_twice()
    {
        var pane = await Pane();

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);
        await Press(crumb);

        Assert.Equal(["docs", "pics"], Rows(crumb));
    }

    /// <summary>
    /// **A menu that answered once and kept the answer would go on offering a
    /// folder somebody has since renamed or deleted.** A crumb lives as long as
    /// the bar shows that path, which is as long as you stay in the folder, so
    /// the read is done again on every press — and the guard that stops two
    /// presses overlapping has to let go of the menu when its read lands, or
    /// the second press would be the last one that ever worked.
    /// </summary>
    [AvaloniaFact]
    public async Task A_later_press_asks_the_folder_again()
    {
        var tree = Sample();

        var pane = await Pane(tree);

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        Assert.Equal(["docs", "pics"], Rows(crumb));

        tree.Put(Root, Tree.Dir("docs"), Tree.Dir("later"), Tree.Dir("pics"));

        await Press(crumb);

        Assert.Equal(["docs", "later", "pics"], Rows(crumb));
    }

    /// <summary>
    /// **Two presses left two un-cancellable enumerations in flight.** The read
    /// takes CancellationToken.None, the way the listing's own does, so nothing
    /// takes it back once it is running — and this is the shape the pane has
    /// already measured once, where three refreshes against a folder that had
    /// not answered left three reads going. A chevron is one press away from a
    /// share that has stopped answering, and a press while that menu is still
    /// filling is dropped rather than started again.
    /// </summary>
    [AvaloniaFact]
    public async Task A_press_while_the_menu_is_still_filling_starts_no_second_read()
    {
        var tree = Sample();

        var gate = tree.Hold(Root);

        var pane = await Pane(tree);

        var crumb = CrumbFor(pane, Root);

        crumb.Menu!.Execute(null);

        await Drain();

        crumb.Menu!.Execute(null);

        await Drain();

        gate.SetResult();

        await Drain();

        Assert.Equal(1, tree.Reads(Root));
        Assert.Equal(["docs", "pics"], Rows(crumb));
    }

    /// <summary>
    /// Which is why the guard is keyed by the MENU and not by the folder, the
    /// one place it differs from the guard on the expander triangle.
    ///
    /// **A navigation rebuilds the whole bar**, so the crumb whose read is
    /// still running is gone and its answer goes into a collection nothing is
    /// bound to. Keyed by folder, the crumb that replaced it was refused a read
    /// of its own and sat on "reading…" with nothing coming.
    ///
    /// Measured, not argued: with _crumbMenusFilling declared HashSet&lt;object&gt;
    /// and keyed `Add(folder)`/`Remove(folder)` this test found the seed row
    /// where the two folders should be, and the other twenty-four in this file
    /// stayed green.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_the_bar_rebuilt_is_not_blocked_by_the_old_ones_read()
    {
        var tree = Sample();

        var gate = tree.Hold(Root);

        var pane = await Pane(tree);

        var stale = CrumbFor(pane, Root);

        stale.Menu!.Execute(null);

        await Drain();

        // Away and back: CurrentPath changes twice, and each change rebuilds
        // Breadcrumbs from nothing.
        await pane.NavigateAsync(In("docs"));
        await pane.NavigateAsync(In("docs", "inner"));
        await Drain();

        var fresh = CrumbFor(pane, Root);

        Assert.NotSame(stale, fresh);

        fresh.Menu!.Execute(null);

        await Drain();

        gate.SetResult();

        await Drain();

        Assert.Equal(["docs", "pics"], Rows(fresh));
    }

    /// <summary>
    /// **The fill that was superseded has nowhere to land**, which is why this
    /// read needs no generation re-check where <c>ReadChildrenAsync</c> does:
    /// that one splices rows into the listing on screen, and this one writes
    /// into a collection the rebuilt bar has already thrown away.
    /// </summary>
    [AvaloniaFact]
    public async Task A_fill_that_lands_after_a_navigation_has_nowhere_to_land()
    {
        var tree = Sample();

        var gate = tree.Hold(Root);

        var pane = await Pane(tree);

        var stale = CrumbFor(pane, Root);

        stale.Menu!.Execute(null);

        await Drain();

        await pane.NavigateAsync(In("docs"));
        await Drain();

        gate.SetResult();

        await Drain();

        // It ran to the end and filled the collection it was given.
        Assert.Equal(["docs", "pics"], Rows(stale));

        // And that collection belongs to no crumb the bar is showing.
        Assert.DoesNotContain(pane.Breadcrumbs, c => ReferenceEquals(c, stale));
        Assert.DoesNotContain(pane.Breadcrumbs, c => ReferenceEquals(c.Children, stale.Children));
    }

    /// <summary>
    /// **The popup opens before the read answers**, because the press does both
    /// in one gesture — so a menu that started empty opened as a sliver and then
    /// grew, which is measured in
    /// <see cref="A_menu_with_no_rows_yet_opens_as_a_sliver"/>. And a folder
    /// with no subfolders has to SAY so: an empty popup is indistinguishable
    /// from a broken one.
    ///
    /// Both rows are disabled, because a menu row that lights up under the
    /// pointer is a promise.
    /// </summary>
    [AvaloniaFact]
    public async Task A_crumb_menu_never_opens_empty()
    {
        var tree = new Tree();

        tree.Put(Root, Tree.Dir("bare"));
        tree.Put(In("bare"));

        var pane = await Pane(tree, In("bare"));

        var crumb = CrumbFor(pane, In("bare"));

        Assert.NotEmpty(crumb.Children);

        await Press(crumb);

        var row = Assert.Single(crumb.Children);

        Assert.Equal("no folders in here", row.Name);
        Assert.False(row.Open.CanExecute(null), "a row that says something must not be pressable");
    }

    /// <summary>
    /// **A refusal must not read as an empty folder.** A crumb is one press away
    /// from a permission error on every platform, and answering that with "no
    /// folders in here" is the application stating, in a menu, something it does
    /// not know.
    /// </summary>
    [AvaloniaFact]
    public async Task A_folder_that_cannot_be_read_says_so_rather_than_reading_as_empty()
    {
        var tree = Sample();

        tree.Refuse(Root);

        var pane = await Pane(tree);

        var crumb = CrumbFor(pane, Root);

        await Press(crumb);

        var row = Assert.Single(crumb.Children);

        Assert.Equal("could not read this folder", row.Name);
        Assert.False(row.Open.CanExecute(null), "a row that says something must not be pressable");
    }

    // ---- which crumbs have one ---------------------------------------------

    /// <summary>
    /// The ellipsis stands for several folders at once, so there is no one
    /// folder to list — and it keeps its separator, drawn as plain text, or the
    /// two halves of an elided path would run together as `C:\ … Vaktari`.
    /// </summary>
    [AvaloniaFact]
    public async Task The_ellipsis_crumb_offers_no_menu_and_keeps_its_separator()
    {
        var pane = await Pane();

        var ellipsis = Assert.Single(pane.Breadcrumbs, c => c.IsEllipsis);

        Assert.False(ellipsis.HasMenu, "the ellipsis is not one folder, so it cannot list one");
        Assert.True(ellipsis.ShowPlainSeparator, "the elided path lost the mark between its halves");

        // And the other half of the rule, or the bar draws the mark twice: a
        // crumb that HAS a menu carries its separator inside the button that
        // opens it.
        var ordinary = CrumbFor(pane, Root);

        Assert.True(ordinary.ShowSeparator);
        Assert.False(ordinary.ShowPlainSeparator, "this crumb draws two separators");
    }

    /// <summary>
    /// And the third case, which is neither: a virtual listing gets ONE crumb
    /// naming itself, last and with nothing to enumerate. It has no menu and no
    /// separator, and "no menu" alone must not be read as "draw the mark" —
    /// that would hang a trailing "\" off the end of Recycle Bin.
    /// </summary>
    [AvaloniaFact]
    public void The_one_crumb_of_a_virtual_listing_draws_no_separator()
    {
        var lonely = new PathSegment(
            "Recycle Bin", VirtualPaths.Trash,
            new CommunityToolkit.Mvvm.Input.RelayCommand(() => { }), IsLast: true);

        Assert.False(lonely.HasMenu);
        Assert.False(lonely.ShowSeparator);
        Assert.False(lonely.ShowPlainSeparator, "a trailing mark after a listing that has no path");
    }

    /// <summary>
    /// **This PC is not a directory**, so the crumb that reaches it would have
    /// handed "vaktari:computer" to the filesystem provider and reported that
    /// the machine could not be read. It takes the same branch the listing takes,
    /// which is what makes the OTHER drive reachable from the bar rather than
    /// only from the sidebar.
    /// </summary>
    [AvaloniaFact]
    public async Task The_machine_crumb_lists_the_drives()
    {
        var before = PaneViewModel.Places;

        PaneViewModel.Places = new Drives(
            new Place
            {
                Id = "dev:one",
                Label = "Windows (C:)",
                Path = Root,
                Kind = PlaceKind.Device,
                Icon = "device-desktop",
            });

        _restore.Add(() => PaneViewModel.Places = before);

        var pane = await Pane();

        var crumb = CrumbFor(pane, VirtualPaths.Computer);

        await Press(crumb);

        Assert.Contains("Windows (C:)", Rows(crumb));
    }

    // ---- where the gesture lives -------------------------------------------

    /// <summary>
    /// The half that lives in the markup: the separator after a crumb is a
    /// BUTTON carrying the flyout, and the flyout's rows are the crumb's
    /// children with the children's own commands. A view model that fills a
    /// collection nothing binds is a feature with no way in.
    /// </summary>
    [AvaloniaFact]
    public void The_separator_after_a_crumb_is_what_opens_its_menu()
    {
        var markup = RepoSource.Ui("MainWindow.axaml");

        var at = markup.IndexOf("ItemsSource=\"{Binding ActiveTab.Breadcrumbs}\"",
                                StringComparison.Ordinal);

        Assert.True(at > 0, "the breadcrumb strip is not declared the way this looks for it");

        var template = markup[at..markup.IndexOf("</ItemsControl>", at, StringComparison.Ordinal)];

        Assert.Contains("Command=\"{Binding Menu}\"", template, StringComparison.Ordinal);
        Assert.Contains("IsVisible=\"{Binding HasMenu}\"", template, StringComparison.Ordinal);
        Assert.Contains("<MenuFlyout ItemsSource=\"{Binding Children}\">", template,
                        StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Command\" Value=\"{Binding Open}\"/>", template,
                        StringComparison.Ordinal);
    }

    /// <summary>
    /// The assumption the markup rests on, checked against Avalonia rather than
    /// believed: ONE press on a button that has both a Command and a Flyout runs
    /// the command AND opens the flyout. The command is what fills the menu the
    /// flyout is showing, so if Avalonia ever picked one of the two, the
    /// separator would open a menu that stayed on "reading…" forever.
    /// </summary>
    [AvaloniaFact]
    public void One_press_both_opens_the_flyout_and_fills_it()
    {
        var ran = false;

        var flyout = new MenuFlyout();

        var button = new Button
        {
            Content = "\\",
            Flyout = flyout,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(() => ran = true),
        };

        var window = new Window { Width = 200, Height = 100, Content = button };

        window.Show();
        window.Measure(new Size(200, 100));
        window.Arrange(new Rect(0, 0, 200, 100));

        var at = button.TranslatePoint(
            new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            ?? new Point(0, 0);

        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);

        Assert.True(ran, "the command did not run, so nothing would ever fill the menu");
        Assert.True(flyout.IsOpen, "the flyout did not open, so the filled menu is never shown");

        window.Close();
    }

    /// <summary>
    /// The measurement behind the header template, checked against Avalonia
    /// rather than believed: a menu header that reaches a ContentPresenter as a
    /// bare string is drawn through AccessText, which reads "_" as the marker
    /// before a mnemonic.
    ///
    /// It pins nothing in the source on its own — the two tests below do that.
    /// It is here so the number in the markup's comment can be re-read rather
    /// than trusted.
    /// </summary>
    [AvaloniaFact]
    public void A_name_taken_as_a_label_loses_its_underscore()
    {
        var asWritten = new TextBlock { Text = "git_projects" };
        var eaten = new AccessText { Text = "git_projects" };
        var withoutIt = new TextBlock { Text = "gitprojects" };

        var window = new Window
        {
            Width = 400,
            Height = 200,
            Content = new StackPanel { Children = { asWritten, eaten, withoutIt } },
        };

        window.Show();
        window.Measure(new Size(400, 200));
        window.Arrange(new Rect(0, 0, 400, 200));

        // 168 against 154 on this machine: the underscore is gone from the
        // drawing, and the letter after it has become a mnemonic.
        Assert.Equal(withoutIt.DesiredSize.Width, eaten.DesiredSize.Width);
        Assert.True(eaten.DesiredSize.Width < asWritten.DesiredSize.Width);
        Assert.Equal("p", eaten.AccessKey);

        window.Close();
    }

    /// <summary>
    /// Which is why every MenuItem theme in the window that takes its header
    /// from a name draws that name rather than handing it over as a label.
    ///
    /// Found from the markup rather than listed here, so a fourth menu built
    /// the same way is held to it without anybody remembering — the crumb menu
    /// and the two history flyouts are the same mistake waiting in the same
    /// shape, and folder names are what all three show.
    /// </summary>
    [AvaloniaFact]
    public void Every_menu_that_shows_a_name_draws_it_rather_than_parsing_it()
    {
        var themes = XDocument.Parse(RepoSource.Ui("MainWindow.axaml"))
            .Descendants(Xaml + "ControlTheme")
            .Where(t => (string?)t.Attribute("TargetType") == "MenuItem")
            .Where(t => t.Elements(Xaml + "Setter")
                         .Any(s => (string?)s.Attribute("Property") == "Header"))
            .ToList();

        // A guard, not decoration: a new menu of this shape must fail here
        // rather than quietly drop out of the check below. It has already
        // earned its keep once — the address bar's Recent locations flyout
        // arrived between this test being written and being run, took a folder
        // name as a bare Header, and this line is what said so.
        Assert.Equal(4, themes.Count);

        foreach (var theme in themes)
            Assert.Contains(theme.Elements(Xaml + "Setter"),
                            s => (string?)s.Attribute("Property") == "HeaderTemplate");
    }

    // ---- the real window -----------------------------------------------------

    /// <summary>
    /// **New markup on a path that only runs once somebody presses a chevron is
    /// exactly the kind that ships broken.** Nothing else in this file reaches
    /// the flyout's item container theme at all: every test above reads the view
    /// model's rows, and a Header binding that resolves to nothing is a logged
    /// warning in Avalonia rather than an exception.
    ///
    /// So: the real window, pointed at a real folder, with the flyout really
    /// open and its MenuItems really realized.
    /// </summary>
    [AvaloniaFact]
    public async Task The_real_menu_shows_the_folder_names_as_they_are_written()
    {
        var (window, root) = await RealWindow();

        var rows = await Realized(window, root);

        // The name reaches the row at all. Without the Header setter this is a
        // stack of blank rows, which every view-model test in this file would
        // go on passing over.
        Assert.Equal(["git_projects", "here"], rows.Select(r => r.Header?.ToString()));

        // And it is DRAWN, not parsed. An AccessText here is the underscore
        // eaten and the "p" underlined — see
        // A_name_taken_as_a_label_loses_its_underscore for what that measures.
        Assert.Empty(rows[0].GetVisualDescendants().OfType<AccessText>());

        var drawn = Assert.Single(
            rows[0].GetVisualDescendants().OfType<TextBlock>(), t => t.Text is "git_projects");

        var asWritten = new TextBlock { Text = "git_projects", FontSize = drawn.FontSize };

        asWritten.Measure(new Size(400, 40));

        Assert.Equal(asWritten.DesiredSize.Width, drawn.DesiredSize.Width);
    }

    /// <summary>
    /// The glyph on the button, which is the whole visible affordance on the
    /// two crumbs the change is most for: a root carries its separator inside
    /// its own name and the folder you are in has none after it, so both would
    /// otherwise be a 4px pressable nothing.
    ///
    /// And exactly ONE mark per crumb — the button holds the separator where
    /// there is one, so the plain TextBlock beside it must stay away or the bar
    /// draws two.
    /// </summary>
    [AvaloniaFact]
    public async Task The_crumb_with_no_separator_gets_a_chevron_instead()
    {
        var (window, root) = await RealWindow();

        var here = Chevron(window, Path.Combine(root, "here"));
        var above = Chevron(window, root);

        Assert.True(Arrow(here).IsVisible, "the folder you are in has no way in at all");
        Assert.False(Mark(here).IsVisible, "a crumb with no separator drew one anyway");

        Assert.False(Arrow(above).IsVisible, "an ordinary crumb grew a second glyph");
        Assert.True(Mark(above).IsVisible, "the separator between two crumbs is gone");

        Assert.False(Trailing(above).IsVisible, "this crumb draws its separator twice");
        Assert.False(Trailing(here).IsVisible);

        // A visible Path with no geometry, no stroke or no size is a pressable
        // 4px nothing, which is what this crumb would be without the glyph.
        Assert.NotNull(Arrow(here).Data);
        Assert.NotNull(Arrow(here).Stroke);
        Assert.Equal(9d, Arrow(here).Width);

        Assert.Equal(PathSegment.Separator, Mark(above).Text);
    }

    /// <summary>
    /// **Every chevron said the same thing.** The literal "What is inside this
    /// folder" was on all ten buttons of an ordinary path, so a screen reader
    /// walking the bar heard one sentence ten times with nothing in it naming
    /// the folder that would open — while the name button an inch to the left
    /// has said its own name all along.
    /// </summary>
    [AvaloniaFact]
    public async Task Each_chevron_says_which_folder_it_would_open()
    {
        var (window, root) = await RealWindow();

        var chevrons = window.GetVisualDescendants()
            .OfType<Button>()
            .Where(b => b.Flyout is MenuFlyout && b.DataContext is PathSegment)
            .ToList();

        // The bar under a temp path is ten crumbs deep, so this is a real
        // sample rather than the one crumb that happens to work.
        Assert.True(chevrons.Count > 3, $"only {chevrons.Count} chevrons were realized");

        foreach (var chevron in chevrons)
        {
            var said = $"What is inside {((PathSegment)chevron.DataContext!).Name}";

            Assert.Equal(said, AutomationProperties.GetName(chevron));

            // And the same for the pointer, or hovering two chevrons in a row
            // tells you the same nothing twice.
            Assert.Equal(said, ToolTip.GetTip(chevron));
        }
    }

    /// <summary>
    /// The measurement behind the "reading…" row: the press opens the popup and
    /// starts the read in one gesture, so the flyout is on screen before the
    /// first row exists.
    ///
    /// Measured here: bound to an empty collection it laid out 2 by 32, and
    /// jumped to 216 by 64 when the two real rows landed. It pins nothing on
    /// its own — A_crumb_menu_never_opens_empty is what fails if the seed row
    /// goes — it is here so the numbers in the source comment can be re-read.
    /// </summary>
    [AvaloniaFact]
    public async Task A_menu_with_no_rows_yet_opens_as_a_sliver()
    {
        var (window, root) = await RealWindow();

        var chevron = Chevron(window, root);
        var crumb = (PathSegment)chevron.DataContext!;
        var flyout = (MenuFlyout)chevron.Flyout!;

        // What a crumb really carries before anybody presses it.
        Assert.Equal("reading…", Assert.Single(crumb.Children).Name);

        crumb.Children.Clear();

        flyout.ShowAt(chevron);

        await Layout(window);

        var popup = (Control)flyout.Popup.Child!;
        var empty = popup.Bounds;

        chevron.Command!.Execute(null);

        await Layout(window);

        Assert.Equal(2, crumb.Children.Count);
        Assert.True(popup.Bounds.Width > empty.Width * 4,
                    $"the empty menu was already {empty.Width} wide, so nothing jumped");
        Assert.True(popup.Bounds.Height > empty.Height);
    }

    // ---- driving the real window ---------------------------------------------

    /// <summary>
    /// A real MainWindow in a real folder, pointed at `&lt;root&gt;/here` with a
    /// sibling whose name carries an underscore.
    ///
    /// The window and the folder are torn down by <see cref="Dispose"/>, and
    /// the search backend is borrowed and given back — the constructor assigns
    /// the platform's own, and a later test that loads a search listing would
    /// otherwise walk the machine for real.
    /// </summary>
    private async Task<(MainWindow Window, string Root)> RealWindow()
    {
        UseSearch(PaneViewModel.Search);

        var places = PaneViewModel.Places;

        _restore.Add(() => PaneViewModel.Places = places);

        var root = Path.Combine(
            Path.GetTempPath(), "vaktari-crumb-real-" + Guid.NewGuid().ToString("N")[..8]);

        Directory.CreateDirectory(Path.Combine(root, "git_projects"));
        Directory.CreateDirectory(Path.Combine(root, "here"));

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

        await shell.ActiveTab!.NavigateAsync(Path.Combine(root, "here"));
        await Layout(window);

        return (window, root);
    }

    /// <summary>Runs the queue, then measures and arranges — a realized flyout
    /// has to be laid out before anything in it has a size.</summary>
    private static async Task Layout(Window window)
    {
        for (var i = 0; i < 40; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(1);
        }

        window.Measure(new Size(1400, 900));
        window.Arrange(new Rect(0, 0, 1400, 900));

        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The button that opens one crumb's menu.</summary>
    private static Button Chevron(Window window, string path)
        => Assert.Single(
            window.GetVisualDescendants().OfType<Button>(),
            b => b.Flyout is MenuFlyout && (b.DataContext as PathSegment)?.FullPath == path);

    private static Avalonia.Controls.Shapes.Path Arrow(Button chevron)
        => chevron.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();

    private static TextBlock Mark(Button chevron)
        => chevron.GetVisualDescendants().OfType<TextBlock>().Single();

    /// <summary>The plain separator drawn beside the button rather than in
    /// it — the one the ellipsis keeps.</summary>
    private static TextBlock Trailing(Button chevron)
        => ((Panel)chevron.Parent!).Children.OfType<TextBlock>().Single();

    /// <summary>Presses one crumb's chevron for real and hands back the
    /// MenuItems the flyout realized.</summary>
    private static async Task<List<MenuItem>> Realized(Window window, string path)
    {
        var chevron = Chevron(window, path);

        chevron.Command!.Execute(null);

        await Layout(window);

        var flyout = (MenuFlyout)chevron.Flyout!;

        flyout.ShowAt(chevron);

        await Layout(window);

        return [.. ((Visual)flyout.Popup.Child!).GetVisualDescendants().OfType<MenuItem>()];
    }

    // ---- the fakes -----------------------------------------------------------

    /// <summary>
    /// A filesystem of folders and files. Anything not registered enumerates as
    /// nothing, the way a folder that is not there does.
    /// </summary>
    private sealed class Tree : IFileSystemProvider
    {
        private readonly Dictionary<string, List<(string Name, EntryFlags Flags)>>
            _folders = new(StringComparer.Ordinal);

        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);

        private readonly Dictionary<string, TaskCompletionSource> _held = new(StringComparer.Ordinal);

        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        public static (string, EntryFlags) Dir(string name) => (name, EntryFlags.Directory);

        public static (string, EntryFlags) Hidden(string name)
            => (name, EntryFlags.Directory | EntryFlags.Hidden);

        public static (string, EntryFlags) File(string name) => (name, EntryFlags.None);

        public void Put(string folder, params (string Name, EntryFlags Flags)[] rows)
            => _folders[folder] = [.. rows];

        /// <summary>Makes reading this folder throw, the way one you have no
        /// rights to does.</summary>
        public void Refuse(string folder) => _refused.Add(folder);

        /// <summary>
        /// Parks every read of this folder until the returned source is set —
        /// a share that has stopped answering, which is the only condition
        /// under which two presses can overlap at all.
        /// </summary>
        public TaskCompletionSource Hold(string folder)
            => _held[folder] = new TaskCompletionSource();

        /// <summary>How many enumerations of this folder actually started.</summary>
        public int Reads(string folder) => _reads.GetValueOrDefault(folder);

        public async IAsyncEnumerable<IReadOnlyList<FileEntry>> EnumerateAsync(
            string path, ListingOptions options,
            [EnumeratorCancellation] CancellationToken ct)
        {
            _reads[path] = _reads.GetValueOrDefault(path) + 1;

            await Task.Yield();

            if (_held.TryGetValue(path, out var gate)) await gate.Task;

            if (_refused.Contains(path)) throw new UnauthorizedAccessException(path);

            if (!_folders.TryGetValue(path, out var rows)) yield break;

            yield return
            [
                .. rows
                    .Where(r => options.IncludeHidden || (r.Flags & EntryFlags.Hidden) == 0)
                    .Select(r => new FileEntry(
                        r.Name, Path.Combine(path, r.Name), 0,
                        DateTimeOffset.UnixEpoch, r.Flags)),
            ];
        }

        public ValueTask<FileEntry?> GetEntryAsync(string path, CancellationToken ct)
            => ValueTask.FromResult<FileEntry?>(null);

        public IDisposable Watch(string path, Action<FileSystemChange> onChange) => new Nothing();

        public ValueTask<bool> IsReachableAsync(string path, TimeSpan timeout, CancellationToken ct)
            => ValueTask.FromResult(true);

        public string Combine(string basePath, string name) => Path.Combine(basePath, name);
        public string? GetParent(string path) => PathRules.Parent(path);
        public bool IsCaseSensitive => false;

        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }

    /// <summary>The places provider behind This PC, holding whatever this test
    /// wants the machine to have on it.</summary>
    private sealed class Drives(params Place[] places) : IPlacesProvider
    {
        public event EventHandler? PlacesChanged { add { } remove { } }

        public string? NameFor(string path) => null;

        public ValueTask<IReadOnlyList<PlaceGroup>> GetPlacesAsync(CancellationToken ct)
            => ValueTask.FromResult<IReadOnlyList<PlaceGroup>>([new("DEVICES", places)]);

        public ValueTask PinAsync(string path, string? label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask UnpinAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask RenameAsync(string id, string label, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask ReorderAsync(IReadOnlyList<string> ids, CancellationToken ct)
            => ValueTask.CompletedTask;
        public ValueTask MountAsync(string id, CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask<EjectResult> EjectAsync(string id, CancellationToken ct)
            => ValueTask.FromResult(EjectResult.InUse("nothing to eject"));
        public ValueTask<int> ImportExistingAsync(CancellationToken ct)
            => ValueTask.FromResult(0);
    }
}
